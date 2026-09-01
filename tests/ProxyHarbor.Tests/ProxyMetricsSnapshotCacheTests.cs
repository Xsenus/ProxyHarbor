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
                LastCheckedAt = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero),
                LatencyMs = 42
            });
            await seed.SaveChangesAsync();
        }

        var factory = new CountingFactory(options);
        using var cache = CreateCache(factory, new ManualTimeProvider());

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
        using var cache = CreateCache(factory, new ManualTimeProvider());
        var first = await cache.GetAsync(CancellationToken.None);

        var afterFailure = await cache.RefreshAsync(CancellationToken.None);

        Assert.Same(first, afterFailure);
        Assert.Equal(2, factory.Attempts);
    }

    [Fact]
    public async Task StaleDemandReturnsImmediatelyAndWorkerCoalescesRefresh()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"proxy-metrics-demand-{Guid.NewGuid():N}").Options;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(new ProxyEndpoint { Host = "192.0.2.30", Port = 8080 });
            await seed.SaveChangesAsync();
        }

        var factory = new CountingFactory(options);
        var clock = new ManualTimeProvider();
        using var cache = CreateCache(factory, clock);
        var initial = await cache.GetAsync(CancellationToken.None);
        Assert.Equal(1, factory.Created);

        clock.Advance(ProxyMetricsSnapshotCache.MaximumAge + TimeSpan.FromSeconds(1));
        var stale = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => cache.GetAsync(CancellationToken.None)));

        Assert.All(stale, snapshot => Assert.Same(initial, snapshot));
        Assert.Equal(1, cache.RefreshRequestsQueued);
        Assert.Equal(19, cache.RefreshRequestsCoalesced);

        var worker = new ProxyMetricsSnapshotRefreshWorker(
            cache, NullLogger<ProxyMetricsSnapshotRefreshWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => factory.Created == 2);
            ProxyMetricsSnapshot? refreshed = null;
            await WaitUntilAsync(async () =>
            {
                refreshed = await cache.GetAsync(CancellationToken.None);
                return !ReferenceEquals(initial, refreshed);
            });
            Assert.NotSame(initial, refreshed);
            Assert.Equal(2, cache.DatabaseReads);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task ExcessivelyStaleSnapshotRefreshesSynchronously()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"proxy-metrics-hard-expiry-{Guid.NewGuid():N}").Options;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(new ProxyEndpoint { Host = "192.0.2.31", Port = 3128 });
            await seed.SaveChangesAsync();
        }

        var factory = new CountingFactory(options);
        var clock = new ManualTimeProvider();
        using var cache = CreateCache(factory, clock);
        var initial = await cache.GetAsync(CancellationToken.None);

        clock.Advance(ProxyMetricsSnapshotCache.MaximumStaleAge + TimeSpan.FromSeconds(1));
        var refreshed = await cache.GetAsync(CancellationToken.None);

        Assert.NotSame(initial, refreshed);
        Assert.Equal(2, factory.Created);
        Assert.Equal(0, cache.RefreshRequestsQueued);
    }

    [Fact]
    public async Task WorkerKeepsConsumingDemandAfterStartupWarmupFailure()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"proxy-metrics-worker-recovery-{Guid.NewGuid():N}").Options;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(new ProxyEndpoint { Host = "192.0.2.32", Port = 1080 });
            await seed.SaveChangesAsync();
        }

        var factory = new FailOnceThenCountingFactory(options);
        var clock = new ManualTimeProvider();
        using var cache = CreateCache(factory, clock);
        var worker = new ProxyMetricsSnapshotRefreshWorker(
            cache, NullLogger<ProxyMetricsSnapshotRefreshWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => factory.Attempts == 1);
            var initial = await cache.GetAsync(CancellationToken.None);
            Assert.Equal(2, factory.Attempts);

            clock.Advance(ProxyMetricsSnapshotCache.MaximumAge + TimeSpan.FromSeconds(1));
            Assert.Same(initial, await cache.GetAsync(CancellationToken.None));
            await WaitUntilAsync(() => factory.Attempts == 3);

            ProxyMetricsSnapshot? refreshed = null;
            await WaitUntilAsync(async () =>
            {
                refreshed = await cache.GetAsync(CancellationToken.None);
                return !ReferenceEquals(initial, refreshed);
            });
            Assert.NotSame(initial, refreshed);
            Assert.Equal(2, cache.DatabaseReads);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private static ProxyMetricsSnapshotCache CreateCache(
        IDbContextFactory<ProxyHarborDbContext> factory,
        TimeProvider timeProvider) =>
        new(factory,
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15, DeadRetentionDays = 7 }),
            NullLogger<ProxyMetricsSnapshotCache>.Instance,
            timeProvider);

    private sealed class CountingFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        private int _created;
        public int Created => Volatile.Read(ref _created);
        public ProxyHarborDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _created);
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

    private sealed class FailOnceThenCountingFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public ProxyHarborDbContext CreateDbContext() =>
            throw new NotSupportedException("Use the async factory path.");

        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            return attempt == 1
                ? Task.FromException<ProxyHarborDbContext>(new InvalidOperationException("startup failure"))
                : Task.FromResult(new ProxyHarborDbContext(options));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= timeoutAt)
                throw new TimeoutException("Background proxy metrics refresh did not complete.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= timeoutAt)
                throw new TimeoutException("Background proxy metrics refresh did not publish its snapshot.");
            await Task.Delay(10);
        }
    }
}
