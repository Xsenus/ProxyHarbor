using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

// Имена DTO-полей намеренно совпадают с self-documenting OpenAPI contract.
#pragma warning disable CS1591

/// <summary>Публичный каталог проверенных VPN endpoint и готовых ссылок подключения.</summary>
[ApiController, Route("api/v1/vpn"), EnableRateLimiting("public")]
public sealed class VpnController(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IFreeExportAccessService accessService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Конструктор тестов старого контракта: явно предоставляет полный доступ.</summary>
    internal VpnController(IDbContextFactory<ProxyHarborDbContext> testDbFactory)
        : this(testDbFactory, AlwaysPaidAccessService.Instance) { }

    /// <summary>Возвращает страницу VPN endpoint; бесплатный тариф получает смешанные 10 записей.</summary>
    [HttpGet]
    [OutputCache(PolicyName = PublicOutputCachePolicies.VpnCatalog)]
    public async Task<ActionResult<PagedResult<VpnEndpointResponse>>> Get(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] VpnProtocol? protocol = null, [FromQuery] VpnEndpointStatus? status = null,
        [FromQuery] string[]? country = null,
        CancellationToken token = default)
    {
        if (!TryNormalizeCountries(country, out var countries)) return InvalidCountries();
        page = Math.Clamp(page, 1, 100_000); pageSize = Math.Clamp(pageSize, 10, 100);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        // Публичный каталог обязан быть непосредственно пригоден для использования:
        // каждая строка имеет готовый импортируемый URI и уже определённую страну.
        // Неполные/необогащённые endpoint остаются доступны только администратору.
        var query = db.VpnEndpoints.AsNoTracking()
            .Where(x => x.ConnectionUri != null && x.CountryCode != null);
        if (protocol.HasValue) query = query.Where(x => x.Protocol == protocol.Value);
        // Не задаём enum как optional-значение параметра: Microsoft.AspNetCore.OpenApi 10
        // пытается сериализовать boxed int как Nullable<VpnEndpointStatus> и роняет
        // /openapi/v1.json. Поведение по умолчанию сохраняем внутри метода.
        var effectiveStatus = status ?? VpnEndpointStatus.Reachable;
        query = query.Where(x => x.Status == effectiveStatus);
        if (countries.Length > 0) query = query.Where(x => x.CountryCode != null && countries.Contains(x.CountryCode));
        var total = await query.CountAsync(token);
        var paid = await accessService.HasPaidAccessAsync(CurrentUser, token);
        var effectivePage = paid ? page : 1;
        var effectivePageSize = paid ? pageSize : FreeExportAccessService.FreeVpnLimit;
        VpnEndpointResponse[] items;
        if (paid)
        {
            items = await Ordered(query).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(ToResponse()).ToArrayAsync(token);
        }
        else
        {
            var candidates = await Ordered(query).Take(FreeCatalogSelector.CandidatePoolSize).ToArrayAsync(token);
            items = FreeCatalogSelector.Select(candidates,
                    x => $"{x.Protocol}:{x.Host}:{x.Port}", x => x.CountryCode,
                    FreeExportAccessService.FreeVpnLimit, DateTimeOffset.UtcNow)
                .OrderBy(x => x.LatencyMs == null).ThenBy(x => x.LatencyMs)
                .ThenByDescending(x => x.SuccessfulChecks)
                .Select(x => new VpnEndpointResponse(x.Id, x.Host, x.Port, x.CountryCode, x.Protocol,
                    x.Transport, x.Status, x.LatencyMs, x.FirstSeenAt, x.LastSeenAt, x.LastCheckedAt,
                    x.SuccessfulChecks, x.FailedChecks, x.ConnectionUri))
                .ToArray();
        }
        return Ok(new PagedResult<VpnEndpointResponse>(items, effectivePage, effectivePageSize, total)
        {
            FullAccess = paid,
            Accessible = paid ? null : Math.Min(total, FreeExportAccessService.FreeVpnLimit),
            Limited = !paid,
            Message = paid ? null : FreeExportAccessService.GetVpnUpgradeMessage(Language, total),
            UpgradeUrl = paid ? null : "/account"
        });
    }

    /// <summary>Возвращает страны доступных VPN endpoint для фирменного фильтра.</summary>
    [HttpGet("countries")]
    [OutputCache(PolicyName = PublicOutputCachePolicies.Countries)]
    public async Task<ActionResult<IReadOnlyList<ProxyCountryDto>>> Countries(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var rows = await db.VpnEndpoints.AsNoTracking()
            .Where(x => x.Status == VpnEndpointStatus.Reachable && x.CountryCode != null && x.ConnectionUri != null)
            .GroupBy(x => x.CountryCode!)
            .Select(group => new ProxyCountryDto(group.Key, group.Count()))
            .ToArrayAsync(token);
        return Ok(rows.OrderByDescending(x => x.Count).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Экспортирует готовые VPN URI в JSON либо TXT с явным описанием тарифа.</summary>
    [HttpGet("export/{format}")]
    [EnableRateLimiting("export")]
    public async Task<IActionResult> Export(
        string format,
        [FromQuery] VpnProtocol? protocol = null,
        [FromQuery] string[]? country = null,
        [FromQuery] int limit = 1_000,
        CancellationToken token = default)
    {
        var normalizedFormat = format.ToLowerInvariant();
        if (normalizedFormat is not ("json" or "txt"))
            return Problem("VPN export поддерживает json и txt.", statusCode: StatusCodes.Status400BadRequest);
        if (!TryNormalizeCountries(country, out var countries)) return InvalidCountries();
        limit = Math.Clamp(limit, 1, 5_000);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var query = db.VpnEndpoints.AsNoTracking().Where(x =>
            x.Status == VpnEndpointStatus.Reachable && x.ConnectionUri != null && x.CountryCode != null);
        if (protocol.HasValue) query = query.Where(x => x.Protocol == protocol.Value);
        if (countries.Length > 0) query = query.Where(x => x.CountryCode != null && countries.Contains(x.CountryCode));
        var total = await query.CountAsync(token);
        var paid = await accessService.HasPaidAccessAsync(CurrentUser, token);
        var effectiveLimit = paid ? limit : FreeExportAccessService.FreeVpnLimit;
        var skip = paid ? 0 : Math.Max(0, (total - effectiveLimit) / 2);
        var items = await Ordered(query).Skip(skip).Take(effectiveLimit).Select(ToResponse()).ToArrayAsync(token);
        var limited = !paid && total > effectiveLimit;
        var message = paid ? null : FreeExportAccessService.GetVpnUpgradeMessage(Language, total);
        Response.Headers["X-Access-Tier"] = paid ? "paid" : "free";
        Response.Headers["X-Catalog-Total"] = total.ToString(CultureInfo.InvariantCulture);
        Response.Headers["X-Export-Limit"] = effectiveLimit.ToString(CultureInfo.InvariantCulture);
        if (!paid) Response.Headers["Link"] = "</account>; rel=\"upgrade\"";
        if (normalizedFormat == "json")
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                access = new { tier = paid ? "paid" : "free", limited, accessible = items.Length, total, message, upgradeUrl = paid ? null : "/account" },
                vpn = items
            }, JsonOptions);
            return File(body, "application/json; charset=utf-8", "vpn-configurations.json");
        }
        var text = new StringBuilder();
        if (message is not null) text.Append("# ").AppendLine(message).Append("# total: ").AppendLine(total.ToString(CultureInfo.InvariantCulture));
        foreach (var item in items) text.AppendLine(item.ConnectionUri ?? $"{item.Host}:{item.Port}");
        return File(Encoding.UTF8.GetBytes(text.ToString()), "text/plain; charset=utf-8", "vpn-configurations.txt");
    }

    [HttpGet("sources")]
    public ActionResult<object> Sources() => Ok(new
    {
        lastAuditedOn = BuiltInVpnSourceCatalog.LastAuditedOn,
        feedCount = BuiltInVpnSourceCatalog.Sources.Count,
        providers = BuiltInVpnSourceCatalog.Sources.Select(x => x.Provider).Distinct().Count(),
        protocols = Enum.GetNames<VpnProtocol>()
    });

    private ClaimsPrincipal CurrentUser => ControllerContext.HttpContext?.User ?? new ClaimsPrincipal();
    private static string Language => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    private static IOrderedQueryable<VpnEndpoint> Ordered(IQueryable<VpnEndpoint> query) =>
        query.OrderBy(x => x.LatencyMs == null).ThenBy(x => x.LatencyMs).ThenByDescending(x => x.SuccessfulChecks).ThenBy(x => x.Id);
    private static System.Linq.Expressions.Expression<Func<VpnEndpoint, VpnEndpointResponse>> ToResponse() => x =>
        new VpnEndpointResponse(x.Id, x.Host, x.Port, x.CountryCode, x.Protocol, x.Transport, x.Status,
            x.LatencyMs, x.FirstSeenAt, x.LastSeenAt, x.LastCheckedAt, x.SuccessfulChecks, x.FailedChecks,
            x.ConnectionUri);
    private static bool TryNormalizeCountries(string[]? values, out string[] countries)
    {
        countries = (values ?? []).SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(x => x.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        return countries.All(x => x.Length == 2 && x.All(char.IsAsciiLetter));
    }
    private BadRequestObjectResult InvalidCountries() => BadRequest(new ProblemDetails
    {
        Title = "Некорректная страна",
        Detail = "Используйте двухбуквенные ISO-коды стран.",
        Status = 400
    });

    private sealed class AlwaysPaidAccessService : IFreeExportAccessService
    {
        internal static AlwaysPaidAccessService Instance { get; } = new();
        public Task<FreeExportAccess> AcquireAsync(ClaimsPrincipal principal, string? remoteIp, CancellationToken cancellationToken) =>
            Task.FromResult(new FreeExportAccess(true, true, int.MaxValue, null, "paid"));
        public Task<bool> HasPaidAccessAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}

public sealed record VpnEndpointResponse(Guid Id, string Host, int Port, string? CountryCode, VpnProtocol Protocol, string Transport,
    VpnEndpointStatus Status, int? LatencyMs, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt,
    DateTimeOffset? LastCheckedAt, int SuccessfulChecks, int FailedChecks, string? ConnectionUri);

/// <summary>Административное управление VPN-каталогом и его источниками.</summary>
[ApiController, Route("api/v1/admin/vpn"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
public sealed class AdminVpnController(IDbContextFactory<ProxyHarborDbContext> dbFactory, VpnCatalogService catalog) : ControllerBase
{
    private static readonly string[] AllowedEndpointSorts =
        ["address", "protocol", "status", "latency", "quality", "firstSeen", "lastSeen", "lastChecked"];

    /// <summary>Возвращает страницу VPN-источников.</summary>
    [HttpGet("sources")]
    public async Task<ActionResult<PagedResult<AdminVpnSourceResponse>>> Sources(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] string? search = null,
        CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000); pageSize = Math.Clamp(pageSize, 10, 100); search = search?.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var query = db.VpnSources.AsNoTracking();
        if (!string.IsNullOrEmpty(search)) query = query.Where(x => x.Name.Contains(search) || x.Provider.Contains(search) || x.Url.Contains(search));
        var total = await query.CountAsync(token);
        var builtIns = BuiltInVpnSourceCatalog.Sources.Select(x => x.Url).ToHashSet(StringComparer.Ordinal);
        var entities = await query.OrderBy(x => x.Priority).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        var items = entities.Select(x => new AdminVpnSourceResponse(x.Id, x.Name, x.Provider, x.Url, x.DefaultProtocol,
            x.Enabled, x.Priority, x.License, x.LastFetchedAt, x.LastSucceededAt, x.LastItemCount,
            x.ConsecutiveFailures, x.LastError, builtIns.Contains(x.Url))).ToArray();
        return Ok(new PagedResult<AdminVpnSourceResponse>(items, page, pageSize, total));
    }

    [HttpPost("sources")]
    public async Task<ActionResult<AdminVpnSourceResponse>> Add([FromBody] SaveVpnSourceRequest request, CancellationToken token)
    {
        var problem = await ValidateRequestAsync(request, token); if (problem is not null) return problem;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        if (await db.VpnSources.AnyAsync(x => x.Url == request.Url, token)) return Conflict(new ProblemDetails { Detail = "Такой VPN feed уже существует." });
        var source = new VpnSource
        {
            Name = request.Name.Trim(),
            Provider = request.Provider.Trim(),
            Url = request.Url,
            DefaultProtocol = request.Protocol,
            Enabled = request.Enabled,
            Priority = request.Priority,
            License = request.License.Trim()
        };
        db.VpnSources.Add(source); await db.SaveChangesAsync(token);
        return Created($"/api/v1/admin/vpn/sources/{source.Id}", Map(source, false));
    }

    [HttpPut("sources/{id:guid}")]
    public async Task<ActionResult<AdminVpnSourceResponse>> Update(Guid id, [FromBody] SaveVpnSourceRequest request, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var source = await db.VpnSources.SingleOrDefaultAsync(x => x.Id == id, token); if (source is null) return NotFound();
        var builtIn = BuiltInVpnSourceCatalog.Sources.Any(x => x.Url == source.Url);
        if (builtIn) { source.Enabled = request.Enabled; }
        else
        {
            var problem = await ValidateRequestAsync(request, token); if (problem is not null) return problem;
            source.Name = request.Name.Trim(); source.Provider = request.Provider.Trim(); source.Url = request.Url;
            source.DefaultProtocol = request.Protocol; source.Enabled = request.Enabled; source.Priority = request.Priority; source.License = request.License.Trim();
        }
        await db.SaveChangesAsync(token); return Ok(Map(source, builtIn));
    }

    [HttpDelete("sources/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var source = await db.VpnSources.SingleOrDefaultAsync(x => x.Id == id, token); if (source is null) return NotFound();
        if (BuiltInVpnSourceCatalog.Sources.Any(x => x.Url == source.Url)) { source.Enabled = false; await db.SaveChangesAsync(token); return NoContent(); }
        db.VpnSources.Remove(source); await db.SaveChangesAsync(token); return NoContent();
    }

    [HttpGet("endpoints")]
    public async Task<ActionResult<AdminVpnEndpointPage>> Endpoints(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] VpnProtocol? protocol = null,
        [FromQuery] VpnEndpointStatus? status = null,
        [FromQuery] string? transport = null,
        [FromQuery] string? country = null,
        [FromQuery] string? query = null,
        [FromQuery] string sort = "lastChecked",
        [FromQuery] string order = "desc",
        CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        transport = transport?.Trim().ToLowerInvariant();
        country = country?.Trim().ToUpperInvariant();
        query = query?.Trim();
        order = order.Trim().ToLowerInvariant();

        if (transport is { Length: > 0 } && transport is not ("tcp" or "udp"))
            return Problem("Транспорт должен быть TCP или UDP.", statusCode: 400);
        if (country is { Length: > 0 } && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
            return Problem("Страна должна быть двухбуквенным ISO-кодом.", statusCode: 400);
        if (query is { Length: > 128 })
            return Problem("Поисковая строка не может быть длиннее 128 символов.", statusCode: 400);
        if (!AllowedEndpointSorts.Contains(sort, StringComparer.Ordinal) || order is not ("asc" or "desc"))
            return Problem("Неизвестный порядок сортировки.", statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var all = db.VpnEndpoints.AsNoTracking();
        var rawSummary = await all.GroupBy(_ => 1).Select(group => new
        {
            Total = group.Count(),
            Reachable = group.Count(item => item.Status == VpnEndpointStatus.Reachable),
            Pending = group.Count(item => item.Status == VpnEndpointStatus.Pending),
            Unreachable = group.Count(item => item.Status == VpnEndpointStatus.Unreachable),
            Unsupported = group.Count(item => item.Status == VpnEndpointStatus.UnsupportedTransport),
            EverReachable = group.Count(item => item.SuccessfulChecks > 0),
            Countries = group.Where(item => item.CountryCode != null).Select(item => item.CountryCode).Distinct().Count(),
            AverageReachableLatencyMs = group.Where(item => item.Status == VpnEndpointStatus.Reachable && item.LatencyMs != null)
                .Average(item => (double?)item.LatencyMs),
            OldestReachableAt = group.Where(item => item.Status == VpnEndpointStatus.Reachable)
                .Min(item => (DateTimeOffset?)item.FirstSeenAt)
        }).SingleOrDefaultAsync(token);
        var countries = await all.Where(item => item.CountryCode != null)
            .GroupBy(item => item.CountryCode!)
            .Select(group => new { Code = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count).ThenBy(item => item.Code)
            .ToArrayAsync(token);

        var filtered = all;
        if (protocol.HasValue) filtered = filtered.Where(item => item.Protocol == protocol.Value);
        if (status.HasValue) filtered = filtered.Where(item => item.Status == status.Value);
        if (!string.IsNullOrEmpty(transport)) filtered = filtered.Where(item => item.Transport == transport);
        if (!string.IsNullOrEmpty(country)) filtered = filtered.Where(item => item.CountryCode == country);
        if (!string.IsNullOrEmpty(query))
        {
            var search = query;
            filtered = filtered.Where(item => item.Host.Contains(search));
        }

        var total = await filtered.CountAsync(token);
        var ascending = order == "asc";
        var ordered = (sort, ascending) switch
        {
            ("address", true) => filtered.OrderBy(item => item.Host).ThenBy(item => item.Port),
            ("address", false) => filtered.OrderByDescending(item => item.Host).ThenByDescending(item => item.Port),
            ("protocol", true) => filtered.OrderBy(item => item.Protocol).ThenBy(item => item.Host),
            ("protocol", false) => filtered.OrderByDescending(item => item.Protocol).ThenBy(item => item.Host),
            ("status", true) => filtered.OrderBy(item => item.Status).ThenBy(item => item.Host),
            ("status", false) => filtered.OrderByDescending(item => item.Status).ThenBy(item => item.Host),
            ("latency", true) => filtered.OrderBy(item => item.LatencyMs == null).ThenBy(item => item.LatencyMs).ThenBy(item => item.Id),
            ("latency", false) => filtered.OrderBy(item => item.LatencyMs == null).ThenByDescending(item => item.LatencyMs).ThenBy(item => item.Id),
            ("quality", true) => filtered.OrderBy(item => item.SuccessfulChecks + item.FailedChecks == 0
                    ? -1.0 : item.SuccessfulChecks * 1.0 / (item.SuccessfulChecks + item.FailedChecks))
                .ThenBy(item => item.Id),
            ("quality", false) => filtered.OrderByDescending(item => item.SuccessfulChecks + item.FailedChecks == 0
                    ? -1.0 : item.SuccessfulChecks * 1.0 / (item.SuccessfulChecks + item.FailedChecks))
                .ThenBy(item => item.Id),
            ("firstSeen", true) => filtered.OrderBy(item => item.FirstSeenAt).ThenBy(item => item.Id),
            ("firstSeen", false) => filtered.OrderByDescending(item => item.FirstSeenAt).ThenBy(item => item.Id),
            ("lastSeen", true) => filtered.OrderBy(item => item.LastSeenAt).ThenBy(item => item.Id),
            ("lastSeen", false) => filtered.OrderByDescending(item => item.LastSeenAt).ThenBy(item => item.Id),
            (_, true) => filtered.OrderBy(item => item.LastCheckedAt == null).ThenBy(item => item.LastCheckedAt).ThenBy(item => item.Id),
            _ => filtered.OrderBy(item => item.LastCheckedAt == null).ThenByDescending(item => item.LastCheckedAt).ThenBy(item => item.Id)
        };
        var entities = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        var summary = rawSummary is null
            ? new AdminVpnSummary(0, 0, 0, 0, 0, 0, null, 0, null)
            : new AdminVpnSummary(rawSummary.Total, rawSummary.Reachable, rawSummary.Pending,
                rawSummary.Unreachable, rawSummary.Unsupported, rawSummary.EverReachable,
                rawSummary.AverageReachableLatencyMs is null ? null : (int)Math.Round(rawSummary.AverageReachableLatencyMs.Value),
                rawSummary.Countries, rawSummary.OldestReachableAt is null ? null : Math.Max(0, (long)(now - rawSummary.OldestReachableAt.Value).TotalSeconds));

        return Ok(new AdminVpnEndpointPage(entities.Select(item => AdminVpnEndpointItem.From(item, now)).ToArray(),
            page, pageSize, total, summary, countries.Select(item => new AdminVpnCountry(item.Code, item.Count)).ToArray()));
    }

    [HttpPost("collect")]
    public async Task<ActionResult<VpnCollectionResult>> Collect(CancellationToken token)
    {
        try { return Ok(await catalog.CollectAsync(forceAllSources: true, token: token)); }
        catch (OperationAlreadyRunningException exception) { return Conflict(new ProblemDetails { Detail = exception.Message }); }
    }
    [HttpPost("validate")]
    public async Task<ActionResult<VpnValidationResult>> Validate(CancellationToken token)
    {
        try { return Ok(await catalog.ValidateAsync(token)); }
        catch (OperationAlreadyRunningException exception) { return Conflict(new ProblemDetails { Detail = exception.Message }); }
    }

    private static AdminVpnSourceResponse Map(VpnSource x, bool builtIn) => new(x.Id, x.Name, x.Provider, x.Url,
        x.DefaultProtocol, x.Enabled, x.Priority, x.License, x.LastFetchedAt, x.LastSucceededAt, x.LastItemCount,
        x.ConsecutiveFailures, x.LastError, builtIn);

    private async Task<ActionResult?> ValidateRequestAsync(SaveVpnSourceRequest request, CancellationToken token)
    {
        if (request.Name.Trim().Length is < 2 or > 120 || request.Provider.Trim().Length is < 2 or > 120 ||
            request.License.Trim().Length is < 2 or > 80 || request.Priority is < -10000 or > 10000)
            return BadRequest(new ProblemDetails { Detail = "Проверьте название, провайдера, лицензию и приоритет." });
        if (!NetworkSafety.TryParseSafeHttpsUrl(request.Url, out var uri) || !await NetworkSafety.IsSafePublicHttpsUrlAsync(uri.AbsoluteUri, token))
            return BadRequest(new ProblemDetails { Detail = "VPN feed должен быть безопасным публичным HTTPS URL." });
        request.Url = uri.AbsoluteUri; return null;
    }
}

public sealed class SaveVpnSourceRequest
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Url { get; set; } = "";
    public VpnProtocol Protocol { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string License { get; set; } = "Public repository";
}

public sealed record AdminVpnSourceResponse(Guid Id, string Name, string Provider, string Url, VpnProtocol DefaultProtocol,
    bool Enabled, int Priority, string License, DateTimeOffset? LastFetchedAt, DateTimeOffset? LastSucceededAt,
    int LastItemCount, int ConsecutiveFailures, string? LastError, bool IsBuiltIn);

public sealed record AdminVpnEndpointPage(IReadOnlyList<AdminVpnEndpointItem> Items, int Page, int PageSize, int Total,
    AdminVpnSummary Summary, IReadOnlyList<AdminVpnCountry> Countries);

public sealed record AdminVpnSummary(int Total, int Reachable, int Pending, int Unreachable, int UnsupportedTransport,
    int EverReachable, int? AverageReachableLatencyMs, int Countries, long? LongestKnownSeconds);

public sealed record AdminVpnCountry(string Code, int Count);

public sealed record AdminVpnEndpointItem(Guid Id, string Host, int Port, string? CountryCode, VpnProtocol Protocol,
    string Transport, VpnEndpointStatus Status, int? LatencyMs, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt,
    DateTimeOffset? LastCheckedAt, DateTimeOffset? NextCheckAt, int SuccessfulChecks, int FailedChecks,
    decimal SuccessRate, long KnownForSeconds, string? LastError, string? ConnectionUri)
{
    public static AdminVpnEndpointItem From(VpnEndpoint item, DateTimeOffset now)
    {
        var checks = item.SuccessfulChecks + item.FailedChecks;
        var successRate = checks == 0 ? 0 : Math.Round(item.SuccessfulChecks * 100m / checks, 1);
        return new(item.Id, item.Host, item.Port, item.CountryCode, item.Protocol, item.Transport, item.Status,
            item.LatencyMs, item.FirstSeenAt, item.LastSeenAt, item.LastCheckedAt, item.NextCheckAt,
            item.SuccessfulChecks, item.FailedChecks, successRate,
            Math.Max(0, (long)(now - item.FirstSeenAt).TotalSeconds), item.LastError, item.ConnectionUri);
    }
}
#pragma warning restore CS1591
