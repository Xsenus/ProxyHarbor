using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует Prometheus-контракт и выбор последнего завершённого цикла.</summary>
public sealed class MetricsControllerTests
{
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

        var controller = new MetricsController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { CollectionIntervalMinutes = 15 }),
            new ProbeControlHealth());

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_sources_stale 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_stale 1", metrics, StringComparison.Ordinal);
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
                    LastValidationAttemptAt = sourceAuditedAt.AddMinutes(-2),
                    LastValidationDeferred = true
                },
                new ProxyEndpoint { Host = "9.9.9.9", Port = 8082 });
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
            new TestDbFactory(options), Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }), controlHealth);

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_sources_truncated 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_source_catalog_complete 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_source_catalog_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_expected 81", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_truncated 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_providers_expected 50", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_providers_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_collection_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_never_attempted 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_attempts_last_5m 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_checked_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_alive_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_deferred_last_5m 1", metrics, StringComparison.Ordinal);
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
        Assert.Contains("proxyharbor_backup_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_telegram_configured 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_sent_to_telegram 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_size_bytes 12345", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_timestamp_seconds 1700000040", metrics, StringComparison.Ordinal);
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
