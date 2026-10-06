using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using SnapshotDatabase = ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests.SnapshotDatabase;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyCollectorImportContinuationIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ChangingBodyCannotStarvePendingTailAcrossCollectorRestarts()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("changing-body");
        var handler = new FeedHandler(request => request == 1
            ? "8.8.8.8:80\n8.8.8.8:81\n8.8.8.8:82" : "1.1.1.1:99");
        using var clients = new FeedClients(handler);
        Guid firstSnapshotId = default;
        for (var cycle = 1; cycle <= 4; cycle++)
        {
            using var collector = Collector(database, clients);
            var run = await collector.CollectAsync(CancellationToken.None);
            Assert.Equal(1, run.NewProxies);
            await using var verify = database.Factory.CreateDbContext();
            var state = await verify.ProxySourceImportStates.SingleAsync();
            if (cycle == 1) firstSnapshotId = state.SnapshotId;
            Assert.Equal(firstSnapshotId, state.SnapshotId);
            Assert.Equal(cycle <= 2 ? 1 : cycle - 1, state.NextIndex);
            Assert.Equal(cycle, await verify.Proxies.CountAsync());
            if (cycle == 2)
            {
                Assert.True(await verify.Proxies.AnyAsync(proxy => proxy.Port == 99));
                Assert.False(state.PreferFresh);
            }
            if (cycle == 4) Assert.Empty(state.Payload);
        }
        Assert.Equal(4, handler.Requests);
        await using var final = database.Factory.CreateDbContext();
        var ports = await final.Proxies.OrderBy(proxy => proxy.Port).Select(proxy => proxy.Port).ToArrayAsync();
        Assert.Equal([80, 81, 82, 99], ports);
        Assert.Equal(1, (await final.Sources.SingleAsync(item => item.Id == source.Id)).LastItemCount);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task GlobalQuotaRotatesToPreviouslyUnacknowledgedSource()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var first = await database.AddSourceAsync("first");
        var second = await database.AddSourceAsync("second");
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.Where(source => source.Id == second.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(source => source.Priority, 101));
        var handler = new FeedHandler(_ => "unused", request => request.RequestUri!.AbsolutePath.Contains("first", StringComparison.Ordinal)
            ? "8.8.8.8:80" : "1.1.1.1:443");
        using var clients = new FeedClients(handler);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var collector = Collector(database, clients, globalLimit: 1);
            var run = await collector.CollectAsync(CancellationToken.None);
            Assert.Equal(1, run.NewProxies);
            Assert.True(run.CandidateLimitReached);
            await using var verify = database.Factory.CreateDbContext();
            Assert.Equal(cycle + 1, await verify.Proxies.CountAsync());
            if (cycle == 0)
            {
                var states = await verify.ProxySourceImportStates.ToDictionaryAsync(item => item.ProxySourceId);
                Assert.Equal(1, states[first.Id].NextIndex);
                Assert.Equal(0, states[second.Id].NextIndex);
                Assert.Null(states[second.Id].LastProgressAt);
            }
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CachedContinuationHonorsHttpScheduleWithoutFabricatingFetchFreshness()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("scheduled");
        var handler = new FeedHandler(_ => "8.8.8.8:80\n8.8.8.8:81\n8.8.8.8:82");
        using var clients = new FeedClients(handler);
        using (var collector = Collector(database, clients))
            _ = await collector.CollectAsync(CancellationToken.None);
        DateTimeOffset? fetchedAt;
        var nextFetchAt = DateTimeOffset.UtcNow.AddHours(2);
        await using (var db = database.Factory.CreateDbContext())
        {
            fetchedAt = (await db.Sources.SingleAsync()).LastFetchedAt;
            await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextFetchAt, nextFetchAt));
        }
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var collector = Collector(database, clients);
            var run = await collector.CollectAsync(CancellationToken.None);
            Assert.Equal(1, run.SourcesProcessed);
            Assert.Equal(1, run.NewProxies);
        }
        using (var collector = Collector(database, clients))
        {
            var paused = await collector.CollectAsync(CancellationToken.None);
            Assert.Equal(0, paused.SourcesProcessed);
            Assert.Equal(1, paused.SourcesSkipped);
        }
        Assert.Equal(1, handler.Requests);
        await using var verify = database.Factory.CreateDbContext();
        var storedSource = await verify.Sources.SingleAsync(item => item.Id == source.Id);
        Assert.Equal(fetchedAt, storedSource.LastFetchedAt);
        Assert.True(Math.Abs((storedSource.NextFetchAt!.Value - nextFetchAt).TotalMilliseconds) < 1);
        Assert.Equal(3, await verify.Proxies.CountAsync());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task BulkFailureKeepsUnacknowledgedSnapshotAndHttpHealthForSafeReplay()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        _ = await database.AddSourceAsync("failed-import");
        var handler = new FeedHandler(request => request == 1 ? "8.8.8.8:80\n8.8.8.8:81" : "1.1.1.1:99");
        using var clients = new FeedClients(handler);
        await using (var db = database.Factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_snapshot_test_import() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'expected snapshot import failure'; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_snapshot_test_import BEFORE INSERT ON "Proxies"
                FOR EACH ROW EXECUTE FUNCTION reject_snapshot_test_import();
                """);
        using (var collector = Collector(database, clients))
            await Assert.ThrowsAsync<PostgresException>(() => collector.CollectAsync(CancellationToken.None));
        await using (var verify = database.Factory.CreateDbContext())
        {
            var state = await verify.ProxySourceImportStates.SingleAsync();
            Assert.Equal(0, state.NextIndex);
            Assert.Null(state.LastProgressAt);
            Assert.NotEmpty(state.Payload);
            Assert.Empty(await verify.Proxies.ToArrayAsync());
            var source = await verify.Sources.SingleAsync();
            Assert.NotNull(source.LastSucceededAt);
            Assert.Null(source.LastError);
            Assert.Equal("failed", (await verify.Runs.SingleAsync()).Status);
            await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_snapshot_test_import ON \"Proxies\"");
        }
        using (var collector = Collector(database, clients))
            Assert.Equal(1, (await collector.CollectAsync(CancellationToken.None)).NewProxies);
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal("1.1.1.1", (await final.Proxies.SingleAsync()).Host);
        Assert.Equal(0, (await final.ProxySourceImportStates.SingleAsync()).NextIndex);
        Assert.False((await final.ProxySourceImportStates.SingleAsync()).PreferFresh);
        using (var collector = Collector(database, clients))
            Assert.Equal(1, (await collector.CollectAsync(CancellationToken.None)).NewProxies);
        await final.Entry(await final.ProxySourceImportStates.SingleAsync()).ReloadAsync();
        Assert.Equal(1, (await final.ProxySourceImportStates.SingleAsync()).NextIndex);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ContinuouslyChangingBodySharesSingleCandidateQuotaWithOldTail()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        _ = await database.AddSourceAsync("always-changing");
        using var clients = new FeedClients(new FeedHandler(request => request == 1
            ? "8.8.8.8:80\n8.8.8.8:81\n8.8.8.8:82" : $"1.1.1.1:{100 + request}"));
        for (var cycle = 1; cycle <= 5; cycle++)
        {
            using var collector = Collector(database, clients, globalLimit: 1);
            var run = await collector.CollectAsync(CancellationToken.None);
            Assert.Equal(1, run.CandidatesFound);
            Assert.Equal(1, run.NewProxies);
            await using var verify = database.Factory.CreateDbContext();
            var state = await verify.ProxySourceImportStates.SingleAsync();
            Assert.Equal((cycle + 1) / 2, state.NextIndex);
            Assert.Equal(cycle % 2 == 1, state.PreferFresh);
            Assert.NotNull(state.LastProgressAt);
            if (cycle == 5) Assert.Empty(state.Payload);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FailedFreshImportPreservesLaneAndHashForReplay()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        _ = await database.AddSourceAsync("fresh-failure");
        using var clients = new FeedClients(new FeedHandler(request => request == 1
            ? "8.8.8.8:80\n8.8.8.8:81" : "1.1.1.1:99"));
        using (var collector = Collector(database, clients))
            _ = await collector.CollectAsync(CancellationToken.None);
        await using var db = database.Factory.CreateDbContext();
        var before = await db.ProxySourceImportStates.AsNoTracking().SingleAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_fresh_test_import() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'expected fresh import failure'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_fresh_test_import BEFORE INSERT ON "Proxies"
            FOR EACH ROW EXECUTE FUNCTION reject_fresh_test_import();
            """);
        using (var collector = Collector(database, clients))
            await Assert.ThrowsAsync<PostgresException>(() => collector.CollectAsync(CancellationToken.None));
        var failed = await db.ProxySourceImportStates.AsNoTracking().SingleAsync();
        Assert.Equal(before.NextIndex, failed.NextIndex);
        Assert.Equal(before.LastProgressAt, failed.LastProgressAt);
        Assert.Equal(before.FreshBodyHash, failed.FreshBodyHash);
        Assert.True(failed.PreferFresh);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_fresh_test_import ON \"Proxies\"");
        using (var collector = Collector(database, clients))
            Assert.Equal(1, (await collector.CollectAsync(CancellationToken.None)).NewProxies);
        var committed = await db.ProxySourceImportStates.AsNoTracking().SingleAsync();
        Assert.Equal(before.NextIndex, committed.NextIndex);
        Assert.False(committed.PreferFresh);
        Assert.NotEqual(before.FreshBodyHash, committed.FreshBodyHash);
        Assert.True(committed.LastProgressAt > before.LastProgressAt);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FreshPrefixAndPendingTailShareLargerSourceQuota()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        _ = await database.AddSourceAsync("shared-quota");
        using var clients = new FeedClients(new FeedHandler(request => request == 1
            ? "8.8.8.8:80\n8.8.8.8:81\n8.8.8.8:82\n8.8.8.8:83\n8.8.8.8:84\n8.8.8.8:85"
            : "1.1.1.1:99\n1.1.1.1:100\n1.1.1.1:101"));
        using (var collector = Collector(database, clients, sourceLimit: 4))
            Assert.Equal(4, (await collector.CollectAsync(CancellationToken.None)).CandidatesFound);
        using (var collector = Collector(database, clients, sourceLimit: 4))
        {
            var run = await collector.CollectAsync(CancellationToken.None);
            Assert.Equal(4, run.CandidatesFound);
            Assert.Equal(4, run.NewProxies);
        }
        await using var verify = database.Factory.CreateDbContext();
        Assert.True(await verify.Proxies.AnyAsync(proxy => proxy.Port == 99));
        Assert.True(await verify.Proxies.AnyAsync(proxy => proxy.Port == 100));
        var state = await verify.ProxySourceImportStates.SingleAsync();
        Assert.Equal(6, state.NextIndex);
        Assert.Empty(state.Payload);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task InFlightConfigurationChangeDiscardsBothFreshPrefixAndOldTail()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("fresh-config-change");
        using var clients = new FeedClients(new FeedHandler(_ => "unused", _ =>
        {
            using var db = database.Factory.CreateDbContext();
            var persisted = db.Sources.Single();
            if (persisted.LastFetchedAt is not null)
            {
                persisted.Url = "https://8.8.8.8/replacement.txt";
                db.SaveChanges();
                return "1.1.1.1:99";
            }
            return "8.8.8.8:80\n8.8.8.8:81";
        }));
        using (var collector = Collector(database, clients))
            _ = await collector.CollectAsync(CancellationToken.None);
        using (var collector = Collector(database, clients))
            Assert.Equal(0, (await collector.CollectAsync(CancellationToken.None)).CandidatesFound);
        await using var verify = database.Factory.CreateDbContext();
        Assert.Equal(1, await verify.Proxies.CountAsync());
        Assert.Empty(await verify.ProxySourceImportStates.ToArrayAsync());
        Assert.NotEqual(source.Url, (await verify.Sources.SingleAsync()).Url);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task UnacknowledgedFreshBodyDoesNotKeepValidatorsThatWouldHideReplay()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        _ = await database.AddSourceAsync("fresh-validators");
        var requests = 0;
        using var clients = new FeedClients(new FeedHandler(_ => "unused", request =>
        {
            requests++;
            if (requests == 2) Assert.Single(request.Headers.IfNoneMatch);
            else Assert.Empty(request.Headers.IfNoneMatch);
            return requests switch
            {
                1 => "8.8.8.8:80\n8.8.8.8:81\n8.8.8.8:82",
                2 => "1.1.1.1:99",
                _ => "1.1.1.1:100"
            };
        }, sendETag: true));
        for (var cycle = 1; cycle <= 5; cycle++)
        {
            using var collector = Collector(database, clients);
            var run = await collector.CollectAsync(CancellationToken.None);
            Assert.Equal(1, run.NewProxies);
            await using var db = database.Factory.CreateDbContext();
            var source = await db.Sources.SingleAsync();
            if (cycle is >= 2 and <= 4) Assert.Null(source.HttpETag);
            else Assert.NotNull(source.HttpETag);
            if (cycle == 3)
            {
                Assert.False(await db.Proxies.AnyAsync(proxy => proxy.Port == 100));
                Assert.True((await db.ProxySourceImportStates.SingleAsync()).PreferFresh);
            }
            if (cycle == 4) Assert.True(await db.Proxies.AnyAsync(proxy => proxy.Port == 100));
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task MissingImportStateForcesBodyDespiteRestoredSuccessfulHttpValidators()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        _ = await database.AddSourceAsync("restored");
        var previousFetch = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.LastFetchedAt, previousFetch)
                .SetProperty(source => source.LastSucceededAt, previousFetch)
                .SetProperty(source => source.LastContentFetchedAt, previousFetch)
                .SetProperty(source => source.LastItemCount, 1)
                .SetProperty(source => source.HttpETag, "\"old-body\""));
        var handler = new FeedHandler(_ => "unused", request =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            return "8.8.8.8:80";
        });
        using var clients = new FeedClients(handler);
        using var collector = Collector(database, clients);
        Assert.Equal(1, (await collector.CollectAsync(CancellationToken.None)).NewProxies);
        Assert.Equal(1, handler.Requests);
        await using var verify = database.Factory.CreateDbContext();
        Assert.True((await verify.Sources.SingleAsync()).LastContentFetchedAt > previousFetch);
    }

    private static ProxyCollector Collector(SnapshotDatabase database, IHttpClientFactory clients, int globalLimit = 10, int sourceLimit = 1) => new(
        database.Factory, clients,
        Options.Create(new CollectorOptions { SourceConcurrency = 1, SourceRetryCount = 0, MaxProxiesPerSource = sourceLimit, MaxCandidatesPerRun = globalLimit }),
        NullLogger<ProxyCollector>.Instance);

    private sealed class FeedClients(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler);
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class FeedHandler(
        Func<int, string> content, Func<HttpRequestMessage, string>? bySource = null, bool sendETag = false) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bySource?.Invoke(request) ?? content(Requests))
            };
            if (sendETag) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"body-{Requests}\"");
            return Task.FromResult(response);
        }
    }
}
