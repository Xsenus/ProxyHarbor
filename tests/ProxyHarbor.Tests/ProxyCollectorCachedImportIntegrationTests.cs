using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using SnapshotDatabase = ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests.SnapshotDatabase;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyCollectorCachedImportIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CachedPassesDrainWithSameLimitsWithoutHttpOrChangingSourceHealth()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("cached-only");
        var previousFetch = DateTimeOffset.UtcNow.AddDays(-1);
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LastFetchedAt, previousFetch)
                .SetProperty(item => item.LastSucceededAt, previousFetch)
                .SetProperty(item => item.LastError, "provider refused")
                .SetProperty(item => item.ConsecutiveFailures, 3)
                .SetProperty(item => item.HttpETag, "\"original\""));
        await SeedAsync(database, source, "8.8.8.8:80\n8.8.8.8:81\n8.8.8.8:82");
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            using var collector = Collector(database);
            var run = Assert.IsType<CollectionRun>(await collector.ImportCachedSourcesAsync(CancellationToken.None));
            Assert.Equal(1, run.CandidatesFound);
            Assert.Equal(1, run.NewProxies);
            await using var verify = database.Factory.CreateDbContext();
            var snapshot = await verify.ProxySourceImportStates.SingleAsync();
            Assert.Equal(cycle, snapshot.NextIndex);
            var stored = await verify.Sources.SingleAsync();
            Assert.Equal(previousFetch.ToUnixTimeMilliseconds(), stored.LastFetchedAt!.Value.ToUnixTimeMilliseconds());
            Assert.Equal("provider refused", stored.LastError);
            Assert.Equal(3, stored.ConsecutiveFailures);
            Assert.Equal("\"original\"", stored.HttpETag);
            Assert.Null(stored.NextFetchAt); // HTTP is due, but cached-only must still never fetch.
        }
        using (var collector = Collector(database))
            Assert.Null(await collector.ImportCachedSourcesAsync(CancellationToken.None));
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal(3, await final.Runs.CountAsync());
        Assert.Empty((await final.ProxySourceImportStates.SingleAsync()).Payload);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task GlobalQuotaLeavesOtherCursorUnacknowledgedUntilNextPass()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var first = await database.AddSourceAsync("first-cached");
        var second = await database.AddSourceAsync("second-cached");
        await SeedAsync(database, first, "8.8.8.8:80\n8.8.8.8:81");
        await SeedAsync(database, second, "1.1.1.1:80\n1.1.1.1:81");
        using var collector = Collector(database);
        for (var cycle = 0; cycle < 2; cycle++)
            Assert.Equal(1, (await collector.ImportCachedSourcesAsync(CancellationToken.None))!.CandidatesFound);
        await using var verify = database.Factory.CreateDbContext();
        var states = await verify.ProxySourceImportStates.ToArrayAsync();
        Assert.All(states, state => Assert.Equal(1, state.NextIndex));
        Assert.Equal(2, await verify.Proxies.CountAsync());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PaidSnapshotsAreExcludedAndEmptyPassCreatesNoAudit()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("paid-cached");
        source.Url = PaidProxySourceCatalog.BestProxiesUrl;
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Url, source.Url));
        await SeedAsync(database, source, "8.8.8.8:80");
        using var collector = Collector(database);
        Assert.Null(await collector.ImportCachedSourcesAsync(CancellationToken.None));
        await using var verify = database.Factory.CreateDbContext();
        Assert.Empty(await verify.Runs.ToArrayAsync());
        Assert.Equal(0, (await verify.ProxySourceImportStates.SingleAsync()).NextIndex);
        Assert.Empty(await verify.Proxies.ToArrayAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "PostgresIntegration")]
    public async Task ChangedOrDisabledSourceCannotImportOldSnapshot(bool disable)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("changed-cached");
        await SeedAsync(database, source, "8.8.8.8:80");
        await using (var db = database.Factory.CreateDbContext())
        {
            if (disable) await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Enabled, false));
            else await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DefaultProtocol, ProxyProtocol.Socks5));
        }
        using var collector = Collector(database);
        Assert.Null(await collector.ImportCachedSourcesAsync(CancellationToken.None));
        await using var verify = database.Factory.CreateDbContext();
        Assert.Empty(await verify.Proxies.ToArrayAsync());
        Assert.Empty(await verify.Runs.ToArrayAsync());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FailedBulkCommitDoesNotAdvanceCursorAndRestartReplaysWithoutNetwork()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("failed-cached");
        await SeedAsync(database, source, "8.8.8.8:80\n8.8.8.8:81");
        await using (var db = database.Factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_cached_import() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'expected cached import failure'; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_cached_import BEFORE INSERT ON "Proxies"
                FOR EACH ROW EXECUTE FUNCTION reject_cached_import();
                """);
        using (var collector = Collector(database))
            await Assert.ThrowsAsync<PostgresException>(() => collector.ImportCachedSourcesAsync(CancellationToken.None));
        await using (var verify = database.Factory.CreateDbContext())
        {
            Assert.Equal(0, (await verify.ProxySourceImportStates.SingleAsync()).NextIndex);
            Assert.Empty(await verify.Proxies.ToArrayAsync());
            Assert.Equal("failed", (await verify.Runs.SingleAsync()).Status);
            await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_cached_import ON \"Proxies\"");
        }
        using (var collector = Collector(database))
            Assert.Equal(1, (await collector.ImportCachedSourcesAsync(CancellationToken.None))!.NewProxies);
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal(1, (await final.ProxySourceImportStates.SingleAsync()).NextIndex);
        Assert.Single(await final.Proxies.ToArrayAsync());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CorruptCacheIsDiscardedWithoutNetworkOrFakeSourceFailure()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("corrupt-cached");
        await SeedAsync(database, source, "8.8.8.8:80");
        await using (var db = database.Factory.CreateDbContext())
        {
            var state = await db.ProxySourceImportStates.SingleAsync();
            state.Payload[8] ^= 1;
            db.Entry(state).Property(item => item.Payload).IsModified = true;
            await db.SaveChangesAsync();
        }
        using var collector = Collector(database);
        Assert.Equal(0, (await collector.ImportCachedSourcesAsync(CancellationToken.None))!.CandidatesFound);
        Assert.Null(await collector.ImportCachedSourcesAsync(CancellationToken.None));
        await using var verify = database.Factory.CreateDbContext();
        Assert.Empty(await verify.ProxySourceImportStates.ToArrayAsync());
        Assert.Empty(await verify.Proxies.ToArrayAsync());
        Assert.Null((await verify.Sources.SingleAsync()).LastError);
        Assert.Null((await verify.Sources.SingleAsync()).LastFetchedAt);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CachedImportUsesSameClusterLockAsFullCollection()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("locked-cached");
        await SeedAsync(database, source, "8.8.8.8:80");
        await using var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            database.Factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None);
        Assert.NotNull(lease);
        using var collector = Collector(database);
        await Assert.ThrowsAsync<OperationAlreadyRunningException>(() => collector.ImportCachedSourcesAsync(CancellationToken.None));
        await using var verify = database.Factory.CreateDbContext();
        Assert.Empty(await verify.Runs.ToArrayAsync());
        Assert.Equal(0, (await verify.ProxySourceImportStates.SingleAsync()).NextIndex);
    }

    private static Task<ProxySourceImportState?> SeedAsync(SnapshotDatabase database, ProxySource source, string body) =>
        new ProxySourceImportStore(database.Factory).BeginAsync(source,
            ProxyCandidateSnapshotCodec.Encode(body, source.DefaultProtocol), CancellationToken.None);

    private static ProxyCollector Collector(SnapshotDatabase database) => new(database.Factory, new NoNetworkClients(),
        Options.Create(new CollectorOptions { SourceConcurrency = 1, MaxProxiesPerSource = 1, MaxCandidatesPerRun = 1 }),
        NullLogger<ProxyCollector>.Instance);

    private sealed class NoNetworkClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Cached import attempted to create an HTTP client");
    }
}
