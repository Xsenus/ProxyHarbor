using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Сводные показатели для панели и внешнего мониторинга.</summary>
[ApiController, Route("api/v1/stats"), EnableRateLimiting("public")]
public sealed class StatsController(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<CollectorOptions> collectorOptions) : ControllerBase
{
    /// <summary>Возвращает согласованный snapshot proxy/source/run агрегатов.</summary>
    [HttpGet]
    [OutputCache(PolicyName = "public-summary")]
    [ProducesResponseType<StatsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<StatsResponse>> Get(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var now = DateTimeOffset.UtcNow;
        var response = await BufferedReadSnapshot.ExecuteAsync(db, async token =>
        {
            // Один агрегирующий проход заменяет семь отдельных сканирований большой таблицы Proxies.
            var proxyRows = await db.Proxies.AsNoTracking()
                .GroupBy(x => new { x.Status, x.Protocol }).Select(group => new
                {
                    group.Key.Status,
                    group.Key.Protocol,
                    Total = group.Count(),
                    FreshAlive = group.Count(proxy => proxy.Status == ProxyStatus.Alive && proxy.LastCheckedAt >= freshAfter),
                    StaleAlive = group.Count(proxy => proxy.Status == ProxyStatus.Alive &&
                        (proxy.LastCheckedAt == null || proxy.LastCheckedAt < freshAfter)),
                    Due = group.Count(proxy => (proxy.NextCheckAt == null || proxy.NextCheckAt <= now) &&
                        (proxy.CheckLeaseUntil == null || proxy.CheckLeaseUntil < now)),
                    Leased = group.Count(proxy => proxy.CheckLeaseUntil >= now),
                    Scheduled = group.Count(proxy => proxy.NextCheckAt > now),
                    FreshLatencyTotal = group.Where(proxy => proxy.Status == ProxyStatus.Alive &&
                        proxy.LastCheckedAt >= freshAfter && proxy.LatencyMs != null)
                        .Sum(proxy => (long?)proxy.LatencyMs) ?? 0,
                    FreshLatencySamples = group.Count(proxy => proxy.Status == ProxyStatus.Alive &&
                        proxy.LastCheckedAt >= freshAfter && proxy.LatencyMs != null)
                }).ToListAsync(token);
            var sourceHealth = await db.Sources.AsNoTracking().Where(x => x.Enabled).GroupBy(_ => 1).Select(x => new
            {
                Enabled = x.Count(),
                Failing = x.Count(source => source.ConsecutiveFailures > 0),
                RepeatedlyFailing = x.Count(source => source.ConsecutiveFailures >= 3),
                Truncated = x.Count(source => source.LastResultTruncated)
            }).SingleOrDefaultAsync(token);
            var lastRun = await db.Runs.AsNoTracking().OrderByDescending(x => x.StartedAt)
                .FirstOrDefaultAsync(token);

            var alive = proxyRows.Sum(row => row.FreshAlive);
            var latencySamples = proxyRows.Sum(row => row.FreshLatencySamples);
            var latencyTotal = proxyRows.Sum(row => row.FreshLatencyTotal);
            var byProtocol = proxyRows.GroupBy(row => row.Protocol)
                .Select(group => new ProtocolCountResponse(group.Key, group.Sum(row => row.FreshAlive)))
                .Where(row => row.Count > 0)
                .ToArray();
            return new StatsResponse(
                alive,
                proxyRows.Sum(row => row.StaleAlive),
                proxyRows.Where(row => row.Status == ProxyStatus.Pending).Sum(row => row.Total),
                proxyRows.Where(row => row.Status == ProxyStatus.Dead).Sum(row => row.Total),
                proxyRows.Sum(row => row.Due),
                proxyRows.Sum(row => row.Leased),
                proxyRows.Sum(row => row.Scheduled),
                latencySamples == 0 ? null : (double)latencyTotal / latencySamples,
                sourceHealth?.Enabled ?? 0,
                sourceHealth?.Failing ?? 0,
                sourceHealth?.RepeatedlyFailing ?? 0,
                sourceHealth?.Truncated ?? 0,
                byProtocol,
                lastRun is null ? null : PublicCollectionRunResponse.From(lastRun));
        }, cancellationToken);
        return Ok(response);
    }
}

/// <summary>Стабильная публичная сводка без прямой сериализации persistence-сущностей.</summary>
public sealed record StatsResponse(
    int Alive,
    int StaleAlive,
    int Pending,
    int Dead,
    int DueForCheck,
    int ChecksInProgress,
    int ScheduledChecks,
    double? AverageLatencyMs,
    int Sources,
    int FailingSources,
    int RepeatedlyFailingSources,
    int TruncatedSources,
    IReadOnlyList<ProtocolCountResponse> ByProtocol,
    PublicCollectionRunResponse? LastRun);

/// <summary>Число свежих живых прокси одного протокола.</summary>
public sealed record ProtocolCountResponse(ProxyProtocol Protocol, int Count);

/// <summary>
/// Публичная часть последнего collection run. Внутренние идентификаторы и текст ошибок
/// намеренно исключены: они принадлежат административной диагностике.
/// </summary>
public sealed record PublicCollectionRunResponse(
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int SourcesProcessed,
    int SourcesSucceeded,
    int SourcesFailed,
    int SourcesSkipped,
    int SourcesTruncated,
    int CandidatesFound,
    bool CandidateLimitReached,
    int NewProxies,
    int AliveProxies,
    string Status)
{
    internal static PublicCollectionRunResponse From(CollectionRun run) => new(
        run.StartedAt,
        run.FinishedAt,
        run.SourcesProcessed,
        run.SourcesSucceeded,
        run.SourcesFailed,
        run.SourcesSkipped,
        run.SourcesTruncated,
        run.CandidatesFound,
        run.CandidateLimitReached,
        run.NewProxies,
        run.AliveProxies,
        run.Status);
}
