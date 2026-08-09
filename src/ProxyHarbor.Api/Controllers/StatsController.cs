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
    [OutputCache(PolicyName = "public-short")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var alive = db.Proxies.AsNoTracking().Where(x =>
            x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter);
        var statusCounts = await db.Proxies.AsNoTracking().GroupBy(x => x.Status)
            .Select(x => new { status = x.Key, count = x.Count() }).ToDictionaryAsync(x => x.status, x => x.count, cancellationToken);
        var byProtocol = await alive.GroupBy(x => x.Protocol).Select(x => new { protocol = x.Key, count = x.Count() }).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var schedule = await db.Proxies.AsNoTracking().GroupBy(_ => 1).Select(x => new
        {
            due = x.Count(proxy => proxy.NextCheckAt == null || proxy.NextCheckAt <= now),
            scheduled = x.Count(proxy => proxy.NextCheckAt > now)
        }).FirstOrDefaultAsync(cancellationToken);
        var lastRun = await db.Runs.AsNoTracking().OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(cancellationToken);
        var sourceHealth = await db.Sources.AsNoTracking().Where(x => x.Enabled).GroupBy(_ => 1).Select(x => new
        {
            enabled = x.Count(),
            failing = x.Count(source => source.ConsecutiveFailures > 0),
            repeatedlyFailing = x.Count(source => source.ConsecutiveFailures >= 3)
        }).FirstOrDefaultAsync(cancellationToken);
        return Ok(new
        {
            alive = await alive.CountAsync(cancellationToken),
            staleAlive = await db.Proxies.AsNoTracking().CountAsync(x =>
                x.Status == ProxyStatus.Alive && (x.LastCheckedAt == null || x.LastCheckedAt < freshAfter), cancellationToken),
            pending = statusCounts.GetValueOrDefault(ProxyStatus.Pending),
            dead = statusCounts.GetValueOrDefault(ProxyStatus.Dead),
            dueForCheck = schedule?.due ?? 0,
            scheduledChecks = schedule?.scheduled ?? 0,
            averageLatencyMs = await alive.AverageAsync(x => (double?)x.LatencyMs, cancellationToken),
            sources = sourceHealth?.enabled ?? 0,
            failingSources = sourceHealth?.failing ?? 0,
            repeatedlyFailingSources = sourceHealth?.repeatedlyFailing ?? 0,
            byProtocol,
            lastRun
        });
    }
}
