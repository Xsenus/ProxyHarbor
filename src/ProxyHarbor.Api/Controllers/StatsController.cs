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
    [HttpGet]
    [OutputCache(PolicyName = "public-summary")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        await using var proxyDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var sourceDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var runDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var now = DateTimeOffset.UtcNow;
        // Один агрегирующий проход заменяет семь отдельных сканирований большой таблицы Proxies.
        var proxyTask = proxyDb.Proxies.AsNoTracking().GroupBy(x => new { x.Status, x.Protocol }).Select(group => new
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
                proxy.LastCheckedAt >= freshAfter && proxy.LatencyMs != null).Sum(proxy => (long?)proxy.LatencyMs) ?? 0,
            FreshLatencySamples = group.Count(proxy => proxy.Status == ProxyStatus.Alive &&
                proxy.LastCheckedAt >= freshAfter && proxy.LatencyMs != null)
        }).ToListAsync(cancellationToken);
        var sourceTask = sourceDb.Sources.AsNoTracking().Where(x => x.Enabled).GroupBy(_ => 1).Select(x => new
        {
            Enabled = x.Count(),
            Failing = x.Count(source => source.ConsecutiveFailures > 0),
            RepeatedlyFailing = x.Count(source => source.ConsecutiveFailures >= 3),
            Truncated = x.Count(source => source.LastResultTruncated)
        }).FirstOrDefaultAsync(cancellationToken);
        var lastRunTask = runDb.Runs.AsNoTracking().OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        await Task.WhenAll(proxyTask, sourceTask, lastRunTask);

        var proxyRows = await proxyTask;
        var sourceHealth = await sourceTask;
        var lastRun = await lastRunTask;
        var alive = proxyRows.Sum(row => row.FreshAlive);
        var latencySamples = proxyRows.Sum(row => row.FreshLatencySamples);
        var latencyTotal = proxyRows.Sum(row => row.FreshLatencyTotal);
        var byProtocol = proxyRows.GroupBy(row => row.Protocol).Select(group => new
        {
            protocol = group.Key,
            count = group.Sum(row => row.FreshAlive)
        }).Where(row => row.count > 0).ToList();
        return Ok(new
        {
            alive,
            staleAlive = proxyRows.Sum(row => row.StaleAlive),
            pending = proxyRows.Where(row => row.Status == ProxyStatus.Pending).Sum(row => row.Total),
            dead = proxyRows.Where(row => row.Status == ProxyStatus.Dead).Sum(row => row.Total),
            dueForCheck = proxyRows.Sum(row => row.Due),
            checksInProgress = proxyRows.Sum(row => row.Leased),
            scheduledChecks = proxyRows.Sum(row => row.Scheduled),
            averageLatencyMs = latencySamples == 0 ? (double?)null : (double)latencyTotal / latencySamples,
            sources = sourceHealth?.Enabled ?? 0,
            failingSources = sourceHealth?.Failing ?? 0,
            repeatedlyFailingSources = sourceHealth?.RepeatedlyFailing ?? 0,
            truncatedSources = sourceHealth?.Truncated ?? 0,
            byProtocol,
            lastRun
        });
    }
}
