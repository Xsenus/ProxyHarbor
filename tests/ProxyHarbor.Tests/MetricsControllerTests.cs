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
    public async Task MetricsIgnoreNewerRunningCycleForLastCompletionValues()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-{Guid.NewGuid():N}").Options;
        var finishedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_100);
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Sources.AddRange(
                new ProxySource { Name = "healthy", Url = "https://example.com/a", LastItemCount = 10 },
                new ProxySource { Name = "failed", Url = "https://example.com/b", ConsecutiveFailures = 1, LastError = "timeout" });
            seed.Runs.AddRange(
                new CollectionRun
                {
                    StartedAt = finishedAt.AddSeconds(-12.5),
                    FinishedAt = finishedAt,
                    Status = "completed",
                    CandidatesFound = 42
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
        Assert.Contains("proxyharbor_sources_healthy 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_collection_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_probe_control_available 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_candidates 42", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_sources_skipped 0", metrics, StringComparison.Ordinal);
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
