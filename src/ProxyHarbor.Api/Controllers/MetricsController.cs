using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Минимальный Prometheus-совместимый endpoint без дополнительных runtime-зависимостей.</summary>
[ApiController, Route("metrics"), EnableRateLimiting("public"), ApiExplorerSettings(IgnoreApi = true)]
public sealed class MetricsController(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<CollectorOptions> collectorOptions) : ControllerBase
{
    [HttpGet]
    [Produces("text/plain")]
    [OutputCache(PolicyName = "public-summary")]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var now = DateTimeOffset.UtcNow;
        var freshAfter = now.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var proxyCounts = await db.Proxies.AsNoTracking()
            .GroupBy(x => new { x.Status, x.Protocol })
            .Select(x => new { x.Key.Status, x.Key.Protocol, Count = x.Count() })
            .ToListAsync(token);
        var queue = await db.Proxies.AsNoTracking().GroupBy(_ => 1).Select(x => new
        {
            Due = x.Count(proxy => proxy.NextCheckAt == null || proxy.NextCheckAt <= now),
            Leased = x.Count(proxy => proxy.CheckLeaseUntil > now)
        }).FirstOrDefaultAsync(token);
        var sources = await db.Sources.AsNoTracking().GroupBy(_ => 1).Select(x => new
        {
            Enabled = x.Count(source => source.Enabled),
            Failing = x.Count(source => source.Enabled && source.ConsecutiveFailures > 0)
        }).FirstOrDefaultAsync(token);
        // Текущий running-цикл не должен обнулять показатели последнего действительно завершённого запуска.
        var lastFinishedRun = await db.Runs.AsNoTracking().Where(x => x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).FirstOrDefaultAsync(token);
        var lastSuccessfulRun = await db.Runs.AsNoTracking().Where(x => x.Status == "completed" && x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).FirstOrDefaultAsync(token);
        var activeRuns = await db.Runs.AsNoTracking().CountAsync(x => x.Status == "running" && x.FinishedAt == null, token);

        var output = new StringBuilder(1_024);
        output.AppendLine("# HELP proxyharbor_proxies Number of known proxies by status and protocol.");
        output.AppendLine("# TYPE proxyharbor_proxies gauge");
        foreach (var row in proxyCounts.OrderBy(x => x.Status).ThenBy(x => x.Protocol))
            output.Append("proxyharbor_proxies{status=\"").Append(row.Status.ToString().ToLowerInvariant())
                .Append("\",protocol=\"").Append(row.Protocol.ToString().ToLowerInvariant()).Append("\"} ")
                .AppendLine(row.Count.ToString(CultureInfo.InvariantCulture));
        Gauge(output, "proxyharbor_validation_due", "Proxy records currently due for validation.", queue?.Due ?? 0);
        Gauge(output, "proxyharbor_validation_leased", "Proxy records currently leased by validators.", queue?.Leased ?? 0);
        Gauge(output, "proxyharbor_sources_enabled", "Enabled proxy source feeds.", sources?.Enabled ?? 0);
        Gauge(output, "proxyharbor_sources_failing", "Enabled feeds whose latest fetch failed.", sources?.Failing ?? 0);
        Gauge(output, "proxyharbor_sources_healthy", "Enabled feeds whose latest fetch succeeded.",
            (sources?.Enabled ?? 0) - (sources?.Failing ?? 0));
        Gauge(output, "proxyharbor_proxies_published", "Alive proxies fresh enough for public API and exports.",
            await db.Proxies.AsNoTracking().CountAsync(x => x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter, token));
        Gauge(output, "proxyharbor_collection_runs_active", "Collection runs currently marked as active.", activeRuns);
        Gauge(output, "proxyharbor_last_collection_success", "Whether the latest finished collection completed successfully.",
            lastFinishedRun?.Status == "completed" ? 1 : 0);
        Gauge(output, "proxyharbor_last_collection_candidates", "Candidates found by the latest finished collection run.",
            lastFinishedRun?.CandidatesFound ?? 0);
        Gauge(output, "proxyharbor_last_collection_timestamp_seconds", "Unix timestamp of the last collection completion.",
            lastFinishedRun?.FinishedAt?.ToUnixTimeSeconds() ?? 0);
        Gauge(output, "proxyharbor_last_successful_collection_timestamp_seconds", "Unix timestamp of the latest successful collection.",
            lastSuccessfulRun?.FinishedAt?.ToUnixTimeSeconds() ?? 0);
        GaugeDouble(output, "proxyharbor_last_collection_duration_seconds", "Duration of the latest finished collection in seconds.",
            lastFinishedRun?.FinishedAt is { } finishedAt
                ? Math.Max(0, (finishedAt - lastFinishedRun.StartedAt).TotalSeconds)
                : 0);
        return Content(output.ToString(), "text/plain; version=0.0.4; charset=utf-8", Encoding.UTF8);
    }

    private static void Gauge(StringBuilder output, string name, string help, long value)
    {
        output.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        output.Append("# TYPE ").Append(name).AppendLine(" gauge");
        output.Append(name).Append(' ').AppendLine(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void GaugeDouble(StringBuilder output, string name, string help, double value)
    {
        output.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        output.Append("# TYPE ").Append(name).AppendLine(" gauge");
        output.Append(name).Append(' ').AppendLine(value.ToString("0.###", CultureInfo.InvariantCulture));
    }
}
