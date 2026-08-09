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
            await seed.SaveChangesAsync();
        }

        var controller = new MetricsController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_sources_healthy 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_collection_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_candidates 42", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_timestamp_seconds 1700000100", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_duration_seconds 12.5", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyharbor_last_collection_candidates 999", metrics, StringComparison.Ordinal);
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
