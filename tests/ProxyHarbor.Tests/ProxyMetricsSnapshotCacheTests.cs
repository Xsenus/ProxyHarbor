using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Защищает coalescing и stale-on-error общего дорогого aggregate.</summary>
public sealed class ProxyMetricsSnapshotCacheTests
{
    [Fact]
    public async Task ConcurrentColdRequestsShareOneDatabaseSnapshot()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"proxy-metrics-cache-{Guid.NewGuid():N}").Options;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(new ProxyEndpoint
            {
                Host = "203.0.113.10",
                Port = 8080,
                Status = ProxyStatus.Alive,
                Protocol = ProxyProtocol.Https,
                LastCheckedAt = DateTimeOffset.UtcNow,
                LatencyMs = 42
            });
            await seed.SaveChangesAsync();
        }

        var factory = new CountingFactory(options);
        using var cache = CreateCache(factory);

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => cache.GetAsync(CancellationToken.None)));

        Assert.Equal(1, factory.Created);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        Assert.Equal(1, snapshots[0].Published);
        Assert.Equal(42, Assert.Single(snapshots[0].Groups).FreshLatencyTotal);
    }

    [Fact]
    public async Task RefreshFailureReturnsLastSuccessfulSnapshot()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"proxy-metrics-stale-{Guid.NewGuid():N}").Options;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(new ProxyEndpoint { Host = "198.51.100.20", Port = 3128 });
            await seed.SaveChangesAsync();
        }

        var factory = new FailAfterFirstFactory(options);
        using var cache = CreateCache(factory);
        var first = await cache.GetAsync(CancellationToken.None);

        var afterFailure = await cache.RefreshAsync(CancellationToken.None);

        Assert.Same(first, afterFailure);
        Assert.Equal(2, factory.Attempts);
    }

    private static ProxyMetricsSnapshotCache CreateCache(IDbContextFactory<ProxyHarborDbContext> factory) =>
        new(factory,
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15, DeadRetentionDays = 7 }),
            NullLogger<ProxyMetricsSnapshotCache>.Instance);

    private sealed class CountingFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public int Created { get; private set; }
        public ProxyHarborDbContext CreateDbContext()
        {
            Created++;
            return new ProxyHarborDbContext(options);
        }

        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FailAfterFirstFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public int Attempts { get; private set; }
        public ProxyHarborDbContext CreateDbContext() =>
            throw new NotSupportedException("Use the async factory path.");

        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Attempts == 1
                ? Task.FromResult(new ProxyHarborDbContext(options))
                : Task.FromException<ProxyHarborDbContext>(new InvalidOperationException("transient database failure"));
        }
    }
}
