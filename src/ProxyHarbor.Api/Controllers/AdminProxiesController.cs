using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Защищённый реестр всех когда-либо обнаруженных прокси и их качества.</summary>
[ApiController, Route("api/v1/admin/proxies"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class AdminProxiesController(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<CollectorOptions> collectorOptions,
    ProxyMetricsSnapshotCache? proxySnapshotCache = null) : ControllerBase
{
    private static readonly string[] AllowedSorts = ["lastChecked", "active", "latency", "lastSeen"];

    /// <summary>Возвращает серверную страницу прокси, общую сводку и доступные страны.</summary>
    [HttpGet]
    [ProducesResponseType<AdminProxyPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AdminProxyPage>> Get(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] ProxyStatus? status = null,
        [FromQuery] ProxyProtocol? protocol = null,
        [FromQuery] string? country = null,
        [FromQuery] string? query = null,
        [FromQuery] string sort = "lastChecked",
        CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        country = country?.Trim().ToUpperInvariant();
        query = query?.Trim();

        if (country is { Length: > 0 } && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
            return Problem("Страна должна быть двухбуквенным ISO-кодом.", statusCode: 400);
        if (query is { Length: > 128 })
            return Problem("Поисковая строка не может быть длиннее 128 символов.", statusCode: 400);
        if (!AllowedSorts.Contains(sort, StringComparer.Ordinal))
            return Problem("Неизвестный порядок сортировки.", statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        var freshAfter = now.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var proxySnapshot = proxySnapshotCache is null
            ? null
            : await proxySnapshotCache.GetAsync(token);
        await using var db = await dbFactory.CreateDbContextAsync(token);

        var all = db.Proxies.AsNoTracking();
        proxySnapshot ??= await ProxyMetricsSnapshotReader.ReadAsync(
            db,
            now,
            now.AddDays(-Math.Max(1, collectorOptions.Value.DeadRetentionDays)),
            freshAfter,
            token);

        var filtered = all;
        if (status.HasValue) filtered = filtered.Where(item => item.Status == status.Value);
        if (protocol.HasValue) filtered = filtered.Where(item => item.Protocol == protocol.Value);
        if (!string.IsNullOrEmpty(country)) filtered = filtered.Where(item => item.CountryCode == country);
        if (!string.IsNullOrEmpty(query))
        {
            var search = query;
            filtered = filtered.Where(item => item.Host.Contains(search) || (item.ExitIp != null && item.ExitIp.Contains(search)));
        }

        // Summary и facet-счётчики получены одним общим snapshot-проходом и
        // обновляются совместно. Повторный exact count(*) на почти миллионной,
        // активно изменяемой таблице занимал секунды и создавал лишний I/O при
        // каждом переключении обычного фильтра. Только произвольный текстовый
        // поиск требует отдельного точного count.
        var total = string.IsNullOrEmpty(query)
            ? ToInt(proxySnapshot.Facets
                .Where(item => !status.HasValue || item.Status == status.Value)
                .Where(item => !protocol.HasValue || item.Protocol == protocol.Value)
                .Where(item => string.IsNullOrEmpty(country) || item.CountryCode == country)
                .Sum(item => item.Count))
            : await filtered.CountAsync(token);
        var ordered = sort switch
        {
            "active" => filtered.OrderBy(item => item.CurrentAliveSince == null).ThenBy(item => item.CurrentAliveSince).ThenBy(item => item.Id),
            "latency" => filtered.OrderBy(item => item.LatencyMs == null).ThenBy(item => item.LatencyMs).ThenBy(item => item.Id),
            "lastSeen" => filtered.OrderByDescending(item => item.LastSeenAt).ThenBy(item => item.Id),
            _ => filtered.OrderBy(item => item.LastCheckedAt == null).ThenByDescending(item => item.LastCheckedAt).ThenBy(item => item.Id)
        };
        var skip = (page - 1) * pageSize;
        var entities = skip == 0
            ? await ordered.Take(pageSize).ToArrayAsync(token)
            : await ReadDeepPageAsync(db, all, ordered, skip, pageSize, token);

        var groups = proxySnapshot.Groups;
        var latencySamples = groups.Sum(row => row.FreshLatencySamples);
        var summary = new AdminProxySummary(
            ToInt(groups.Sum(row => row.Count)),
            ToInt(groups.Where(row => row.Status == ProxyStatus.Alive).Sum(row => row.Count)),
            ToInt(proxySnapshot.Published),
            ToInt(groups.Sum(row => row.StaleAlive)),
            ToInt(groups.Where(row => row.Status == ProxyStatus.Pending).Sum(row => row.Count)),
            ToInt(groups.Where(row => row.Status == ProxyStatus.Dead).Sum(row => row.Count)),
            ToInt(groups.Sum(row => row.EverAlive)),
            latencySamples == 0
                ? null
                : (int?)Math.Round(groups.Sum(row => row.FreshLatencyTotal) / (double)latencySamples),
            proxySnapshot.Countries.Count,
            proxySnapshot.OldestActiveAt is null
                ? null
                : Math.Max(0, (long)(now - proxySnapshot.OldestActiveAt.Value).TotalSeconds));
        var countries = proxySnapshot.Countries
            .Select(countryMetric => new AdminProxyCountry(countryMetric.Code, ToInt(countryMetric.Count)))
            .ToArray();

        return Ok(new AdminProxyPage(
            entities.Select(item => AdminProxyItem.From(item, now)).ToArray(),
            page, pageSize, total, summary, countries));
    }

    private static int ToInt(long value) => (int)Math.Min(int.MaxValue, Math.Max(0, value));

    /// <summary>
    /// Глубокий OFFSET сначала проходит только узкий индекс идентификаторов, а полные
    /// строки читает исключительно для итоговой страницы. На большом реестре это не
    /// заставляет PostgreSQL извлекать из heap десятки тысяч широких строк, которые
    /// затем всё равно будут отброшены. Обе операции выполняются в одном snapshot,
    /// поэтому параллельная очистка каталога не может разорвать страницу.
    /// </summary>
    private static Task<ProxyEndpoint[]> ReadDeepPageAsync(
        ProxyHarborDbContext db,
        IQueryable<ProxyEndpoint> all,
        IOrderedQueryable<ProxyEndpoint> ordered,
        int skip,
        int pageSize,
        CancellationToken token) =>
        BufferedReadSnapshot.ExecuteAsync(db, async readToken =>
        {
            var ids = await ordered.Select(item => item.Id)
                .Skip(skip).Take(pageSize).ToArrayAsync(readToken);
            if (ids.Length == 0) return [];

            var entitiesById = await all.Where(item => ids.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, readToken);
            return ids.Where(entitiesById.ContainsKey)
                .Select(id => entitiesById[id]).ToArray();
        }, token);
}

/// <summary>Серверная страница защищённого реестра.</summary>
public sealed record AdminProxyPage(
    IReadOnlyList<AdminProxyItem> Items,
    int Page,
    int PageSize,
    int Total,
    AdminProxySummary Summary,
    IReadOnlyList<AdminProxyCountry> Countries);

/// <summary>Глобальные показатели по всей накопленной базе прокси.</summary>
public sealed record AdminProxySummary(
    int Total,
    int Alive,
    int FreshAlive,
    int StaleAlive,
    int Pending,
    int Dead,
    int EverAlive,
    int? AverageAliveLatencyMs,
    int Countries,
    long? LongestActiveSeconds);

/// <summary>Количество известных прокси по стране выхода.</summary>
public sealed record AdminProxyCountry(string Code, int Count);

/// <summary>Полная безопасная диагностика одного прокси без служебных lease-токенов.</summary>
public sealed record AdminProxyItem(
    Guid Id,
    string Host,
    int Port,
    ProxyProtocol Protocol,
    ProxyStatus Status,
    int? LatencyMs,
    string? ExitIp,
    string? CountryCode,
    bool IsAnonymous,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? FirstAliveAt,
    DateTimeOffset? LastAliveAt,
    DateTimeOffset? CurrentAliveSince,
    long? ActiveForSeconds,
    DateTimeOffset? LastValidationAttemptAt,
    bool LastValidationDeferred,
    DateTimeOffset? NextCheckAt,
    int SuccessfulChecks,
    int FailedChecks,
    int ConsecutiveFailedChecks,
    decimal SuccessRate,
    string? LastError)
{
    /// <summary>Создаёт API-представление и вычисляет длительность текущей Alive-серии.</summary>
    public static AdminProxyItem From(ProxyEndpoint item, DateTimeOffset now) => new(
        item.Id, item.Host, item.Port, item.Protocol, item.Status, item.LatencyMs,
        item.ExitIp, item.CountryCode, item.IsAnonymous, item.FirstSeenAt, item.LastSeenAt,
        item.LastCheckedAt, item.FirstAliveAt, item.LastAliveAt, item.CurrentAliveSince,
        item.CurrentAliveSince is null ? null : Math.Max(0, (long)(now - item.CurrentAliveSince.Value).TotalSeconds),
        item.LastValidationAttemptAt, item.LastValidationDeferred, item.NextCheckAt,
        item.SuccessfulChecks, item.FailedChecks, item.ConsecutiveFailedChecks, item.SuccessRate, item.LastError);
}
