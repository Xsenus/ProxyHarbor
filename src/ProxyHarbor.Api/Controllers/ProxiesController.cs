using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Публичный каталог проверенных прокси и экспорты.</summary>
[ApiController, Route("api/v1"), EnableRateLimiting("public")]
public sealed class ProxiesController(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<CollectorOptions> collectorOptions,
    IProxyExportDbContextFactory exportDbFactory,
    IFreeExportAccessService freeExportAccess) : ControllerBase
{
    private const int MaxExportPageSize = 50_000;
    private const int MaxLegacyOffset = 5_000_000;
    private const int ConcurrentExportLimit = 2;
    private static readonly TimeSpan MaxExportDuration = TimeSpan.FromMinutes(5);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly SemaphoreSlim ExportConcurrencyGate = new(ConcurrentExportLimit, ConcurrentExportLimit);
    private readonly TimeSpan _exportTimeout = MaxExportDuration;
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Provider-agnostic unit tests могут использовать одну InMemory-фабрику для всех
    /// запросов. Production DI видит только публичный fail-closed конструктор выше.
    /// </summary>
    internal ProxiesController(
        IDbContextFactory<ProxyHarborDbContext> testDbFactory,
        IOptions<CollectorOptions> testCollectorOptions)
        : this(testDbFactory, testCollectorOptions, new TestExportDbContextFactory(testDbFactory),
            AllowAllExportAccessService.Instance)
    {
    }

    /// <summary>Совместимый конструктор интеграционных тестов потокового PostgreSQL export.</summary>
    internal ProxiesController(
        IDbContextFactory<ProxyHarborDbContext> testDbFactory,
        IOptions<CollectorOptions> testCollectorOptions,
        IProxyExportDbContextFactory testExportDbFactory)
        : this(testDbFactory, testCollectorOptions, testExportDbFactory, AllowAllExportAccessService.Instance)
    {
    }

    /// <summary>Позволяет transport-тесту ускорить только lifetime export, сохраняя production default.</summary>
    internal ProxiesController(
        IDbContextFactory<ProxyHarborDbContext> testDbFactory,
        IOptions<CollectorOptions> testCollectorOptions,
        IProxyExportDbContextFactory testExportDbFactory,
        TimeSpan exportTimeout)
        : this(testDbFactory, testCollectorOptions, testExportDbFactory, AllowAllExportAccessService.Instance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(exportTimeout, TimeSpan.Zero);
        _exportTimeout = exportTimeout;
    }

    /// <summary>Возвращает страницу только живых прокси, отсортированную по задержке.</summary>
    [HttpGet("proxies")]
    public async Task<ActionResult<PagedResult<ProxyDto>>> Get(
        [FromQuery, EnumDataType(typeof(ProxyProtocol))] ProxyProtocol? protocol,
        [FromQuery, Range(1, int.MaxValue)] int? maxLatencyMs,
        [FromQuery, Range(typeof(decimal), "0", "100")] decimal? minSuccessRate,
        [FromQuery] string[]? country,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeCountries(country, out var countries)) return InvalidCountries();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var requestedOffset = (long)(page - 1) * pageSize;
        if (requestedOffset > 5_000_000)
            return BadRequest(new ProblemDetails
            {
                Title = "Слишком глубокая страница",
                Detail = "Используйте больший pageSize или экспорт; максимальное смещение — 5 000 000 записей.",
                Status = 400
            });
        var skip = (int)requestedOffset;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var result = await BufferedReadSnapshot.ExecuteAsync(db, async token =>
        {
            var query = ApplyFilters(db.Proxies.AsNoTracking().Where(x =>
                x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter),
                protocol, maxLatencyMs, minSuccessRate, countries);
            var total = await query.CountAsync(token);
            var paid = await freeExportAccess.HasPaidAccessAsync(
                ControllerContext.HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal(), token);
            if (paid)
            {
                var entities = await OrderForPublication(query).Skip(skip).Take(pageSize).ToListAsync(token);
                return new PagedResult<ProxyDto>(entities.Select(ProxyDto.From).ToList(), page, pageSize, total)
                {
                    FullAccess = true
                };
            }

            var freeEntities = await SelectDiversifiedFreeProxiesAsync(query, token);
            return new PagedResult<ProxyDto>(freeEntities.Select(ProxyDto.From).ToList(), 1,
                FreeExportAccessService.FreeLimit, total)
            {
                FullAccess = false,
                Accessible = Math.Min(total, FreeExportAccessService.FreeLimit),
                Limited = true,
                Message = FreeExportAccessService.GetProxyCatalogUpgradeMessage(
                    CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, total),
                UpgradeUrl = "/account"
            };
        }, cancellationToken);
        if (ControllerContext.HttpContext is not null)
            HttpContext.Items["ProxyHarbor.ProxyItems"] = result.Items.Count;
        return Ok(result);
    }

    /// <summary>
    /// Выбирает до двух адресов из быстрого диапазона и заполняет набор средним
    /// диапазоном. Состав меняется раз в десять минут и предпочитает разные страны.
    /// </summary>
    private static async Task<List<ProxyEndpoint>> SelectDiversifiedFreeProxiesAsync(
        IQueryable<ProxyEndpoint> query, CancellationToken token)
    {
        var candidates = await OrderForPublication(query)
            .Take(FreeCatalogSelector.CandidatePoolSize)
            .ToListAsync(token);
        return FreeCatalogSelector.Select(candidates, x => x.Key, x => x.CountryCode,
                FreeExportAccessService.FreeLimit, DateTimeOffset.UtcNow)
            .OrderBy(x => x.LatencyMs == null).ThenBy(x => x.LatencyMs)
            .ThenByDescending(x => x.SuccessfulChecks).ToList();
    }

    /// <summary>Совместимый вызов для provider-agnostic unit-тестов без country-фильтра.</summary>
    internal Task<ActionResult<PagedResult<ProxyDto>>> Get(
        ProxyProtocol? protocol,
        int? maxLatencyMs,
        decimal? minSuccessRate,
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default) =>
        Get(protocol, maxLatencyMs, minSuccessRate, null, page, pageSize, cancellationToken);

    /// <summary>
    /// Возвращает keyset-страницу без растущего OFFSET и дорогостоящего точного COUNT.
    /// Следующий запрос передаёт непрозрачный NextCursor в параметре after.
    /// </summary>
    [HttpGet("proxies/seek")]
    public async Task<ActionResult<CursorPagedResult<ProxyDto>>> Seek(
        [FromQuery, EnumDataType(typeof(ProxyProtocol))] ProxyProtocol? protocol,
        [FromQuery, Range(1, int.MaxValue)] int? maxLatencyMs,
        [FromQuery, Range(typeof(decimal), "0", "100")] decimal? minSuccessRate,
        [FromQuery] string[]? country,
        [FromQuery, StringLength(PublicationCursor.EncodedLength)] string? after,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (!await freeExportAccess.HasPaidAccessAsync(
                ControllerContext.HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal(), cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Требуется подписка",
                Detail = FreeExportAccessService.GetProxyCatalogUpgradeMessage(
                    CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, FreeExportAccessService.FreeLimit),
                Status = StatusCodes.Status403Forbidden,
                Extensions = { ["upgradeUrl"] = "/account" }
            });
        if (!TryNormalizeCountries(country, out var countries)) return InvalidCountries();
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var fingerprint = PublicationCursor.FilterFingerprint(protocol, maxLatencyMs, minSuccessRate, countries);
        PublicationPosition? position = null;
        if (after is not null)
        {
            if (!PublicationCursor.TryDecode(after, fingerprint, out var decoded))
                return InvalidCursor();
            position = decoded;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var query = ApplyFilters(db.Proxies.AsNoTracking().Where(x =>
            x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter && x.LatencyMs != null),
            protocol, maxLatencyMs, minSuccessRate, countries);
        if (position.HasValue) query = ApplyAfter(query, position.Value);
        var entities = await OrderForPublication(query).Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = entities.Count > pageSize;
        if (hasMore) entities.RemoveAt(pageSize);
        var nextCursor = hasMore ? EncodePosition(entities[^1], fingerprint) : null;
        var items = entities.Select(ProxyDto.From).ToList();
        if (ControllerContext.HttpContext is not null)
            HttpContext.Items["ProxyHarbor.ProxyItems"] = items.Count;
        return Ok(new CursorPagedResult<ProxyDto>(items, pageSize, hasMore, nextCursor));
    }

    /// <summary>Совместимый вызов keyset-страницы без country-фильтра.</summary>
    internal Task<ActionResult<CursorPagedResult<ProxyDto>>> Seek(
        ProxyProtocol? protocol,
        int? maxLatencyMs,
        decimal? minSuccessRate,
        string? after,
        int pageSize = 100,
        CancellationToken cancellationToken = default) =>
        Seek(protocol, maxLatencyMs, minSuccessRate, null, after, pageSize, cancellationToken);

    /// <summary>Возвращает страны, доступные среди свежих живых прокси.</summary>
    [HttpGet("proxies/countries")]
    [OutputCache(PolicyName = "public-list")]
    public async Task<ActionResult<IReadOnlyList<ProxyCountryDto>>> Countries(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var countryRows = await db.Proxies.AsNoTracking()
            .Where(proxy => proxy.Status == ProxyStatus.Alive && proxy.LastCheckedAt >= freshAfter &&
                proxy.CountryCode != null)
            .GroupBy(proxy => proxy.CountryCode!)
            .Select(group => new ProxyCountryDto(group.Key, group.Count()))
            .ToListAsync(cancellationToken);
        var countries = countryRows.OrderByDescending(countryItem => countryItem.Count)
            .ThenBy(countryItem => countryItem.Code, StringComparer.Ordinal)
            .ToList();
        return Ok(countries);
    }

    /// <summary>Потоково экспортирует страницу живых записей в json, xml, txt или csv.</summary>
    [HttpGet("export/{format}")]
    [EnableRateLimiting("export")]
    [ProducesResponseType(typeof(ProxyDto[]), StatusCodes.Status200OK,
        "application/json", "application/xml", "text/plain", "text/csv")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Export(
        string format,
        [FromQuery, EnumDataType(typeof(ProxyProtocol))] ProxyProtocol? protocol,
        [FromQuery, Range(1, int.MaxValue)] int? maxLatencyMs,
        [FromQuery, Range(typeof(decimal), "0", "100")] decimal? minSuccessRate,
        [FromQuery] string[]? country,
        CancellationToken cancellationToken,
        [FromQuery, Range(1, MaxExportPageSize)] int limit = MaxExportPageSize,
        [FromQuery, Range(0, MaxLegacyOffset)] int offset = 0)
    {
        if (!TryNormalizeCountries(country, out var countries)) return InvalidCountries();
        if (offset > MaxLegacyOffset)
            return BadRequest(new ProblemDetails
            {
                Title = "Слишком глубокий экспорт",
                Detail = "Используйте /export/{format}/seek; максимальное legacy-смещение — 5 000 000 записей.",
                Status = StatusCodes.Status400BadRequest
            });
        return await ExportCore(
            format, protocol, maxLatencyMs, minSuccessRate, countries, limit, offset, after: null, cancellationToken);
    }

    /// <summary>Совместимый вызов legacy-экспорта без country-фильтра.</summary>
    internal Task<IActionResult> Export(
        string format,
        ProxyProtocol? protocol,
        int? maxLatencyMs,
        decimal? minSuccessRate,
        CancellationToken cancellationToken,
        int limit = MaxExportPageSize,
        int offset = 0) =>
        Export(format, protocol, maxLatencyMs, minSuccessRate, null, cancellationToken, limit, offset);

    /// <summary>Потоково экспортирует keyset-страницу с постоянной стоимостью продолжения.</summary>
    [HttpGet("export/{format}/seek")]
    [EnableRateLimiting("export")]
    [ProducesResponseType(typeof(ProxyDto[]), StatusCodes.Status200OK,
        "application/json", "application/xml", "text/plain", "text/csv")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ExportSeek(
        string format,
        [FromQuery, EnumDataType(typeof(ProxyProtocol))] ProxyProtocol? protocol,
        [FromQuery, Range(1, int.MaxValue)] int? maxLatencyMs,
        [FromQuery, Range(typeof(decimal), "0", "100")] decimal? minSuccessRate,
        [FromQuery] string[]? country,
        CancellationToken cancellationToken,
        [FromQuery, Range(1, MaxExportPageSize)] int limit = MaxExportPageSize,
        [FromQuery, StringLength(PublicationCursor.EncodedLength)] string? after = null)
    {
        if (!TryNormalizeCountries(country, out var countries)) return InvalidCountries();
        return await ExportCore(
            format, protocol, maxLatencyMs, minSuccessRate, countries, limit, offset: null, after, cancellationToken);
    }

    /// <summary>Совместимый вызов keyset-экспорта без country-фильтра.</summary>
    internal Task<IActionResult> ExportSeek(
        string format,
        ProxyProtocol? protocol,
        int? maxLatencyMs,
        decimal? minSuccessRate,
        CancellationToken cancellationToken,
        int limit = MaxExportPageSize,
        string? after = null) =>
        ExportSeek(format, protocol, maxLatencyMs, minSuccessRate, null, cancellationToken, limit, after);

    private async Task<IActionResult> ExportCore(
        string format,
        ProxyProtocol? protocol,
        int? maxLatencyMs,
        decimal? minSuccessRate,
        string[] countries,
        int limit,
        int? offset,
        string? after,
        CancellationToken cancellationToken)
    {
        var normalizedFormat = format.ToLowerInvariant();
        var contentType = normalizedFormat switch
        {
            "json" => "application/json; charset=utf-8",
            "txt" => "text/plain; charset=utf-8",
            "csv" => "text/csv; charset=utf-8",
            "xml" => "application/xml; charset=utf-8",
            _ => null
        };
        if (contentType is null)
            return Problem("Поддерживаются форматы json, xml, txt и csv.", statusCode: 400);
        var access = await freeExportAccess.AcquireAsync(
            User,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!access.Allowed)
        {
            var wait = Math.Max(1, (int)Math.Ceiling((access.NextAllowedAt!.Value - DateTimeOffset.UtcNow).TotalSeconds));
            Response.Headers.RetryAfter = wait.ToString(CultureInfo.InvariantCulture);
            Response.Headers["X-Access-Tier"] = access.Tier;
            var problem = new ProblemDetails
            {
                Title = "Лимит бесплатной выгрузки",
                Detail = FreeExportAccessService.GetUpgradeMessage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName),
                Status = StatusCodes.Status429TooManyRequests
            };
            problem.Extensions["limit"] = FreeExportAccessService.FreeLimit;
            problem.Extensions["cooldownSeconds"] = FreeExportAccessService.CooldownSeconds;
            problem.Extensions["nextAllowedAt"] = access.NextAllowedAt;
            problem.Extensions["upgradeUrl"] = "/account";
            return StatusCode(StatusCodes.Status429TooManyRequests, problem);
        }
        var effectiveLimit = access.IsPaid ? limit : FreeExportAccessService.FreeLimit;
        var seekMode = !offset.HasValue;
        var fingerprint = PublicationCursor.FilterFingerprint(protocol, maxLatencyMs, minSuccessRate, countries);
        PublicationPosition? position = null;
        if (seekMode && access.IsPaid && after is not null)
        {
            if (!PublicationCursor.TryDecode(after, fingerprint, out var decoded))
                return InvalidCursor();
            position = decoded;
        }
        if (!await ExportConcurrencyGate.WaitAsync(0, cancellationToken))
        {
            Response.Headers.RetryAfter = "1";
            return Problem(
                "Сервис уже формирует максимально допустимое число экспортов; повторите запрос через секунду.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            // Медленный клиент не может бесконечно удерживать один из двух slots и старый
            // PostgreSQL MVCC snapshot. Один token ограничивает SQL, чтение и response writes.
            using var exportLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exportLifetime.CancelAfter(_exportTimeout);
            var exportToken = exportLifetime.Token;
            await using var db = await exportDbFactory.CreateDbContextAsync(exportToken);
            // Boundary headers и streaming body обязаны видеть один набор строк. Иначе
            // validation update между двумя SQL-командами способен выдать cursor от одной
            // страницы, а тело — от другой. InMemory unit provider транзакций не имеет.
            await using var snapshot = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, exportToken)
                : null;
            var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
            var query = ApplyFilters(db.Proxies.AsNoTracking().Where(x =>
                x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter), protocol, maxLatencyMs, minSuccessRate, countries);
            if (seekMode && access.IsPaid)
            {
                query = query.Where(x => x.LatencyMs != null);
                if (position.HasValue) query = ApplyAfter(query, position.Value);
            }
            var ordered = OrderForPublication(query);
            var legacyOffset = offset ?? 0;
            List<ProxyEndpoint>? freeSelection = null;
            if (!access.IsPaid)
            {
                // Бесплатная выдача берёт медианные адреса из разных стран. Это полезнее
                // пяти почти одинаковых соседних строк и не раскрывает premium-верхушку.
                freeSelection = await SelectDiversifiedFreeProxiesAsync(query, exportToken);
                legacyOffset = 0;
            }
            var nextOffset = (long)legacyOffset + effectiveLimit;
            // Предварительный index-only boundary-запрос не материализует body и
            // позволяет выставить continuation headers до первой потоковой записи.
            var pageQuery = seekMode && access.IsPaid ? ordered : ordered.Skip(legacyOffset);
            bool hasMore;
            string? nextCursor = null;
            if (seekMode && access.IsPaid)
            {
                // Последняя включённая строка и один look-ahead дают hasMore и cursor
                // одним round-trip вместо двух одинаковых проходов по индексу.
                var boundary = await pageQuery.Skip(effectiveLimit - 1).Take(2)
                    .Select(x => new PublicationPosition(x.LatencyMs!.Value, x.SuccessfulChecks, x.Id))
                    .ToListAsync(exportToken);
                hasMore = boundary.Count == 2;
                if (hasMore) nextCursor = PublicationCursor.Encode(boundary[0], fingerprint);
            }
            else
            {
                hasMore = freeSelection is not null
                    ? await query.CountAsync(exportToken) > freeSelection.Count
                    : await ordered.Skip((int)nextOffset).AnyAsync(exportToken);
            }
            var proxies = freeSelection is not null
                ? EnumerateAsync(freeSelection.Select(ProxyDto.From), exportToken)
                : pageQuery.Take(effectiveLimit).AsAsyncEnumerable().Select(ProxyDto.From);
            if (ControllerContext.HttpContext is not null)
                HttpContext.Items["ProxyHarbor.ProxyItems"] = effectiveLimit;
            var suffix = protocol?.ToString().ToLowerInvariant() ?? "all";
            Response.ContentType = contentType;
            var pageSuffix = seekMode ? "-seek" : legacyOffset == 0 ? string.Empty : $"-offset-{legacyOffset}";
            Response.Headers.ContentDisposition =
                $"attachment; filename=\"proxies-{suffix}{pageSuffix}.{normalizedFormat}\"";
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Export-Limit"] = effectiveLimit.ToString(CultureInfo.InvariantCulture);
            Response.Headers["X-Export-Truncated"] = hasMore ? "true" : "false";
            Response.Headers["X-Access-Tier"] = access.Tier;
            if (!access.IsPaid)
            {
                Response.Headers["X-Free-Cooldown"] = FreeExportAccessService.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
                Response.Headers["Link"] = "</account>; rel=\"upgrade\"";
            }
            if (seekMode && access.IsPaid)
            {
                Response.Headers["X-Export-Cursor"] = after ?? "start";
                if (nextCursor is not null) Response.Headers["X-Next-Cursor"] = nextCursor;
            }
            else
            {
                Response.Headers["X-Export-Offset"] = legacyOffset.ToString(CultureInfo.InvariantCulture);
            }
            if (access.IsPaid && !seekMode && hasMore)
                Response.Headers["X-Next-Offset"] = nextOffset.ToString(CultureInfo.InvariantCulture);

            // Фиксируем continuation headers до первой записи. После этого timeout
            // корректно обрывает уже начатый stream вместо попытки заменить его ProblemDetails.
            await Response.StartAsync(exportToken);
            switch (normalizedFormat)
            {
                case "json": await WriteJsonAsync(Response.Body, proxies, access, exportToken); break;
                case "txt": await WriteTextAsync(Response.Body, proxies, exportToken); break;
                case "csv": await WriteCsvAsync(Response.Body, proxies, exportToken); break;
                case "xml": await WriteXmlAsync(Response.Body, proxies, exportToken); break;
            }
            if (snapshot is not null) await snapshot.CommitAsync(exportToken);
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && !Response.HasStarted)
        {
            // Lifetime истёк ещё до начала body: клиент получает повторяемый bounded отказ.
            Response.Headers.RetryAfter = "5";
            return Problem(
                "Экспорт превысил максимальное время формирования; уменьшите limit и повторите запрос.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        finally { ExportConcurrencyGate.Release(); }
    }

    private BadRequestObjectResult InvalidCursor() => BadRequest(new ProblemDetails
    {
        Title = "Некорректный cursor",
        Detail = "Cursor повреждён, устарел либо был создан для другого набора фильтров.",
        Status = StatusCodes.Status400BadRequest
    });

    private static async IAsyncEnumerable<ProxyDto> EnumerateAsync(
        IEnumerable<ProxyDto> values,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            yield return value;
        }
        await Task.CompletedTask;
    }

    private static async Task WriteJsonAsync(
        Stream output,
        IAsyncEnumerable<ProxyDto> proxies,
        FreeExportAccess access,
        CancellationToken token)
    {
        // SerializeAsync умеет потоково перечислять IAsyncEnumerable и не выполняет
        // запрещённый Kestrel synchronous flush при включённом Brotli/Gzip.
        if (access.IsPaid)
        {
            await JsonSerializer.SerializeAsync(output, proxies, ExportJsonOptions, token);
            return;
        }

        var metadata = JsonSerializer.Serialize(new
        {
            tier = access.Tier,
            limited = true,
            limit = FreeExportAccessService.FreeLimit,
            cooldownSeconds = FreeExportAccessService.CooldownSeconds,
            nextAllowedAt = access.NextAllowedAt,
            message = FreeExportAccessService.GetUpgradeMessage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName),
            upgradeUrl = "/account"
        }, ExportJsonOptions);
        await output.WriteAsync(Encoding.UTF8.GetBytes($"{{\"access\":{metadata},\"proxies\":"), token);
        await JsonSerializer.SerializeAsync(output, proxies, ExportJsonOptions, token);
        await output.WriteAsync("}"u8.ToArray(), token);
    }

    private static async Task WriteTextAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        using var cancellationBoundOutput = new CancellationBoundOutputStream(output, token);
        await using var writer = new StreamWriter(cancellationBoundOutput, Utf8NoBom, 64 * 1024, leaveOpen: true);
        await foreach (var proxy in proxies.WithCancellation(token))
            await writer.WriteLineAsync(proxy.Url.AsMemory(), token);
        await writer.FlushAsync(token);
    }

    private static async Task WriteCsvAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        using var cancellationBoundOutput = new CancellationBoundOutputStream(output, token);
        await using var writer = new StreamWriter(cancellationBoundOutput, Utf8NoBom, 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync("protocol,host,port,countryCode,latencyMs,successRate,lastCheckedAt,url,exitIp".AsMemory(), token);
        await foreach (var proxy in proxies.WithCancellation(token))
        {
            var row = string.Join(',', new[]
            {
                CsvField(proxy.Protocol.ToString()),
                CsvField(proxy.Host),
                proxy.Port.ToString(CultureInfo.InvariantCulture),
                CsvField(proxy.CountryCode ?? string.Empty),
                proxy.LatencyMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                proxy.SuccessRate.ToString(CultureInfo.InvariantCulture),
                CsvField(proxy.LastCheckedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                CsvField(proxy.Url),
                CsvField(proxy.ExitIp ?? string.Empty)
            });
            await writer.WriteLineAsync(row.AsMemory(), token);
        }
        await writer.FlushAsync(token);
    }

    private static async Task WriteXmlAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        // XmlWriter.Dispose выполняет синхронный Flush даже после FlushAsync. Kestrel и
        // compression streams запрещают его, поэтому обёртка поглощает только финальный
        // избыточный Flush, сохраняя запрет на любые синхронные записи.
        // XmlWriter async API не принимает CancellationToken для отдельных записей.
        // Адаптер поэтому навязывает request token каждому обращению к Response.Body.
        using var asyncOutput = new CancellationBoundOutputStream(output, token);
        using var writer = XmlWriter.Create(asyncOutput, new XmlWriterSettings
        {
            Async = true,
            Encoding = Utf8NoBom,
            Indent = false,
            CloseOutput = false
        });
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "proxies", null);
        await foreach (var proxy in proxies.WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteStartElementAsync(null, "proxy", null);
            await writer.WriteElementStringAsync(null, "protocol", null, proxy.Protocol.ToString());
            await writer.WriteElementStringAsync(null, "host", null, proxy.Host);
            await writer.WriteElementStringAsync(null, "port", null, proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await writer.WriteElementStringAsync(null, "countryCode", null, proxy.CountryCode ?? string.Empty);
            await writer.WriteElementStringAsync(null, "latencyMs", null,
                proxy.LatencyMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            await writer.WriteElementStringAsync(null, "successRate", null, proxy.SuccessRate.ToString(CultureInfo.InvariantCulture));
            await writer.WriteElementStringAsync(null, "lastCheckedAt", null,
                proxy.LastCheckedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            await writer.WriteElementStringAsync(null, "url", null, proxy.Url);
            await writer.WriteElementStringAsync(null, "exitIp", null, proxy.ExitIp ?? string.Empty);
            await writer.WriteEndElementAsync();
        }
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    /// <summary>Применяет одинаковые ограничения к постраничной и потоковой публичной выдаче.</summary>
    private static IQueryable<ProxyEndpoint> ApplyFilters(
        IQueryable<ProxyEndpoint> query,
        ProxyProtocol? protocol,
        int? maxLatencyMs,
        decimal? minSuccessRate,
        string[] countries)
    {
        if (protocol.HasValue) query = query.Where(x => x.Protocol == protocol);
        if (maxLatencyMs.HasValue) query = query.Where(x => x.LatencyMs <= maxLatencyMs);
        if (countries.Length > 0) query = query.Where(x => x.CountryCode != null && countries.Contains(x.CountryCode));
        if (minSuccessRate.HasValue)
        {
            var threshold = minSuccessRate.Value;
            query = query.Where(x => x.SuccessfulChecks + x.FailedChecks > 0 &&
                100m * x.SuccessfulChecks >= threshold * (x.SuccessfulChecks + x.FailedChecks));
        }
        return query;
    }

    /// <summary>Принимает повторяющиеся и comma-separated ISO-коды, возвращая стабильный набор.</summary>
    internal static bool TryNormalizeCountries(string[]? values, out string[] countries)
    {
        countries = (values ?? [])
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return countries.Length <= 250 && countries.All(code =>
            code.Length == 2 && code.All(character => character is >= 'A' and <= 'Z'));
    }

    private BadRequestObjectResult InvalidCountries() => BadRequest(new ProblemDetails
    {
        Title = "Некорректный фильтр стран",
        Detail = "Используйте двухбуквенные ISO 3166-1 alpha-2 коды, например country=RU&country=DE.",
        Status = StatusCodes.Status400BadRequest
    });

    /// <summary>
    /// Задаёт единый полный порядок для списка и каждого export-формата.
    /// UUID устраняет неоднозначность между строками с одинаковой latency/statistics,
    /// поэтому последовательные offset-страницы не повторяют и не пропускают записи.
    /// </summary>
    private static IOrderedQueryable<ProxyEndpoint> OrderForPublication(IQueryable<ProxyEndpoint> query) =>
        query.OrderBy(x => x.LatencyMs)
            .ThenByDescending(x => x.SuccessfulChecks)
            .ThenBy(x => x.Id);

    /// <summary>Формирует sargable lexicographic predicate под partial public-order index.</summary>
    internal static IQueryable<ProxyEndpoint> ApplyAfter(
        IQueryable<ProxyEndpoint> query,
        PublicationPosition position) =>
        query.Where(x => x.LatencyMs > position.LatencyMs ||
            x.LatencyMs == position.LatencyMs &&
            (x.SuccessfulChecks < position.SuccessfulChecks ||
                x.SuccessfulChecks == position.SuccessfulChecks && x.Id.CompareTo(position.Id) > 0));

    private static string EncodePosition(ProxyEndpoint endpoint, ulong fingerprint) =>
        PublicationCursor.Encode(
            new PublicationPosition(endpoint.LatencyMs!.Value, endpoint.SuccessfulChecks, endpoint.Id), fingerprint);

    /// <summary>Кавычит строковое CSV-поле и нейтрализует spreadsheet formula injection.</summary>
    private static string CsvField(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = $"'{value}";
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>
    /// Навязывает request token каждому async write/flush, включая финальный flush из
    /// DisposeAsync writer'а, чтобы отключившийся клиент немедленно освобождал export slot.
    /// </summary>
    private sealed class CancellationBoundOutputStream(Stream inner, CancellationToken requestToken) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        // Перед Dispose всегда выполняется XmlWriter.FlushAsync; повторный sync flush не нужен.
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            requestToken.ThrowIfCancellationRequested();
            return inner.FlushAsync(requestToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous XML response writes are forbidden.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            requestToken.ThrowIfCancellationRequested();
            return inner.WriteAsync(buffer, offset, count, requestToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            requestToken.ThrowIfCancellationRequested();
            return inner.WriteAsync(buffer, requestToken);
        }

        protected override void Dispose(bool disposing)
        {
            // Жизненным циклом исходного Response.Body владеет ASP.NET Core.
            base.Dispose(disposing);
        }
    }

    /// <summary>Внутренний адаптер существует только для unit-test конструктора.</summary>
    private sealed class TestExportDbContextFactory(
        IDbContextFactory<ProxyHarborDbContext> factory) : IProxyExportDbContextFactory
    {
        public Task<ProxyHarborDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            factory.CreateDbContextAsync(cancellationToken);
    }

    /// <summary>Старые transport-тесты проверяют только формат и явно обходят коммерческую политику.</summary>
    private sealed class AllowAllExportAccessService : IFreeExportAccessService
    {
        internal static AllowAllExportAccessService Instance { get; } = new();
        public Task<FreeExportAccess> AcquireAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            string? remoteIp,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FreeExportAccess(true, true, int.MaxValue, null, "paid"));
        public Task<bool> HasPaidAccessAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }
}

/// <summary>Стабильный публичный контракт без внутренних полей и ошибок.</summary>
public sealed record ProxyDto(
    string Host,
    int Port,
    ProxyProtocol Protocol,
    string Url,
    int? LatencyMs,
    decimal SuccessRate,
    string? ExitIp,
    string? CountryCode,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? FirstAliveAt,
    DateTimeOffset? LastAliveAt,
    DateTimeOffset? ActiveSince,
    long? ActiveForSeconds)
{
    /// <summary>Проецирует внутреннюю entity в безопасный публичный контракт и канонический URL.</summary>
    public static ProxyDto From(ProxyEndpoint x)
    {
        // Категория HTTPS в публичных free-list означает HTTP CONNECT, а не TLS до proxy endpoint.
        var transportScheme = x.Protocol is ProxyProtocol.Http or ProxyProtocol.Https
            ? "http"
            : x.Protocol.ToString().ToLowerInvariant();
        var host = x.Host.Contains(':') ? $"[{x.Host}]" : x.Host;
        long? activeForSeconds = x.CurrentAliveSince is { } activeSince
            ? Math.Max(0, (long)(DateTimeOffset.UtcNow - activeSince).TotalSeconds)
            : null;
        return new ProxyDto(x.Host, x.Port, x.Protocol, $"{transportScheme}://{host}:{x.Port}",
            x.LatencyMs, x.SuccessRate, x.ExitIp, x.CountryCode, x.LastCheckedAt,
            x.FirstAliveAt, x.LastAliveAt, x.CurrentAliveSince, activeForSeconds);
    }
}

/// <summary>Доступная страна и число свежих Alive-прокси в ней.</summary>
public sealed record ProxyCountryDto(string Code, int Count);

/// <summary>Bounded keyset-страница без линейного offset и полного count.</summary>
public sealed record CursorPagedResult<T>(IReadOnlyList<T> Items, int PageSize, bool HasMore, string? NextCursor);
