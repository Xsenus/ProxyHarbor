using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет provider-neutral ветку компактного snapshot-store.</summary>
public sealed class MetricsSnapshotStoreTests
{
    [Fact]
    public async Task SaveUpsertsAndLoadReturnsLatestPayload()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-snapshot-store-{Guid.NewGuid():N}").Options;
        var store = new MetricsSnapshotStore(new Factory(options));
        var first = new DateTimeOffset(2026, 9, 3, 1, 0, 0, TimeSpan.Zero);

        await store.SaveAsync(MetricsSnapshotStore.ProxyKey, "{\"revision\":1}", first, CancellationToken.None);
        await store.SaveAsync(
            MetricsSnapshotStore.ProxyKey,
            "{\"revision\":2}",
            first.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal("{\"revision\":2}",
            await store.LoadAsync(MetricsSnapshotStore.ProxyKey, CancellationToken.None));
        await using var verify = new ProxyHarborDbContext(options);
        var state = Assert.Single(await verify.MetricsSnapshotStates.ToArrayAsync());
        Assert.Equal(first.AddMinutes(1), state.CapturedAt);
    }

    [Fact]
    public async Task OlderReplicaCannotOverwriteNewerSnapshot()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-snapshot-store-{Guid.NewGuid():N}").Options;
        var store = new MetricsSnapshotStore(new Factory(options));
        var latest = new DateTimeOffset(2026, 9, 3, 2, 0, 0, TimeSpan.Zero);

        await store.SaveAsync(MetricsSnapshotStore.VpnKey, "{\"revision\":2}", latest, CancellationToken.None);
        await store.SaveAsync(
            MetricsSnapshotStore.VpnKey,
            "{\"revision\":1}",
            latest.AddMinutes(-1),
            CancellationToken.None);

        Assert.Equal("{\"revision\":2}",
            await store.LoadAsync(MetricsSnapshotStore.VpnKey, CancellationToken.None));
    }

    private sealed class Factory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);

        public Task<ProxyHarborDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
