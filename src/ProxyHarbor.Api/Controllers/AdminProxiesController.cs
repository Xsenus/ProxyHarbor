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
    IOptions<CollectorOptions> collectorOptions) : ControllerBase
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
        await using var db = await dbFactory.CreateDbContextAsync(token);

        var all = db.Proxies.AsNoTracking();
        var rawSummary = await all.GroupBy(_ => 1).Select(group => new
        {
            Total = group.Count(),
            Alive = group.Count(item => item.Status == ProxyStatus.Alive),
            FreshAlive = group.Count(item => item.Status == ProxyStatus.Alive && item.LastCheckedAt >= freshAfter),
            Pending = group.Count(item => item.Status == ProxyStatus.Pending),
            Dead = group.Count(item => item.Status == ProxyStatus.Dead),
            EverAlive = group.Count(item => item.FirstAliveAt != null),
            Countries = group.Where(item => item.CountryCode != null).Select(item => item.CountryCode).Distinct().Count(),
            AverageAliveLatencyMs = group.Where(item => item.Status == ProxyStatus.Alive && item.LastCheckedAt >= freshAfter && item.LatencyMs != null)
                .Average(item => (double?)item.LatencyMs),
            OldestActiveAt = group.Where(item => item.Status == ProxyStatus.Alive && item.CurrentAliveSince != null)
                .Min(item => (DateTimeOffset?)item.CurrentAliveSince)
        }).SingleOrDefaultAsync(token);

        var countryCounts = await all.Where(item => item.CountryCode != null)
            .GroupBy(item => item.CountryCode!)
            .Select(group => new { Code = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count).ThenBy(item => item.Code)
            .ToArrayAsync(token);
        var countries = countryCounts.Select(item => new AdminProxyCountry(item.Code, item.Count)).ToArray();

        var filtered = all;
        if (status.HasValue) filtered = filtered.Where(item => item.Status == status.Value);
        if (protocol.HasValue) filtered = filtered.Where(item => item.Protocol == protocol.Value);
        if (!string.IsNullOrEmpty(country)) filtered = filtered.Where(item => item.CountryCode == country);
        if (!string.IsNullOrEmpty(query))
        {
            var search = query;
            filtered = filtered.Where(item => item.Host.Contains(search) || (item.ExitIp != null && item.ExitIp.Contains(search)));
        }

        var total = await filtered.CountAsync(token);
        var ordered = sort switch
        {
            "active" => filtered.OrderBy(item => item.CurrentAliveSince == null).ThenBy(item => item.CurrentAliveSince).ThenBy(item => item.Id),
            "latency" => filtered.OrderBy(item => item.LatencyMs == null).ThenBy(item => item.LatencyMs).ThenBy(item => item.Id),
            "lastSeen" => filtered.OrderByDescending(item => item.LastSeenAt).ThenBy(item => item.Id),
            _ => filtered.OrderBy(item => item.LastCheckedAt == null).ThenByDescending(item => item.LastCheckedAt).ThenBy(item => item.Id)
        };
        var entities = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);

        var summary = rawSummary is null
            ? new AdminProxySummary(0, 0, 0, 0, 0, 0, 0, null, 0, null)
            : new AdminProxySummary(rawSummary.Total, rawSummary.Alive, rawSummary.FreshAlive,
                rawSummary.Alive - rawSummary.FreshAlive, rawSummary.Pending, rawSummary.Dead,
                rawSummary.EverAlive, rawSummary.AverageAliveLatencyMs is null ? null : (int)Math.Round(rawSummary.AverageAliveLatencyMs.Value),
                rawSummary.Countries, rawSummary.OldestActiveAt is null ? null : Math.Max(0, (long)(now - rawSummary.OldestActiveAt.Value).TotalSeconds));

        return Ok(new AdminProxyPage(
            entities.Select(item => AdminProxyItem.From(item, now)).ToArray(),
            page, pageSize, total, summary, countries));
    }
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
