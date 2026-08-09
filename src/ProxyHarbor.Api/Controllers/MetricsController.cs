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
    IOptions<CollectorOptions> collectorOptions,
    ProbeControlHealth probeControlHealth) : ControllerBase
{
    [HttpGet]
    [Produces("text/plain")]
    [OutputCache(PolicyName = "public-summary")]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var now = DateTimeOffset.UtcNow;
        var freshAfter = now.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var validationWindowStart = now.AddMinutes(-5);
        var sourceFreshAfter = now.Subtract(
            SourceCatalogHealth.FreshnessWindow(collectorOptions.Value.CollectionIntervalMinutes));
        var proxyCounts = await db.Proxies.AsNoTracking()
            .GroupBy(x => new { x.Status, x.Protocol })
            .Select(x => new { x.Key.Status, x.Key.Protocol, Count = x.Count() })
            .ToListAsync(token);
        var queue = await db.Proxies.AsNoTracking().GroupBy(_ => 1).Select(x => new
        {
            Due = x.Count(proxy => proxy.NextCheckAt == null || proxy.NextCheckAt <= now),
            Leased = x.Count(proxy => proxy.CheckLeaseUntil > now),
            NeverAttempted = x.Count(proxy => proxy.LastValidationAttemptAt == null),
            LastAttemptAt = x.Max(proxy => proxy.LastValidationAttemptAt)
        }).FirstOrDefaultAsync(token);
        var validationRuns = await db.ValidationRuns.AsNoTracking()
            .Where(run => run.FinishedAt >= validationWindowStart || run.Status == "running")
            .ToListAsync(token);
        var validationTelemetry = ValidationTelemetry.Calculate(
            validationRuns, validationWindowStart, queue?.Due ?? 0);
        var sources = await db.Sources.AsNoTracking().GroupBy(_ => 1).Select(group => new
        {
            Enabled = group.Count(source => source.Enabled),
            Failing = group.Count(source => source.Enabled &&
                (source.ConsecutiveFailures > 0 || source.LastError != null)),
            Healthy = group.Count(source => source.Enabled && source.LastSucceededAt != null &&
                source.LastSucceededAt >= sourceFreshAfter && source.LastFetchedAt >= sourceFreshAfter &&
                source.LastItemCount > 0 && !source.LastResultTruncated &&
                source.ConsecutiveFailures == 0 && source.LastError == null),
            NeverAudited = group.Count(source => source.Enabled && source.LastFetchedAt == null),
            Stale = group.Count(source => source.Enabled && source.LastFetchedAt != null &&
                source.LastFetchedAt < sourceFreshAfter),
            Truncated = group.Count(source => source.Enabled && source.LastResultTruncated)
        }).FirstOrDefaultAsync(token);
        // Пользователь может добавить сколько угодно собственных feed'ов. В память загружаются
        // только максимум 81 каноническая запись, нужная для catalog-specific расчёта.
        var builtInUrls = BuiltInSourceCatalog.Sources.Select(source => source.Url).ToArray();
        var builtInSources = await db.Sources.AsNoTracking()
            .Where(source => builtInUrls.Contains(source.Url)).ToListAsync(token);
        var sourceCatalog = SourceCatalogHealth.Calculate(
            builtInSources,
            now,
            SourceCatalogHealth.FreshnessWindow(collectorOptions.Value.CollectionIntervalMinutes));
        // Текущий running-цикл не должен обнулять показатели последнего действительно завершённого запуска.
        var lastFinishedRun = await db.Runs.AsNoTracking().Where(x => x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).FirstOrDefaultAsync(token);
        var lastSuccessfulRun = await db.Runs.AsNoTracking().Where(x => x.Status == "completed" && x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).FirstOrDefaultAsync(token);
        var activeRuns = await db.Runs.AsNoTracking().CountAsync(x => x.Status == "running" && x.FinishedAt == null, token);
        var lastFinishedBackup = await db.BackupRuns.AsNoTracking().Where(x => x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).FirstOrDefaultAsync(token);
        var lastSuccessfulBackup = await db.BackupRuns.AsNoTracking().Where(x => x.Status == "completed" && x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).FirstOrDefaultAsync(token);
        var activeBackups = await db.BackupRuns.AsNoTracking()
            .CountAsync(x => x.Status == "running" && x.FinishedAt == null, token);

        var output = new StringBuilder(1_024);
        output.AppendLine("# HELP proxyharbor_proxies Number of known proxies by status and protocol.");
        output.AppendLine("# TYPE proxyharbor_proxies gauge");
        foreach (var row in proxyCounts.OrderBy(x => x.Status).ThenBy(x => x.Protocol))
            output.Append("proxyharbor_proxies{status=\"").Append(row.Status.ToString().ToLowerInvariant())
                .Append("\",protocol=\"").Append(row.Protocol.ToString().ToLowerInvariant()).Append("\"} ")
                .AppendLine(row.Count.ToString(CultureInfo.InvariantCulture));
        Gauge(output, "proxyharbor_validation_due", "Proxy records currently due for validation.", queue?.Due ?? 0);
        Gauge(output, "proxyharbor_validation_leased", "Proxy records currently leased by validators.", queue?.Leased ?? 0);
        Gauge(output, "proxyharbor_validation_never_attempted", "Proxy records that have never completed a validation attempt.",
            queue?.NeverAttempted ?? 0);
        Gauge(output, "proxyharbor_validation_attempts_last_5m", "Validation attempts completed during the last five minutes.",
            validationTelemetry.Attempts);
        Gauge(output, "proxyharbor_validation_checked_last_5m", "Non-deferred proxy checks completed during the last five minutes.",
            validationTelemetry.Checked);
        Gauge(output, "proxyharbor_validation_alive_last_5m", "Checks marked Alive during the last five minutes.",
            validationTelemetry.Alive);
        Gauge(output, "proxyharbor_validation_deferred_last_5m", "Validation attempts deferred during the last five minutes.",
            validationTelemetry.Deferred);
        Gauge(output, "proxyharbor_validation_runs_failed_last_5m", "Validation batches failed during the last five minutes.",
            validationTelemetry.FailedRuns);
        Gauge(output, "proxyharbor_validation_runs_active", "Validation batches currently marked as active.",
            validationTelemetry.ActiveRuns);
        Gauge(output, "proxyharbor_validation_concurrency_limit", "Configured maximum concurrent validation probes.",
            collectorOptions.Value.ValidationConcurrency);
        Gauge(output, "proxyharbor_validation_batch_size", "Configured maximum proxies claimed by one validation batch.",
            collectorOptions.Value.ValidationBatchSize);
        GaugeDouble(output, "proxyharbor_validation_checks_per_second", "Exact persisted validation attempts per second over the last five minutes.",
            validationTelemetry.ChecksPerSecond);
        Gauge(output, "proxyharbor_validation_estimated_drain_seconds", "Estimated seconds to drain the currently due validation queue; zero means unavailable or empty.",
            validationTelemetry.EstimatedDrainSeconds ?? 0);
        Gauge(output, "proxyharbor_validation_last_attempt_timestamp_seconds", "Unix timestamp of the latest completed validation attempt.",
            queue?.LastAttemptAt?.ToUnixTimeSeconds() ?? 0);
        Gauge(output, "proxyharbor_probe_control_available", "Control endpoint health: 1 available, 0 unavailable, -1 not checked.",
            probeControlHealth.Availability);
        Gauge(output, "proxyharbor_probe_control_last_check_timestamp_seconds", "Unix timestamp of the latest control endpoint health check.",
            probeControlHealth.CheckedAtUnixSeconds);
        Gauge(output, "proxyharbor_sources_enabled", "Enabled proxy source feeds.", sources?.Enabled ?? 0);
        Gauge(output, "proxyharbor_sources_failing", "Enabled feeds whose latest fetch failed.", sources?.Failing ?? 0);
        Gauge(output, "proxyharbor_sources_healthy", "Enabled feeds with a fresh successful non-empty fetch.",
            sources?.Healthy ?? 0);
        Gauge(output, "proxyharbor_sources_never_audited", "Enabled feeds not fetched yet.", sources?.NeverAudited ?? 0);
        Gauge(output, "proxyharbor_sources_stale", "Enabled feeds whose latest fetch is older than three collection intervals.",
            sources?.Stale ?? 0);
        Gauge(output, "proxyharbor_sources_truncated", "Enabled feeds whose latest successful result exceeded the per-source limit.",
            sources?.Truncated ?? 0);
        Gauge(output, "proxyharbor_source_catalog_complete", "Whether every built-in feed and provider is present and enabled.",
            sourceCatalog.IsComplete ? 1 : 0);
        Gauge(output, "proxyharbor_source_catalog_healthy", "Whether every built-in feed has a fresh successful non-empty audit.",
            sourceCatalog.IsHealthy ? 1 : 0);
        Gauge(output, "proxyharbor_builtin_sources_expected", "Built-in feeds expected by this release.",
            sourceCatalog.ExpectedSources);
        Gauge(output, "proxyharbor_builtin_sources_present", "Built-in feeds currently present in the database.",
            sourceCatalog.PresentSources);
        Gauge(output, "proxyharbor_builtin_sources_enabled", "Built-in feeds currently enabled.",
            sourceCatalog.EnabledSources);
        Gauge(output, "proxyharbor_builtin_sources_healthy", "Built-in feeds with a fresh successful non-empty audit.",
            sourceCatalog.HealthySources);
        Gauge(output, "proxyharbor_builtin_sources_failing", "Enabled built-in feeds currently reporting a failure.",
            sourceCatalog.FailingSources);
        Gauge(output, "proxyharbor_builtin_sources_never_audited", "Enabled built-in feeds not fetched yet.",
            sourceCatalog.NeverAuditedSources);
        Gauge(output, "proxyharbor_builtin_sources_stale", "Enabled built-in feeds older than three collection intervals.",
            sourceCatalog.StaleSources);
        Gauge(output, "proxyharbor_builtin_sources_truncated", "Enabled built-in feeds whose latest result exceeded the per-source limit.",
            sourceCatalog.TruncatedSources);
        Gauge(output, "proxyharbor_builtin_providers_expected", "Independent built-in providers expected by this release.",
            sourceCatalog.ExpectedProviders);
        Gauge(output, "proxyharbor_builtin_providers_present", "Independent built-in providers represented in the database.",
            sourceCatalog.PresentProviders);
        Gauge(output, "proxyharbor_builtin_providers_enabled", "Independent built-in providers with at least one enabled feed.",
            sourceCatalog.EnabledProviders);
        Gauge(output, "proxyharbor_proxies_published", "Alive proxies fresh enough for public API and exports.",
            await db.Proxies.AsNoTracking().CountAsync(x => x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter, token));
        Gauge(output, "proxyharbor_collection_runs_active", "Collection runs currently marked as active.", activeRuns);
        Gauge(output, "proxyharbor_last_collection_success", "Whether the latest finished collection completed successfully.",
            lastFinishedRun?.Status == "completed" ? 1 : 0);
        Gauge(output, "proxyharbor_last_collection_candidates", "Candidates found by the latest finished collection run.",
            lastFinishedRun?.CandidatesFound ?? 0);
        Gauge(output, "proxyharbor_last_collection_sources_skipped", "Feeds skipped by adaptive failure backoff in the latest finished run.",
            lastFinishedRun?.SourcesSkipped ?? 0);
        Gauge(output, "proxyharbor_last_collection_sources_truncated", "Feeds truncated by the per-source limit in the latest finished run.",
            lastFinishedRun?.SourcesTruncated ?? 0);
        Gauge(output, "proxyharbor_last_collection_candidate_limit_reached", "Whether the latest finished run reached the global candidate limit.",
            lastFinishedRun?.CandidateLimitReached == true ? 1 : 0);
        Gauge(output, "proxyharbor_last_collection_timestamp_seconds", "Unix timestamp of the last collection completion.",
            lastFinishedRun?.FinishedAt?.ToUnixTimeSeconds() ?? 0);
        Gauge(output, "proxyharbor_last_successful_collection_timestamp_seconds", "Unix timestamp of the latest successful collection.",
            lastSuccessfulRun?.FinishedAt?.ToUnixTimeSeconds() ?? 0);
        GaugeDouble(output, "proxyharbor_last_collection_duration_seconds", "Duration of the latest finished collection in seconds.",
            lastFinishedRun?.FinishedAt is { } finishedAt
                ? Math.Max(0, (finishedAt - lastFinishedRun.StartedAt).TotalSeconds)
                : 0);
        Gauge(output, "proxyharbor_backup_runs_active", "Backup runs currently marked as active.", activeBackups);
        Gauge(output, "proxyharbor_last_backup_success", "Whether the latest finished backup completed successfully.",
            lastFinishedBackup?.Status == "completed" ? 1 : 0);
        Gauge(output, "proxyharbor_last_backup_telegram_configured", "Whether Telegram was configured for the latest successful backup.",
            lastSuccessfulBackup?.TelegramConfigured == true ? 1 : 0);
        Gauge(output, "proxyharbor_last_backup_sent_to_telegram", "Whether the latest successful backup was delivered to Telegram.",
            lastSuccessfulBackup?.SentToTelegram == true ? 1 : 0);
        Gauge(output, "proxyharbor_last_backup_size_bytes", "Encrypted size of the latest successful backup.",
            lastSuccessfulBackup?.SizeBytes ?? 0);
        Gauge(output, "proxyharbor_last_backup_timestamp_seconds", "Unix timestamp of the latest successful backup completion.",
            lastSuccessfulBackup?.FinishedAt?.ToUnixTimeSeconds() ?? 0);
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
