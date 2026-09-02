using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует Prometheus-контракт и выбор последнего завершённого цикла.</summary>
public sealed class MetricsControllerTests
{
    [Fact]
    public async Task MetricsExposeVpnSourceAndBuiltInCatalogHealth()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-vpn-sources-{Guid.NewGuid():N}").Options;
        var auditedAt = DateTimeOffset.UtcNow;
        var builtIn = BuiltInVpnSourceCatalog.Sources[0];
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.VpnSources.AddRange(
                new VpnSource
                {
                    Name = builtIn.Name,
                    Provider = builtIn.Provider,
                    Url = builtIn.Url,
                    DefaultProtocol = builtIn.Protocol,
                    License = builtIn.License,
                    LastFetchedAt = auditedAt,
                    LastSucceededAt = auditedAt,
                    LastItemCount = 10
                },
                new VpnSource
                {
                    Name = "custom failure",
                    Provider = "Custom",
                    Url = "https://example.com/custom-vpn.txt",
                    DefaultProtocol = VpnProtocol.Vless,
                    License = "custom",
                    LastFetchedAt = auditedAt,
                    LastError = "timeout",
                    ConsecutiveFailures = 1
                });
            await seed.SaveChangesAsync();
        }

        var controller = new MetricsController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { CollectionIntervalMinutes = 15 }),
            Options.Create(new BackupOptions()),
            new ProbeControlHealth());

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_vpn_sources_enabled 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_sources_failing 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_sources_healthy 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_sources_never_audited 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_source_catalog_complete 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_source_catalog_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_catalog_audit_timestamp_seconds 1788307200", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_expected 174", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_healthy 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_failing 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_never_audited 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_providers_expected 32", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_providers_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_providers_enabled 1", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsDoNotReportHistoricallySuccessfulStaleSourceAsHealthy()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-stale-{Guid.NewGuid():N}").Options;
        var builtIn = BuiltInSourceCatalog.Sources[0];
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Sources.Add(new ProxySource
            {
                Name = builtIn.Name,
                Url = builtIn.Url,
                DefaultProtocol = builtIn.Protocol,
                LastFetchedAt = DateTimeOffset.UtcNow.AddHours(-1),
                LastSucceededAt = DateTimeOffset.UtcNow.AddHours(-1),
                LastItemCount = 10
            });
            await seed.SaveChangesAsync();
        }

        var httpTelemetry = new HttpRequestTelemetry();
        httpTelemetry.Record(HttpRouteGroup.Proxies, 503, TimeSpan.FromMilliseconds(250));
        long idleTimestamp = 1_000;
        var idleGate = new ValidationClaimIdleGate(
            () => idleTimestamp, 1_000, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        idleGate.MarkEmpty();
        Assert.True(idleGate.TryCoalesce(Guid.NewGuid()).Coalesced);
        using var checkerCredentials = new CheckerNodeCredentialCache(
            new TestDbFactory(options), TimeProvider.System);
        checkerCredentials.Invalidate();
        var invalidCheckerToken = new string('x', 48);
        Assert.False(await checkerCredentials.AuthenticateAsync(Guid.NewGuid(), invalidCheckerToken, default));
        Assert.False(await checkerCredentials.AuthenticateAsync(Guid.NewGuid(), invalidCheckerToken, default));
        var controller = new MetricsController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { CollectionIntervalMinutes = 5 }),
            Options.Create(new BackupOptions()),
            new ProbeControlHealth(),
            httpTelemetry: httpTelemetry,
            validationIdleGate: idleGate,
            checkerCredentialCache: checkerCredentials);

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.DoesNotContain('\r', metrics);
        Assert.Contains("proxyharbor_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_sources_stale 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_stale 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_background_workers_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("# TYPE proxyharbor_validation_empty_claims_coalesced_total counter", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_empty_claims_coalesced_total 1", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_empty_claim_cooldown_active 1", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_attempts_total 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_failures_total 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_snapshot_hits_total 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_database_reads_total 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_invalidations_total 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_last_success_timestamp_seconds 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_last_failure_timestamp_seconds 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_last_deleted_rows 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_last_recovered_rows 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_healthy -1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_collection_interval_seconds 300", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_public_freshness_seconds 900", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_validation_concurrency_limit 800", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_validation_batch_size 1600", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_reachable_validation_interval_seconds 600", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_unreachable_retry_seconds 1800", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_unsupported_retry_seconds 21600", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_public_freshness_seconds 900", metrics, StringComparison.Ordinal);
        Assert.Contains("# TYPE proxyharbor_vpn_endpoints gauge", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_endpoints 0", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyharbor_vpn_endpoints_total", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_validation_due 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_published 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_configuration_read_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_enabled 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_interval_seconds 86400", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_telegram_configured 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_object_storage_configured 0", metrics, StringComparison.Ordinal);
        Assert.Contains("# TYPE proxyharbor_advisory_lock_cleanup_failures_total counter", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_http_requests_total{route=\"proxies\",status=\"5xx\"} 1", metrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsIgnoreNewerRunningCycleForLastCompletionValues()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-{Guid.NewGuid():N}").Options;
        var finishedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_100);
        var sourceAuditedAt = DateTimeOffset.UtcNow;
        var latestValidationAttempt = sourceAuditedAt.AddMinutes(-1);
        var builtIn = BuiltInSourceCatalog.Sources[0];
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Sources.AddRange(
                new ProxySource
                {
                    Name = "healthy",
                    Url = builtIn.Url,
                    DefaultProtocol = builtIn.Protocol,
                    LastFetchedAt = sourceAuditedAt,
                    LastSucceededAt = sourceAuditedAt,
                    LastItemCount = 10,
                    LastResultTruncated = true
                },
                new ProxySource { Name = "failed", Url = "https://example.com/b", ConsecutiveFailures = 1, LastError = "timeout" });
            seed.Proxies.AddRange(
                new ProxyEndpoint
                {
                    Host = "8.8.8.8",
                    Port = 8080,
                    Status = ProxyStatus.Alive,
                    LastCheckedAt = latestValidationAttempt,
                    LastValidationAttemptAt = latestValidationAttempt
                },
                new ProxyEndpoint
                {
                    Host = "1.1.1.1",
                    Port = 8081,
                    FirstSeenAt = sourceAuditedAt.AddDays(-5),
                    LastSeenAt = sourceAuditedAt.AddDays(-4),
                    LastValidationAttemptAt = sourceAuditedAt.AddMinutes(-2),
                    LastValidationDeferred = true
                },
                new ProxyEndpoint { Host = "9.9.9.9", Port = 8082 });
            var leasedProxy = new ProxyEndpoint
            {
                Host = "4.4.4.4",
                Port = 8083,
                LastValidationAttemptAt = sourceAuditedAt.AddMinutes(-3)
            };
            seed.Proxies.Add(leasedProxy);
            seed.ProxyValidationLeases.Add(new ProxyValidationLease
            {
                ProxyId = leasedProxy.Id,
                LeaseId = Guid.NewGuid(),
                LeaseUntil = sourceAuditedAt.AddMinutes(1)
            });
            seed.ValidationRuns.AddRange(
                new ValidationRun
                {
                    LeaseId = Guid.NewGuid(),
                    StartedAt = sourceAuditedAt.AddMinutes(-1).AddSeconds(-1),
                    FinishedAt = sourceAuditedAt.AddMinutes(-1),
                    Claimed = 2,
                    Checked = 1,
                    Alive = 1,
                    Deferred = 1,
                    Status = "completed"
                },
                new ValidationRun
                {
                    LeaseId = Guid.NewGuid(),
                    StartedAt = sourceAuditedAt.AddSeconds(-31),
                    FinishedAt = sourceAuditedAt.AddSeconds(-30),
                    Status = "failed",
                    Error = "probe pipeline failed"
                },
                new ValidationRun
                {
                    LeaseId = Guid.NewGuid(),
                    StartedAt = sourceAuditedAt,
                    Claimed = 3,
                    Status = "running"
                });
            seed.Runs.AddRange(
                new CollectionRun
                {
                    StartedAt = finishedAt.AddSeconds(-12.5),
                    FinishedAt = finishedAt,
                    Status = "completed",
                    CandidatesFound = 42,
                    SourcesTruncated = 1,
                    CandidateLimitReached = true
                },
                new CollectionRun { StartedAt = finishedAt.AddMinutes(1), Status = "running", CandidatesFound = 999 });
            seed.BackupRuns.AddRange(
                new BackupRun
                {
                    StartedAt = finishedAt.AddMinutes(-2),
                    FinishedAt = finishedAt.AddMinutes(-1),
                    Status = "completed",
                    TelegramConfigured = true,
                    SentToTelegram = true,
                    SizeBytes = 12_345
                },
                new BackupRun { StartedAt = finishedAt.AddMinutes(2), Status = "running" });
            await seed.SaveChangesAsync();
        }

        var controlHealth = new ProbeControlHealth();
        controlHealth.Record(available: true);
        var controller = new MetricsController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15, CollectionIntervalMinutes = 20 }),
            Options.Create(new BackupOptions
            {
                Enabled = true,
                IntervalHours = 12,
                TelegramBotToken = "test-token",
                TelegramChatId = "123"
            }),
            controlHealth);

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_sources_truncated 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_source_catalog_complete 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_source_catalog_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_catalog_audit_timestamp_seconds 1788307200", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_expected 255", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_truncated 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_providers_expected 85", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_providers_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_collection_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_never_attempted 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_proxies_stale_unseen 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_proxies_published 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_due 3", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_leased 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_attempts_last_5m 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_checked_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_alive_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_deferred_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_runs_failed_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_background_workers_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_collection_interval_seconds 1200", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_public_freshness_seconds 900", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_concurrency_limit 800", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_batch_size 1600", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_checks_per_second 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_estimated_drain_seconds 2", metrics, StringComparison.Ordinal);
        Assert.Contains($"proxyharbor_validation_last_attempt_timestamp_seconds {latestValidationAttempt.ToUnixTimeSeconds()}",
            metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_probe_control_available 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_candidates 42", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_sources_skipped 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_sources_truncated 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_candidate_limit_reached 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_timestamp_seconds 1700000100", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_duration_seconds 12.5", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyharbor_last_collection_candidates 999", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_interval_seconds 43200", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_telegram_configured 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_telegram_configured 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_sent_to_telegram 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_size_bytes 12345", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_timestamp_seconds 1700000040", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsUseEffectiveRuntimeBackupConfiguration()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-runtime-backup-{Guid.NewGuid():N}").Options;
        var runtimeOptions = new BackupOptions
        {
            Enabled = true,
            IntervalHours = 6,
            TelegramRecipientId = Guid.NewGuid()
        };
        var controller = new MetricsController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions()),
            Options.Create(new BackupOptions { Enabled = false, IntervalHours = 24 }),
            new ProbeControlHealth(),
            backupConfigurationStore: new TestBackupConfigurationStore(runtimeOptions));

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_backup_configuration_read_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_interval_seconds 21600", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_telegram_configured 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_object_storage_configured 0", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsExposeRuntimeBackupConfigurationReadFailureAndUseSafeFallback()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-runtime-backup-failure-{Guid.NewGuid():N}").Options;
        var controller = new MetricsController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions()),
            Options.Create(new BackupOptions { Enabled = false, IntervalHours = 12 }),
            new ProbeControlHealth(),
            backupConfigurationStore: new FailingBackupConfigurationStore());

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_backup_configuration_read_success 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_enabled 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_interval_seconds 43200", metrics, StringComparison.Ordinal);
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestBackupConfigurationStore(BackupOptions options) : IBackupConfigurationStore
    {
        public Task<BackupOptions> GetAsync(CancellationToken token = default) => Task.FromResult(options);

        public Task SaveAsync(BackupOptions optionsToSave, CancellationToken token = default) =>
            Task.CompletedTask;
    }

    private sealed class FailingBackupConfigurationStore : IBackupConfigurationStore
    {
        public Task<BackupOptions> GetAsync(CancellationToken token = default) =>
            Task.FromException<BackupOptions>(new InvalidOperationException("Invalid runtime backup configuration."));

        public Task SaveAsync(BackupOptions options, CancellationToken token = default) =>
            Task.CompletedTask;
    }
}
