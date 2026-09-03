using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Защищает coalescing и demand-refresh общего VPN aggregate.</summary>
public sealed class VpnMetricsSnapshotCacheTests
{
    [Fact]
    public async Task ConcurrentColdRequestsShareOneDatabaseSnapshot()
    {
        var options = Options();
        await SeedAsync(options);
        var factory = new CountingFactory(options);
        using var cache = CreateCache(factory, new ManualTimeProvider());

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => cache.GetAsync(CancellationToken.None)));

        Assert.Equal(1, factory.Created);
        Assert.Equal(1, cache.DatabaseReads);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        Assert.Equal(1, snapshots[0].Reachable);
        Assert.Equal(25, snapshots[0].ReachableLatencyTotal);
        Assert.Equal(1, snapshots[0].NeverChecked);
        Assert.Equal(1, snapshots[0].Due);
        Assert.Equal(0, snapshots[0].FreshReachable);
        Assert.Equal(1, snapshots[0].StaleReachable);
    }

    [Fact]
    public async Task StaleDemandReturnsImmediatelyAndWorkerCoalescesRefresh()
    {
        var options = Options();
        await SeedAsync(options);
        var factory = new CountingFactory(options);
        var clock = new ManualTimeProvider();
        using var cache = CreateCache(factory, clock);
        var initial = await cache.GetAsync(CancellationToken.None);

        clock.Advance(VpnMetricsSnapshotCache.MaximumAge + TimeSpan.FromSeconds(1));
        var stale = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => cache.GetAsync(CancellationToken.None)));

        Assert.All(stale, snapshot => Assert.Same(initial, snapshot));
        Assert.Equal(1, cache.RefreshRequestsQueued);
        Assert.Equal(19, cache.RefreshRequestsCoalesced);

        var worker = new VpnMetricsSnapshotRefreshWorker(
            cache, NullLogger<VpnMetricsSnapshotRefreshWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => factory.Created == 2);
            VpnMetricsSnapshot? refreshed = null;
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
    public async Task PassiveDemandRefreshesAtFiveMinutesInsteadOfOne()
    {
        var options = Options();
        await SeedAsync(options);
        var factory = new CountingFactory(options);
        var clock = new ManualTimeProvider();
        using var cache = CreateCache(factory, clock);
        var initial = await cache.GetPassiveAsync(CancellationToken.None);

        clock.Advance(VpnMetricsSnapshotCache.MaximumAge + TimeSpan.FromSeconds(1));
        Assert.Same(initial, await cache.GetPassiveAsync(CancellationToken.None));
        Assert.Equal(0, cache.RefreshRequestsQueued);
        Assert.Equal(1, cache.DatabaseReads);

        clock.Advance(VpnMetricsSnapshotCache.PassiveMaximumAge -
            VpnMetricsSnapshotCache.MaximumAge);
        Assert.Same(initial, await cache.GetPassiveAsync(CancellationToken.None));
        Assert.Equal(1, cache.RefreshRequestsQueued);
        Assert.Equal(1, cache.DatabaseReads);
    }

    private static DbContextOptions<ProxyHarborDbContext> Options() =>
        new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"vpn-metrics-cache-{Guid.NewGuid():N}").Options;

    private static async Task SeedAsync(DbContextOptions<ProxyHarborDbContext> options)
    {
        await using var db = new ProxyHarborDbContext(options);
        var source = new VpnSource
        {
            Name = "Metrics",
            Provider = "Tests",
            Url = "https://example.test/vpn.txt",
            DefaultProtocol = VpnProtocol.Vless,
            License = "MIT"
        };
        db.VpnSources.Add(source);
        db.VpnEndpoints.Add(new VpnEndpoint
        {
            Host = "203.0.113.10",
            Port = 443,
            Protocol = VpnProtocol.Vless,
            Transport = "tcp",
            Status = VpnEndpointStatus.Reachable,
            LatencyMs = 25,
            SuccessfulChecks = 1,
            FirstSource = source,
            FirstSourceId = source.Id
        });
        await db.SaveChangesAsync();
    }

    private static VpnMetricsSnapshotCache CreateCache(
        IDbContextFactory<ProxyHarborDbContext> factory,
        TimeProvider timeProvider) => new(
            factory,
            NullLogger<VpnMetricsSnapshotCache>.Instance,
            timeProvider,
            Microsoft.Extensions.Options.Options.Create(new CollectorOptions()));

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
                throw new TimeoutException("Background VPN metrics refresh did not complete.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= timeoutAt)
                throw new TimeoutException("Background VPN metrics refresh did not publish its snapshot.");
            await Task.Delay(10);
        }
    }
}
