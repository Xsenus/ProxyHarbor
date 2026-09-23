using System.Data.Common;
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
    IOptions<BackupOptions> backupOptions,
    ProbeControlHealth probeControlHealth,
    OperationalMaintenanceService? maintenance = null,
    HttpRequestTelemetry? httpTelemetry = null,
    ProxyMetricsSnapshotCache? proxySnapshotCache = null,
    ValidationClaimIdleGate? validationIdleGate = null,
    CheckerNodeCredentialCache? checkerCredentialCache = null,
    VpnMetricsSnapshotCache? vpnSnapshotCache = null,
    IBackupConfigurationStore? backupConfigurationStore = null,
    ILogger<MetricsController>? logger = null,
    IOptions<BackupRoutingOptions>? backupRoutingOptions = null,
    BackupProtectionEvaluator? backupProtectionEvaluator = null) : ControllerBase
{
    private static readonly Action<ILogger, Exception?> BackupConfigurationReadFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1701, "BackupConfigurationReadFailed"),
            "Не удалось прочитать runtime-настройки резервного копирования для метрик.");

    /// <summary>Возвращает согласованный Prometheus text exposition operational-метрик.</summary>
    [HttpGet]
    [Produces("text/plain")]
    [OutputCache(PolicyName = PublicOutputCachePolicies.Metrics)]
    public async Task<IActionResult> Get(CancellationToken requestToken)
    {
        var (effectiveBackupOptions, backupConfigurationReadSucceeded) =
            await ResolveBackupOptionsAsync(requestToken);
        var cachedProxySnapshot = proxySnapshotCache is null
            ? null
            : await proxySnapshotCache.GetPassiveAsync(requestToken);
        var cachedVpnSnapshot = vpnSnapshotCache is null
            ? null
            : await vpnSnapshotCache.GetPassiveAsync(requestToken);
        await using var db = await dbFactory.CreateDbContextAsync(requestToken);
        return await BufferedReadSnapshot.ExecuteAsync(
            db, token => GetSnapshotAsync(
                db, cachedProxySnapshot, cachedVpnSnapshot, effectiveBackupOptions,
                backupConfigurationReadSucceeded, token), requestToken);
    }

    private async Task<(BackupOptions Options, bool ReadSucceeded)> ResolveBackupOptionsAsync(
        CancellationToken token)
    {
        if (backupConfigurationStore is null) return (backupOptions.Value, true);
        try
        {
            return (await backupConfigurationStore.GetAsync(token), true);
        }
        catch (Exception exception) when (
            !token.IsCancellationRequested &&
            exception is InvalidOperationException or DbException or TimeoutException)
        {
            if (logger is not null) BackupConfigurationReadFailed(logger, exception);
            return (backupOptions.Value, false);
        }
    }

    /// <summary>Строит весь database-derived exposition внутри уже открытого read snapshot.</summary>
    private async Task<IActionResult> GetSnapshotAsync(
        ProxyHarborDbContext db,
        ProxyMetricsSnapshot? cachedProxySnapshot,
        VpnMetricsSnapshot? cachedVpnSnapshot,
        BackupOptions effectiveBackupOptions,
        bool backupConfigurationReadSucceeded,
        CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var freshAfter = now.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var unseenRetentionCutoff = now.AddDays(-Math.Max(1, collectorOptions.Value.DeadRetentionDays));
        var validationWindowStart = now.AddMinutes(-5);
        var sourceFreshAfter = now.Subtract(
            SourceCatalogHealth.FreshnessWindow(collectorOptions.Value.CollectionIntervalMinutes));
        var proxySnapshot = cachedProxySnapshot ?? await ProxyMetricsSnapshotReader.ReadAsync(
            db, now, unseenRetentionCutoff, freshAfter, token);
        var vpnSnapshot = cachedVpnSnapshot ?? await VpnMetricsSnapshotReader.ReadAsync(
            db, now, collectorOptions.Value.VpnPublicFreshnessMinutes, token);
        var validationRuns = await db.ValidationRuns.AsNoTracking()
            .Where(run => run.FinishedAt >= validationWindowStart || run.Status == "running")
            .ToListAsync(token);
        var validationTelemetry = ValidationTelemetry.Calculate(
            validationRuns, validationWindowStart, proxySnapshot.Due);
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
        }).SingleOrDefaultAsync(token);
        // Пользователь может добавить сколько угодно собственных feed'ов. В память загружаются
        // только максимум 81 каноническая запись, нужная для catalog-specific расчёта.
        var builtInUrls = BuiltInSourceCatalog.Sources.Select(source => source.Url).ToArray();
        var builtInSources = await db.Sources.AsNoTracking()
            .Where(source => builtInUrls.Contains(source.Url)).ToListAsync(token);
        var sourceCatalog = SourceCatalogHealth.Calculate(
            builtInSources,
            now,
            SourceCatalogHealth.FreshnessWindow(collectorOptions.Value.CollectionIntervalMinutes));
        var vpnSources = await db.VpnSources.AsNoTracking().GroupBy(_ => 1).Select(group => new
        {
            Enabled = group.Count(source => source.Enabled),
            Failing = group.Count(source => source.Enabled &&
                (source.ConsecutiveFailures > 0 || source.LastError != null)),
            Healthy = group.Count(source => source.Enabled && source.LastSucceededAt != null &&
                source.LastSucceededAt >= sourceFreshAfter && source.LastFetchedAt >= sourceFreshAfter &&
                source.LastItemCount > 0 && source.ConsecutiveFailures == 0 && source.LastError == null),
            NeverAudited = group.Count(source => source.Enabled && source.LastFetchedAt == null),
            Stale = group.Count(source => source.Enabled && source.LastFetchedAt != null &&
                source.LastFetchedAt < sourceFreshAfter)
        }).SingleOrDefaultAsync(token);
        var builtInVpnUrls = BuiltInVpnSourceCatalog.Sources.Select(source => source.Url).ToArray();
        var builtInVpnSources = await db.VpnSources.AsNoTracking()
            .Where(source => builtInVpnUrls.Contains(source.Url)).ToListAsync(token);
        var vpnSourceCatalog = VpnSourceCatalogHealth.Calculate(
            builtInVpnSources,
            now,
            SourceCatalogHealth.FreshnessWindow(collectorOptions.Value.CollectionIntervalMinutes));
        // Текущий running-цикл не должен обнулять показатели последнего действительно завершённого запуска.
        // На PostgreSQL шесть прежних point-read запросов объединены в один statement и один MVCC snapshot.
        var runMetrics = await ReadRunMetricsAsync(db, token);
        var protectionMetrics = await ReadBackupProtectionMetricsAsync(db, runMetrics, now, token);

        var output = new StringBuilder(16_384);
        (httpTelemetry ?? new HttpRequestTelemetry()).AppendPrometheus(output);
        output.AppendLine("# HELP proxyharbor_proxies Number of known proxies by status and protocol.");
        output.AppendLine("# TYPE proxyharbor_proxies gauge");
        foreach (var row in proxySnapshot.Groups)
            output.Append("proxyharbor_proxies{status=\"").Append(row.Status.ToString().ToLowerInvariant())
                .Append("\",protocol=\"").Append(row.Protocol.ToString().ToLowerInvariant()).Append("\"} ")
                .AppendLine(row.Count.ToString(CultureInfo.InvariantCulture));
        Gauge(output, "proxyharbor_validation_due", "Unleased proxy records currently eligible for validation.", proxySnapshot.Due);
        Gauge(output, "proxyharbor_validation_leased", "Proxy records currently leased by validators.", proxySnapshot.Leased);
        Gauge(output, "proxyharbor_validation_never_attempted", "Proxy records that have never completed a validation attempt.",
            proxySnapshot.NeverAttempted);
        Gauge(output, "proxyharbor_proxies_stale_unseen", "Unleased never-alive Pending or Dead proxies past source-membership retention and awaiting cleanup.",
            proxySnapshot.StaleUnseen);
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
        Counter(output, "proxyharbor_validation_empty_claims_coalesced_total",
            "Empty checker lease polls served without a full validation queue claim on this API replica.",
            validationIdleGate?.CoalescedClaims ?? 0);
        Counter(output, "proxyharbor_validation_empty_claims_serialized_total",
            "Concurrent empty checker polls skipped after waiting for the cluster-wide claim lock.",
            validationIdleGate?.SerializedCoalescedClaims ?? 0);
        Gauge(output, "proxyharbor_validation_empty_claim_cooldown_active",
            "Whether the short process-local empty validation queue cooldown is currently active.",
            validationIdleGate?.CooldownActive == true ? 1 : 0);
        Counter(output, "proxyharbor_checker_auth_attempts_total",
            "Checker-agent authentication attempts handled by this API replica.",
            checkerCredentialCache?.AuthenticationAttempts ?? 0);
        Counter(output, "proxyharbor_checker_auth_failures_total",
            "Checker-agent authentication attempts rejected by this API replica.",
            checkerCredentialCache?.AuthenticationFailures ?? 0);
        Counter(output, "proxyharbor_checker_auth_snapshot_hits_total",
            "Checker-agent authentication attempts served from an existing credential snapshot.",
            checkerCredentialCache?.SnapshotHits ?? 0);
        Counter(output, "proxyharbor_checker_auth_database_reads_total",
            "Enabled checker credential snapshots read from PostgreSQL by this API replica.",
            checkerCredentialCache?.DatabaseReads ?? 0);
        Counter(output, "proxyharbor_checker_auth_invalidations_total",
            "Credential snapshots invalidated after checker-node administration on this API replica.",
            checkerCredentialCache?.Invalidations ?? 0);
        Gauge(output, "proxyharbor_background_workers_enabled", "Whether built-in collection and validation workers are enabled.",
            collectorOptions.Value.BackgroundWorkersEnabled ? 1 : 0);
        Counter(output, "proxyharbor_advisory_lock_cleanup_failures_total",
            "Advisory-lock lease cleanup incidents observed by this API replica.",
            DatabaseRuntimeGate.AdvisoryLockCleanupFailures);
        Gauge(output, "proxyharbor_maintenance_last_success_timestamp_seconds", "Unix timestamp of this replica's latest successful cluster maintenance run.",
            maintenance?.LastSuccessUnixSeconds ?? 0);
        Gauge(output, "proxyharbor_maintenance_last_failure_timestamp_seconds", "Unix timestamp of this replica's latest failed maintenance attempt.",
            maintenance?.LastFailureUnixSeconds ?? 0);
        Gauge(output, "proxyharbor_maintenance_last_deleted_rows", "Rows deleted by this replica's latest successful maintenance run.",
            maintenance?.LastDeletedRows ?? 0);
        Gauge(output, "proxyharbor_maintenance_last_recovered_rows", "Abandoned running audits recovered by this replica's latest successful maintenance run.",
            maintenance?.LastRecoveredRows ?? 0);
        Gauge(output, "proxyharbor_maintenance_healthy", "Latest maintenance outcome on this replica: 1 success, 0 failure, -1 not attempted.",
            maintenance?.Status ?? -1);
        Gauge(output, "proxyharbor_collection_interval_seconds", "Configured interval between collection cycles in seconds.",
            collectorOptions.Value.CollectionIntervalMinutes * 60L);
        Gauge(output, "proxyharbor_public_freshness_seconds", "Maximum validation age accepted by public API and exports.",
            collectorOptions.Value.PublicFreshnessMinutes * 60L);
        Gauge(output, "proxyharbor_validation_concurrency_limit", "Configured maximum concurrent validation probes.",
            collectorOptions.Value.ValidationConcurrency);
        Gauge(output, "proxyharbor_validation_batch_size", "Configured maximum proxies claimed by one validation batch.",
            collectorOptions.Value.ValidationBatchSize);
        GaugeDouble(output, "proxyharbor_validation_checks_per_second", "Exact persisted validation attempts per second over the last five minutes.",
            validationTelemetry.ChecksPerSecond);
        Gauge(output, "proxyharbor_validation_estimated_drain_seconds", "Estimated seconds to drain the currently due validation queue; zero means unavailable or empty.",
            validationTelemetry.EstimatedDrainSeconds ?? 0);
        Gauge(output, "proxyharbor_validation_last_attempt_timestamp_seconds", "Unix timestamp of the latest completed validation attempt.",
            proxySnapshot.LastAttemptAt?.ToUnixTimeSeconds() ?? 0);
        GaugeDouble(output, "proxyharbor_proxy_snapshot_age_seconds",
            "Age of the shared exact proxy aggregate currently served by /stats and /metrics.",
            Math.Max(0, (now - proxySnapshot.CapturedAt).TotalSeconds));
        Counter(output, "proxyharbor_proxy_snapshot_database_reads_total",
            "Exact full-table proxy aggregates executed by this API replica.",
            proxySnapshotCache?.DatabaseReads ?? 0);
        Counter(output, "proxyharbor_proxy_snapshot_refresh_requests_total",
            "Stale snapshot demand signals queued for background refresh.",
            proxySnapshotCache?.RefreshRequestsQueued ?? 0);
        Counter(output, "proxyharbor_proxy_snapshot_refresh_requests_coalesced_total",
            "Stale snapshot demand signals coalesced behind an already queued refresh.",
            proxySnapshotCache?.RefreshRequestsCoalesced ?? 0);
        Gauge(output, "proxyharbor_vpn_endpoints", "Known VPN endpoint records.", vpnSnapshot.Total);
        Gauge(output, "proxyharbor_vpn_endpoints_reachable", "VPN endpoints whose latest TCP probe succeeded.", vpnSnapshot.Reachable);
        Gauge(output, "proxyharbor_vpn_endpoints_pending", "VPN endpoints awaiting their first classification.", vpnSnapshot.Pending);
        Gauge(output, "proxyharbor_vpn_endpoints_unreachable", "VPN endpoints whose latest TCP probe failed.", vpnSnapshot.Unreachable);
        Gauge(output, "proxyharbor_vpn_endpoints_unsupported", "VPN endpoints requiring a protocol-aware probe.", vpnSnapshot.Unsupported);
        Gauge(output, "proxyharbor_vpn_validation_never_checked", "VPN endpoints without a completed validation.", vpnSnapshot.NeverChecked);
        Gauge(output, "proxyharbor_vpn_validation_due", "VPN endpoints currently eligible for validation.", vpnSnapshot.Due);
        Gauge(output, "proxyharbor_vpn_published", "Fresh reachable VPN endpoints eligible for public output.", vpnSnapshot.FreshReachable);
        Gauge(output, "proxyharbor_vpn_stale_reachable", "Reachable VPN endpoints older than the public freshness window.", vpnSnapshot.StaleReachable);
        Gauge(output, "proxyharbor_vpn_validation_checked_last_5m", "VPN endpoints validated during the last five minutes.", vpnSnapshot.CheckedLastFiveMinutes);
        GaugeDouble(output, "proxyharbor_vpn_validation_checks_per_second", "VPN validations per second over the last five minutes.",
            vpnSnapshot.CheckedLastFiveMinutes / 300d);
        Gauge(output, "proxyharbor_vpn_validation_estimated_drain_seconds", "Estimated seconds to drain the currently due VPN queue; zero means unavailable or empty.",
            vpnSnapshot.CheckedLastFiveMinutes > 0
                ? (long)Math.Ceiling(vpnSnapshot.Due * 300d / vpnSnapshot.CheckedLastFiveMinutes)
                : 0);
        Gauge(output, "proxyharbor_vpn_validation_last_check_timestamp_seconds", "Unix timestamp of the latest VPN validation.",
            vpnSnapshot.LatestCheckedAt?.ToUnixTimeSeconds() ?? 0);
        Gauge(output, "proxyharbor_vpn_validation_concurrency_limit", "Configured maximum concurrent VPN probes.",
            collectorOptions.Value.VpnValidationConcurrency);
        Gauge(output, "proxyharbor_vpn_validation_batch_size", "Configured maximum VPN endpoints selected per validation batch.",
            collectorOptions.Value.VpnValidationBatchSize);
        Gauge(output, "proxyharbor_vpn_reachable_validation_interval_seconds", "Configured interval between successful VPN probes.",
            collectorOptions.Value.VpnReachableValidationIntervalMinutes * 60L);
        Gauge(output, "proxyharbor_vpn_unreachable_retry_seconds", "Configured interval between failed VPN probes.",
            collectorOptions.Value.VpnUnreachableRetryMinutes * 60L);
        Gauge(output, "proxyharbor_vpn_unsupported_retry_seconds", "Configured interval between unsupported VPN classifications.",
            collectorOptions.Value.VpnUnsupportedRetryMinutes * 60L);
        Gauge(output, "proxyharbor_vpn_public_freshness_seconds", "Maximum VPN validation age accepted by public outputs.",
            collectorOptions.Value.VpnPublicFreshnessMinutes * 60L);
        GaugeDouble(output, "proxyharbor_vpn_snapshot_age_seconds", "Age of the shared exact VPN aggregate.",
            Math.Max(0, (now - vpnSnapshot.CapturedAt).TotalSeconds));
        Counter(output, "proxyharbor_vpn_snapshot_database_reads_total", "Exact full-table VPN aggregates executed by this API replica.",
            vpnSnapshotCache?.DatabaseReads ?? 0);
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
        Gauge(output, "proxyharbor_builtin_catalog_audit_timestamp_seconds",
            "UTC midnight Unix timestamp of the latest full release audit of every built-in feed.",
            new DateTimeOffset(
                sourceCatalog.LastAuditedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds());
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
        Gauge(output, "proxyharbor_vpn_sources_enabled", "Enabled VPN source feeds.", vpnSources?.Enabled ?? 0);
        Gauge(output, "proxyharbor_vpn_sources_failing", "Enabled VPN feeds whose latest fetch failed.",
            vpnSources?.Failing ?? 0);
        Gauge(output, "proxyharbor_vpn_sources_healthy", "Enabled VPN feeds with a fresh successful non-empty fetch.",
            vpnSources?.Healthy ?? 0);
        Gauge(output, "proxyharbor_vpn_sources_never_audited", "Enabled VPN feeds not fetched yet.",
            vpnSources?.NeverAudited ?? 0);
        Gauge(output, "proxyharbor_vpn_sources_stale", "Enabled VPN feeds whose latest fetch is older than three collection intervals.",
            vpnSources?.Stale ?? 0);
        Gauge(output, "proxyharbor_vpn_source_catalog_complete", "Whether every built-in VPN feed and provider is present and enabled.",
            vpnSourceCatalog.IsComplete ? 1 : 0);
        Gauge(output, "proxyharbor_vpn_source_catalog_healthy", "Whether every built-in VPN feed has a fresh successful non-empty audit.",
            vpnSourceCatalog.IsHealthy ? 1 : 0);
        Gauge(output, "proxyharbor_builtin_vpn_catalog_audit_timestamp_seconds",
            "UTC midnight Unix timestamp of the latest full release audit of every built-in VPN feed.",
            new DateTimeOffset(
                vpnSourceCatalog.LastAuditedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds());
        Gauge(output, "proxyharbor_builtin_vpn_sources_expected", "Built-in VPN feeds expected by this release.",
            vpnSourceCatalog.ExpectedSources);
        Gauge(output, "proxyharbor_builtin_vpn_sources_present", "Built-in VPN feeds currently present in the database.",
            vpnSourceCatalog.PresentSources);
        Gauge(output, "proxyharbor_builtin_vpn_sources_enabled", "Built-in VPN feeds currently enabled.",
            vpnSourceCatalog.EnabledSources);
        Gauge(output, "proxyharbor_builtin_vpn_sources_healthy", "Built-in VPN feeds with a fresh successful non-empty audit.",
            vpnSourceCatalog.HealthySources);
        Gauge(output, "proxyharbor_builtin_vpn_sources_failing", "Enabled built-in VPN feeds currently reporting a failure.",
            vpnSourceCatalog.FailingSources);
        Gauge(output, "proxyharbor_builtin_vpn_sources_never_audited", "Enabled built-in VPN feeds not fetched yet.",
            vpnSourceCatalog.NeverAuditedSources);
        Gauge(output, "proxyharbor_builtin_vpn_sources_stale", "Enabled built-in VPN feeds older than three collection intervals.",
            vpnSourceCatalog.StaleSources);
        Gauge(output, "proxyharbor_builtin_vpn_providers_expected", "Independent built-in VPN providers expected by this release.",
            vpnSourceCatalog.ExpectedProviders);
        Gauge(output, "proxyharbor_builtin_vpn_providers_present", "Independent built-in VPN providers represented in the database.",
            vpnSourceCatalog.PresentProviders);
        Gauge(output, "proxyharbor_builtin_vpn_providers_enabled", "Independent built-in VPN providers with at least one enabled feed.",
            vpnSourceCatalog.EnabledProviders);
        Gauge(output, "proxyharbor_proxies_published", "Alive proxies fresh enough for public API and exports.",
            proxySnapshot.Published);
        Gauge(output, "proxyharbor_collection_runs_active", "Collection runs currently marked as active.",
            runMetrics.ActiveCollectionRuns);
        Gauge(output, "proxyharbor_last_collection_success", "Whether the latest finished collection completed successfully.",
            runMetrics.LastCollectionStatus == "completed" ? 1 : 0);
        Gauge(output, "proxyharbor_last_collection_candidates", "Candidates found by the latest finished collection run.",
            runMetrics.LastCollectionCandidates);
        Gauge(output, "proxyharbor_last_collection_sources_skipped", "Feeds skipped by adaptive failure backoff in the latest finished run.",
            runMetrics.LastCollectionSourcesSkipped);
        Gauge(output, "proxyharbor_last_collection_sources_truncated", "Feeds truncated by the per-source limit in the latest finished run.",
            runMetrics.LastCollectionSourcesTruncated);
        Gauge(output, "proxyharbor_last_collection_candidate_limit_reached", "Whether the latest finished run reached the global candidate limit.",
            runMetrics.LastCollectionCandidateLimitReached ? 1 : 0);
        Gauge(output, "proxyharbor_last_collection_timestamp_seconds", "Unix timestamp of the last collection completion.",
            runMetrics.LastCollectionFinishedAt?.ToUnixTimeSeconds() ?? 0);
        Gauge(output, "proxyharbor_last_successful_collection_timestamp_seconds", "Unix timestamp of the latest successful collection.",
            runMetrics.LastSuccessfulCollectionAt?.ToUnixTimeSeconds() ?? 0);
        GaugeDouble(output, "proxyharbor_last_collection_duration_seconds", "Duration of the latest finished collection in seconds.",
            runMetrics.LastCollectionFinishedAt is { } finishedAt && runMetrics.LastCollectionStartedAt is { } startedAt
                ? Math.Max(0, (finishedAt - startedAt).TotalSeconds)
                : 0);
        Gauge(output, "proxyharbor_backup_configuration_read_success", "Whether the effective runtime backup configuration was read successfully.",
            backupConfigurationReadSucceeded ? 1 : 0);
        Gauge(output, "proxyharbor_backup_enabled", "Whether scheduled encrypted backups are enabled.",
            effectiveBackupOptions.Enabled ? 1 : 0);
        Gauge(output, "proxyharbor_backup_interval_seconds", "Configured interval between scheduled backups in seconds.",
            effectiveBackupOptions.IntervalHours * 3_600L);
        var backupTelegramConfigured = effectiveBackupOptions.TelegramRecipientId.HasValue ||
            !string.IsNullOrWhiteSpace(effectiveBackupOptions.TelegramBotToken) &&
            !string.IsNullOrWhiteSpace(effectiveBackupOptions.TelegramChatId);
        Gauge(output, "proxyharbor_backup_telegram_configured", "Whether a CRM recipient or the legacy Telegram delivery pair is configured.",
            backupTelegramConfigured ? 1 : 0);
        Gauge(output, "proxyharbor_backup_object_storage_configured", "Whether effective runtime settings contain a valid enabled object-storage destination.",
            effectiveBackupOptions.SendToObjectStorage &&
            BackupOptions.IsObjectStorageConfigurationValid(effectiveBackupOptions) ? 1 : 0);
        Gauge(output, "proxyharbor_backup_runs_active", "Backup runs currently marked as active.",
            runMetrics.ActiveBackupRuns);
        Gauge(output, "proxyharbor_last_backup_success", "Whether the latest finished backup completed successfully.",
            runMetrics.LastBackupStatus == "completed" ? 1 : 0);
        Gauge(output, "proxyharbor_last_backup_telegram_configured", "Whether Telegram was configured for the latest successful backup.",
            runMetrics.LastSuccessfulBackupTelegramConfigured ? 1 : 0);
        Gauge(output, "proxyharbor_last_backup_sent_to_telegram", "Whether the latest successful backup was delivered to Telegram.",
            runMetrics.LastSuccessfulBackupSentToTelegram ? 1 : 0);
        Gauge(output, "proxyharbor_last_backup_size_bytes", "Encrypted size of the latest successful backup.",
            runMetrics.LastSuccessfulBackupSizeBytes);
        Gauge(output, "proxyharbor_last_backup_timestamp_seconds", "Unix timestamp of the latest successful backup completion.",
            runMetrics.LastSuccessfulBackupFinishedAt?.ToUnixTimeSeconds() ?? 0);
        Gauge(output, "proxyharbor_backup_routing_enabled", "Whether destination-based backup routing is enabled.",
            backupRoutingOptions?.Value.Enabled == true ? 1 : 0);
        Gauge(output, "proxyharbor_backup_latest_routed_run_exists", "Whether a completed routed backup run exists.",
            protectionMetrics.LatestRunExists ? 1 : 0);
        Gauge(output, "proxyharbor_backup_latest_routed_run_assessed", "Whether the latest completed routed backup has a valid fail-closed protection assessment.",
            protectionMetrics.LatestRunAssessed ? 1 : 0);
        Gauge(output, "proxyharbor_backup_latest_routed_run_timestamp_seconds", "Completion time of the latest routed backup; zero when absent.",
            protectionMetrics.LatestRunFinishedAt?.ToUnixTimeSeconds() ?? 0);
        Gauge(output, "proxyharbor_backup_latest_verified_independent_copies", "Independent verified external copies of the latest routed backup; meaningful only when assessed.",
            protectionMetrics.VerifiedIndependentCopies);
        Gauge(output, "proxyharbor_backup_latest_required_copies", "Required independent verified copies in the latest routed backup policy snapshot; meaningful only when assessed.",
            protectionMetrics.RequiredCopies);
        Gauge(output, "proxyharbor_backup_latest_desired_copies", "Desired independent verified copies in the latest routed backup policy snapshot; meaningful only when assessed.",
            protectionMetrics.DesiredCopies);
        Gauge(output, "proxyharbor_backup_latest_required_copy_debt", "Missing required independent verified copies of the latest routed backup; meaningful only when assessed.",
            protectionMetrics.RequiredCopyDebt);
        Gauge(output, "proxyharbor_backup_latest_desired_copy_debt", "Missing desired independent verified copies of the latest routed backup; meaningful only when assessed.",
            protectionMetrics.DesiredCopyDebt);
        Gauge(output, "proxyharbor_backup_copies_unknown", "Backup copies with an unknown write outcome requiring reconciliation.",
            protectionMetrics.UnknownCopies);
        Gauge(output, "proxyharbor_backup_copies_manual_review", "Backup copies requiring manual review.",
            protectionMetrics.ManualReviewCopies);
        Gauge(output, "proxyharbor_backup_delivery_jobs_pending", "Pending backup destination delivery jobs.",
            protectionMetrics.PendingJobs);
        Gauge(output, "proxyharbor_backup_delivery_jobs_reconciling", "Backup delivery jobs reconciling an uncertain outcome.",
            protectionMetrics.ReconcilingJobs);
        Gauge(output, "proxyharbor_backup_oldest_due_pending_job_age_seconds", "Age of the oldest due pending backup delivery job; zero when none.",
            protectionMetrics.OldestDuePendingJobAt is { } oldestDuePendingJobAt
                ? Math.Max(0, (long)(now - oldestDuePendingJobAt).TotalSeconds) : 0);
        Gauge(output, "proxyharbor_backup_last_isolated_restore_pass_timestamp_seconds", "Latest passed isolated restore verification; zero when none.",
            protectionMetrics.LastIsolatedRestorePassedAt?.ToUnixTimeSeconds() ?? 0);
        Gauge(output, "proxyharbor_backup_provider_put_verified_last_1h", "Verified provider PUT outcomes observed during the last hour.",
            protectionMetrics.ProviderPutVerifiedLastHour);
        Gauge(output, "proxyharbor_backup_provider_put_failed_last_1h", "Failed provider PUT outcomes observed during the last hour.",
            protectionMetrics.ProviderPutFailedLastHour);
        Gauge(output, "proxyharbor_backup_provider_verify_matching_last_1h", "Exact matching provider VERIFY outcomes observed during the last hour.",
            protectionMetrics.ProviderVerifyMatchingLastHour);
        Gauge(output, "proxyharbor_backup_provider_verify_missing_last_1h", "Provider VERIFY outcomes reporting a missing object during the last hour.",
            protectionMetrics.ProviderVerifyMissingLastHour);
        Gauge(output, "proxyharbor_backup_provider_verify_mismatching_last_1h", "Provider VERIFY outcomes reporting a mismatching object during the last hour.",
            protectionMetrics.ProviderVerifyMismatchingLastHour);
        Gauge(output, "proxyharbor_backup_provider_verify_inconclusive_last_1h", "Inconclusive, invalid or legacy provider VERIFY outcomes during the last hour.",
            protectionMetrics.ProviderVerifyInconclusiveLastHour);
        var content = output.ToString();
        // Prometheus text exposition требует LF. AppendLine использует CRLF на Windows,
        // поэтому локальный promtool/Prometheus иначе отклоняет TYPE как `counter\r`.
        if (Environment.NewLine.Length != 1)
            content = content.Replace(Environment.NewLine, "\n", StringComparison.Ordinal);
        return Content(content, "text/plain; version=0.0.4; charset=utf-8", Encoding.UTF8);
    }

    private async Task<BackupProtectionOperationalMetrics> ReadBackupProtectionMetricsAsync(
        ProxyHarborDbContext db, OperationalRunMetrics runMetrics, DateTimeOffset now,
        CancellationToken token)
    {
        var copyCounts = await db.BackupCopies.AsNoTracking().GroupBy(_ => 1).Select(group => new
        {
            Unknown = group.Count(copy => copy.State == "unknown"),
            ManualReview = group.Count(copy => copy.State == "manual_review")
        }).SingleOrDefaultAsync(token);
        var jobCounts = await db.BackupDeliveryJobs.AsNoTracking().GroupBy(_ => 1).Select(group => new
        {
            Pending = group.Count(job => job.State == "pending"),
            Reconciling = group.Count(job => job.State == "reconciling")
        }).SingleOrDefaultAsync(token);
        var oldestDuePendingJobAt = await db.BackupDeliveryJobs.AsNoTracking()
            .Where(job => job.State == "pending" && job.NotBefore <= now)
            .OrderBy(job => job.NotBefore)
            .Select(job => (DateTimeOffset?)job.NotBefore)
            .FirstOrDefaultAsync(token);
        var lastIsolatedRestorePassedAt = await db.BackupRestoreVerifications.AsNoTracking()
            .Where(verification => verification.Environment == "isolated" &&
                verification.Result == "passed" && verification.FinishedAt != null)
            .OrderByDescending(verification => verification.FinishedAt)
            .Select(verification => verification.FinishedAt)
            .FirstOrDefaultAsync(token);
        var providerOutcomes = await db.BackupDestinationHealthOutcomes.AsNoTracking()
            .Where(outcome => outcome.ObservedAt >= now.AddHours(-1))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                PutVerified = group.Count(outcome => outcome.Operation == "put" && outcome.Succeeded),
                PutFailed = group.Count(outcome => outcome.Operation == "put" && !outcome.Succeeded),
                VerifyMatching = group.Count(outcome => outcome.Operation == "verify" &&
                    outcome.Succeeded && outcome.ProbeOutcome == "matching"),
                VerifyMissing = group.Count(outcome => outcome.Operation == "verify" &&
                    outcome.ProbeOutcome == "missing"),
                VerifyMismatching = group.Count(outcome => outcome.Operation == "verify" &&
                    outcome.ProbeOutcome == "mismatching"),
                VerifyInconclusive = group.Count(outcome => outcome.Operation == "verify" &&
                    (outcome.ProbeOutcome == null || outcome.ProbeOutcome == "inconclusive" ||
                        outcome.ProbeOutcome == "invalid"))
            }).SingleOrDefaultAsync(token);
        var result = new BackupProtectionOperationalMetrics
        {
            LatestRunExists = runMetrics.LatestRoutedRunId.HasValue,
            LatestRunFinishedAt = runMetrics.LatestRoutedRunFinishedAt,
            UnknownCopies = copyCounts?.Unknown ?? 0,
            ManualReviewCopies = copyCounts?.ManualReview ?? 0,
            PendingJobs = jobCounts?.Pending ?? 0,
            ReconcilingJobs = jobCounts?.Reconciling ?? 0,
            OldestDuePendingJobAt = oldestDuePendingJobAt,
            LastIsolatedRestorePassedAt = lastIsolatedRestorePassedAt,
            ProviderPutVerifiedLastHour = providerOutcomes?.PutVerified ?? 0,
            ProviderPutFailedLastHour = providerOutcomes?.PutFailed ?? 0,
            ProviderVerifyMatchingLastHour = providerOutcomes?.VerifyMatching ?? 0,
            ProviderVerifyMissingLastHour = providerOutcomes?.VerifyMissing ?? 0,
            ProviderVerifyMismatchingLastHour = providerOutcomes?.VerifyMismatching ?? 0,
            ProviderVerifyInconclusiveLastHour = providerOutcomes?.VerifyInconclusive ?? 0
        };
        if (runMetrics.LatestRoutedRunId is not { } runId || backupProtectionEvaluator is null ||
            runMetrics.LatestRoutedRunPoolId is not { } poolId ||
            runMetrics.LatestRoutedRunPolicyVersion is null ||
            runMetrics.LatestRoutedRunRequiredCopies is null ||
            runMetrics.LatestRoutedRunDesiredCopies is null ||
            string.IsNullOrWhiteSpace(runMetrics.LatestRoutedRunContentSha256))
            return result;

        var copies = await db.BackupCopies.AsNoTracking()
            .Where(copy => copy.BackupRunId == runId)
            .Include(copy => copy.BackupDestination)
            .ToListAsync(token);
        var routes = await db.BackupPoolDestinations.AsNoTracking()
            .Where(route => route.BackupPoolId == poolId)
            .ToDictionaryAsync(route => route.BackupDestinationId, token);
        var latestRun = new BackupRun
        {
            Id = runId,
            BackupPoolId = poolId,
            ProtectionPolicyVersion = runMetrics.LatestRoutedRunPolicyVersion,
            RequiredVerifiedCopies = runMetrics.LatestRoutedRunRequiredCopies,
            DesiredVerifiedCopies = runMetrics.LatestRoutedRunDesiredCopies,
            ContentSha256 = runMetrics.LatestRoutedRunContentSha256,
            SizeBytes = runMetrics.LatestRoutedRunSizeBytes,
            Copies = copies
        };
        try
        {
            // Staging affects pending/unavailable only. We export quorum and debt, never an inferred state.
            var assessment = backupProtectionEvaluator.EvaluateLoaded(latestRun, routes, false);
            result.LatestRunAssessed = true;
            result.VerifiedIndependentCopies = assessment.VerifiedIndependentCopies;
            result.RequiredCopies = assessment.RequiredVerifiedCopies;
            result.DesiredCopies = assessment.DesiredVerifiedCopies;
            result.RequiredCopyDebt = assessment.RequiredCopyDebt;
            result.DesiredCopyDebt = assessment.DesiredCopyDebt;
        }
        catch (BackupDestinationRouteException) { }
        catch (ArgumentException) { }
        return result;
    }

    private sealed class BackupProtectionOperationalMetrics
    {
        public bool LatestRunExists { get; init; }
        public bool LatestRunAssessed { get; set; }
        public DateTimeOffset? LatestRunFinishedAt { get; init; }
        public int VerifiedIndependentCopies { get; set; }
        public int RequiredCopies { get; set; }
        public int DesiredCopies { get; set; }
        public int RequiredCopyDebt { get; set; }
        public int DesiredCopyDebt { get; set; }
        public int UnknownCopies { get; init; }
        public int ManualReviewCopies { get; init; }
        public int PendingJobs { get; init; }
        public int ReconcilingJobs { get; init; }
        public DateTimeOffset? OldestDuePendingJobAt { get; init; }
        public DateTimeOffset? LastIsolatedRestorePassedAt { get; init; }
        public int ProviderPutVerifiedLastHour { get; init; }
        public int ProviderPutFailedLastHour { get; init; }
        public int ProviderVerifyMatchingLastHour { get; init; }
        public int ProviderVerifyMissingLastHour { get; init; }
        public int ProviderVerifyMismatchingLastHour { get; init; }
        public int ProviderVerifyInconclusiveLastHour { get; init; }
    }

    /// <summary>
    /// Читает active/latest/successful состояния collection и backup run'ов одним PostgreSQL command.
    /// Каждый LATERAL lookup использует существующие status/finished indexes; единый statement устраняет
    /// пять сетевых round-trip и не смешивает состояния между последовательными запросами.
    /// </summary>
    private static async Task<OperationalRunMetrics> ReadRunMetricsAsync(
        ProxyHarborDbContext db,
        CancellationToken token)
    {
        if (db.Database.IsRelational())
        {
            return await db.Database.SqlQuery<OperationalRunMetrics>($"""
                SELECT
                    collection_active."Value" AS "ActiveCollectionRuns",
                    collection_finished."Status" AS "LastCollectionStatus",
                    COALESCE(collection_finished."CandidatesFound", 0)::int AS "LastCollectionCandidates",
                    COALESCE(collection_finished."SourcesSkipped", 0)::int AS "LastCollectionSourcesSkipped",
                    COALESCE(collection_finished."SourcesTruncated", 0)::int AS "LastCollectionSourcesTruncated",
                    COALESCE(collection_finished."CandidateLimitReached", FALSE) AS "LastCollectionCandidateLimitReached",
                    collection_finished."StartedAt" AS "LastCollectionStartedAt",
                    collection_finished."FinishedAt" AS "LastCollectionFinishedAt",
                    collection_success."FinishedAt" AS "LastSuccessfulCollectionAt",
                    backup_active."Value" AS "ActiveBackupRuns",
                    backup_finished."Status" AS "LastBackupStatus",
                    COALESCE(backup_success."TelegramConfigured", FALSE) AS "LastSuccessfulBackupTelegramConfigured",
                    COALESCE(backup_success."SentToTelegram", FALSE) AS "LastSuccessfulBackupSentToTelegram",
                    COALESCE(backup_success."SizeBytes", 0)::bigint AS "LastSuccessfulBackupSizeBytes",
                    backup_success."FinishedAt" AS "LastSuccessfulBackupFinishedAt",
                    backup_routed."Id" AS "LatestRoutedRunId",
                    backup_routed."BackupPoolId" AS "LatestRoutedRunPoolId",
                    backup_routed."ProtectionPolicyVersion" AS "LatestRoutedRunPolicyVersion",
                    backup_routed."RequiredVerifiedCopies" AS "LatestRoutedRunRequiredCopies",
                    backup_routed."DesiredVerifiedCopies" AS "LatestRoutedRunDesiredCopies",
                    backup_routed."ContentSha256" AS "LatestRoutedRunContentSha256",
                    COALESCE(backup_routed."SizeBytes", 0)::bigint AS "LatestRoutedRunSizeBytes",
                    backup_routed."FinishedAt" AS "LatestRoutedRunFinishedAt"
                FROM
                    (SELECT COUNT(*)::int AS "Value"
                     FROM "Runs"
                     WHERE "Status" = {"running"} AND "FinishedAt" IS NULL) AS collection_active
                CROSS JOIN
                    (SELECT COUNT(*)::int AS "Value"
                     FROM "BackupRuns"
                     WHERE "Status" = {"running"} AND "FinishedAt" IS NULL) AS backup_active
                LEFT JOIN LATERAL
                    (SELECT "Status", "CandidatesFound", "SourcesSkipped", "SourcesTruncated",
                            "CandidateLimitReached", "StartedAt", "FinishedAt"
                     FROM "Runs"
                     WHERE "FinishedAt" IS NOT NULL
                     ORDER BY "FinishedAt" DESC, "Id" DESC
                     LIMIT 1) AS collection_finished ON TRUE
                LEFT JOIN LATERAL
                    (SELECT "FinishedAt"
                     FROM "Runs"
                     WHERE "Status" = {"completed"} AND "FinishedAt" IS NOT NULL
                     ORDER BY "FinishedAt" DESC, "Id" DESC
                     LIMIT 1) AS collection_success ON TRUE
                LEFT JOIN LATERAL
                    (SELECT "Status"
                     FROM "BackupRuns"
                     WHERE "FinishedAt" IS NOT NULL
                     ORDER BY "FinishedAt" DESC, "Id" DESC
                     LIMIT 1) AS backup_finished ON TRUE
                LEFT JOIN LATERAL
                    (SELECT "TelegramConfigured", "SentToTelegram", "SizeBytes", "FinishedAt"
                     FROM "BackupRuns"
                     WHERE "Status" = {"completed"} AND "FinishedAt" IS NOT NULL
                     ORDER BY "FinishedAt" DESC, "Id" DESC
                     LIMIT 1) AS backup_success ON TRUE
                LEFT JOIN LATERAL
                    (SELECT "Id", "BackupPoolId", "ProtectionPolicyVersion", "RequiredVerifiedCopies",
                            "DesiredVerifiedCopies", "ContentSha256", "SizeBytes", "FinishedAt"
                     FROM "BackupRuns"
                     WHERE "Status" = {"completed"} AND "FinishedAt" IS NOT NULL
                           AND "BackupPoolId" IS NOT NULL
                     ORDER BY "FinishedAt" DESC, "Id" DESC
                     LIMIT 1) AS backup_routed ON TRUE
                """).SingleAsync(token);
        }

        // InMemory provider не исполняет PostgreSQL LATERAL. Этот bounded fallback сохраняет
        // provider-independent unit tests; production всегда использует путь выше.
        var lastFinishedRun = await db.Runs.AsNoTracking().Where(x => x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
        var lastSuccessfulRun = await db.Runs.AsNoTracking()
            .Where(x => x.Status == "completed" && x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
        var activeRuns = await db.Runs.AsNoTracking()
            .CountAsync(x => x.Status == "running" && x.FinishedAt == null, token);
        var lastFinishedBackup = await db.BackupRuns.AsNoTracking().Where(x => x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
        var lastSuccessfulBackup = await db.BackupRuns.AsNoTracking()
            .Where(x => x.Status == "completed" && x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
        var lastRoutedBackup = await db.BackupRuns.AsNoTracking()
            .Where(x => x.Status == "completed" && x.FinishedAt != null && x.BackupPoolId != null)
            .OrderByDescending(x => x.FinishedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
        var activeBackups = await db.BackupRuns.AsNoTracking()
            .CountAsync(x => x.Status == "running" && x.FinishedAt == null, token);

        return new OperationalRunMetrics
        {
            ActiveCollectionRuns = activeRuns,
            LastCollectionStatus = lastFinishedRun?.Status,
            LastCollectionCandidates = lastFinishedRun?.CandidatesFound ?? 0,
            LastCollectionSourcesSkipped = lastFinishedRun?.SourcesSkipped ?? 0,
            LastCollectionSourcesTruncated = lastFinishedRun?.SourcesTruncated ?? 0,
            LastCollectionCandidateLimitReached = lastFinishedRun?.CandidateLimitReached == true,
            LastCollectionStartedAt = lastFinishedRun?.StartedAt,
            LastCollectionFinishedAt = lastFinishedRun?.FinishedAt,
            LastSuccessfulCollectionAt = lastSuccessfulRun?.FinishedAt,
            ActiveBackupRuns = activeBackups,
            LastBackupStatus = lastFinishedBackup?.Status,
            LastSuccessfulBackupTelegramConfigured = lastSuccessfulBackup?.TelegramConfigured == true,
            LastSuccessfulBackupSentToTelegram = lastSuccessfulBackup?.SentToTelegram == true,
            LastSuccessfulBackupSizeBytes = lastSuccessfulBackup?.SizeBytes ?? 0,
            LastSuccessfulBackupFinishedAt = lastSuccessfulBackup?.FinishedAt,
            LatestRoutedRunId = lastRoutedBackup?.Id,
            LatestRoutedRunPoolId = lastRoutedBackup?.BackupPoolId,
            LatestRoutedRunPolicyVersion = lastRoutedBackup?.ProtectionPolicyVersion,
            LatestRoutedRunRequiredCopies = lastRoutedBackup?.RequiredVerifiedCopies,
            LatestRoutedRunDesiredCopies = lastRoutedBackup?.DesiredVerifiedCopies,
            LatestRoutedRunContentSha256 = lastRoutedBackup?.ContentSha256,
            LatestRoutedRunSizeBytes = lastRoutedBackup?.SizeBytes ?? 0,
            LatestRoutedRunFinishedAt = lastRoutedBackup?.FinishedAt
        };
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

    private static void Counter(StringBuilder output, string name, string help, long value)
    {
        output.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        output.Append("# TYPE ").Append(name).AppendLine(" counter");
        output.Append(name).Append(' ').AppendLine(value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>Компактный операционный snapshot collection и backup циклов для Prometheus.</summary>
internal sealed class OperationalRunMetrics
{
    public int ActiveCollectionRuns { get; set; }
    public string? LastCollectionStatus { get; set; }
    public int LastCollectionCandidates { get; set; }
    public int LastCollectionSourcesSkipped { get; set; }
    public int LastCollectionSourcesTruncated { get; set; }
    public bool LastCollectionCandidateLimitReached { get; set; }
    public DateTimeOffset? LastCollectionStartedAt { get; set; }
    public DateTimeOffset? LastCollectionFinishedAt { get; set; }
    public DateTimeOffset? LastSuccessfulCollectionAt { get; set; }
    public int ActiveBackupRuns { get; set; }
    public string? LastBackupStatus { get; set; }
    public bool LastSuccessfulBackupTelegramConfigured { get; set; }
    public bool LastSuccessfulBackupSentToTelegram { get; set; }
    public long LastSuccessfulBackupSizeBytes { get; set; }
    public DateTimeOffset? LastSuccessfulBackupFinishedAt { get; set; }
    public Guid? LatestRoutedRunId { get; set; }
    public Guid? LatestRoutedRunPoolId { get; set; }
    public int? LatestRoutedRunPolicyVersion { get; set; }
    public int? LatestRoutedRunRequiredCopies { get; set; }
    public int? LatestRoutedRunDesiredCopies { get; set; }
    public string? LatestRoutedRunContentSha256 { get; set; }
    public long LatestRoutedRunSizeBytes { get; set; }
    public DateTimeOffset? LatestRoutedRunFinishedAt { get; set; }
}
