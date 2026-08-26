using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

// Имена DTO-полей намеренно совпадают с self-documenting OpenAPI contract.
#pragma warning disable CS1591

/// <summary>Публичный безопасный каталог VPN endpoint без UUID, паролей и ключей.</summary>
[ApiController, Route("api/v1/vpn"), EnableRateLimiting("public")]
public sealed class VpnController(IDbContextFactory<ProxyHarborDbContext> dbFactory) : ControllerBase
{
    /// <summary>Возвращает страницу VPN endpoint; по умолчанию только TCP-доступные.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<VpnEndpointResponse>>> Get(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] VpnProtocol? protocol = null, [FromQuery] VpnEndpointStatus? status = VpnEndpointStatus.Reachable,
        CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000); pageSize = Math.Clamp(pageSize, 10, 100);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var query = db.VpnEndpoints.AsNoTracking();
        if (protocol.HasValue) query = query.Where(x => x.Protocol == protocol.Value);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var total = await query.CountAsync(token);
        var items = await query.OrderBy(x => x.LatencyMs == null).ThenBy(x => x.LatencyMs).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new VpnEndpointResponse(x.Id, x.Host, x.Port, x.Protocol, x.Transport, x.Status,
                x.LatencyMs, x.FirstSeenAt, x.LastSeenAt, x.LastCheckedAt, x.SuccessfulChecks, x.FailedChecks,
                x.FirstSource == null ? null : x.FirstSource.Name, x.FirstSource == null ? null : x.FirstSource.Url))
            .ToArrayAsync(token);
        return Ok(new PagedResult<VpnEndpointResponse>(items, page, pageSize, total));
    }

    [HttpGet("sources")]
    public ActionResult<object> Sources() => Ok(new
    {
        lastAuditedOn = BuiltInVpnSourceCatalog.LastAuditedOn,
        feedCount = BuiltInVpnSourceCatalog.Sources.Count,
        providers = BuiltInVpnSourceCatalog.Sources.Select(x => x.Provider).Distinct().Count(),
        protocols = Enum.GetNames<VpnProtocol>()
    });
}

public sealed record VpnEndpointResponse(Guid Id, string Host, int Port, VpnProtocol Protocol, string Transport,
    VpnEndpointStatus Status, int? LatencyMs, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt,
    DateTimeOffset? LastCheckedAt, int SuccessfulChecks, int FailedChecks, string? SourceName, string? SourceUrl);

/// <summary>Административное управление VPN-каталогом и его источниками.</summary>
[ApiController, Route("api/v1/admin/vpn"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
public sealed class AdminVpnController(IDbContextFactory<ProxyHarborDbContext> dbFactory, VpnCatalogService catalog) : ControllerBase
{
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
        var source = new VpnSource { Name = request.Name.Trim(), Provider = request.Provider.Trim(), Url = request.Url,
            DefaultProtocol = request.Protocol, Enabled = request.Enabled, Priority = request.Priority, License = request.License.Trim() };
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
    public async Task<ActionResult<PagedResult<VpnEndpointResponse>>> Endpoints([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] VpnProtocol? protocol = null, [FromQuery] VpnEndpointStatus? status = null, CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000); pageSize = Math.Clamp(pageSize, 10, 100);
        await using var db = await dbFactory.CreateDbContextAsync(token); var query = db.VpnEndpoints.AsNoTracking();
        if (protocol.HasValue) query = query.Where(x => x.Protocol == protocol.Value); if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(x => x.LastCheckedAt).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new VpnEndpointResponse(x.Id, x.Host, x.Port, x.Protocol, x.Transport, x.Status, x.LatencyMs,
                x.FirstSeenAt, x.LastSeenAt, x.LastCheckedAt, x.SuccessfulChecks, x.FailedChecks,
                x.FirstSource == null ? null : x.FirstSource.Name, x.FirstSource == null ? null : x.FirstSource.Url)).ToArrayAsync(token);
        return Ok(new PagedResult<VpnEndpointResponse>(items, page, pageSize, total));
    }

    [HttpPost("collect")]
    public async Task<ActionResult<VpnCollectionResult>> Collect(CancellationToken token)
    {
        try { return Ok(await catalog.CollectAsync(token)); }
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
#pragma warning restore CS1591
