using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using SnapshotDatabase = ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests.SnapshotDatabase;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class FreeProxyDbSourceApiFetcherIntegrationTests
{
    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task PartialPagesAnd429ResumeWithoutRefetchingPrefixAndCooldownIsSharedWithVpn()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var proxy = await AddProxyAsync(database);
        var vpn = await AddVpnAsync(database);
        var owner = SourceApiCaptureOwner.From(proxy);
        var fetcher = new FreeProxyDbSourceApiFetcher(database.Factory);
        await Assert.ThrowsAsync<SourceApiDeferredException>(() => fetcher.FetchAsync(owner,
            (_, _) => Task.FromResult(Response(Page(3, HttpRow(3, "8.8.4.4")))), CancellationToken.None, 1));
        var saved = Assert.IsType<SourceApiCaptureCheckpoint>(await new SourceApiCaptureStore(database.Factory).LoadAsync(owner, CancellationToken.None));
        var originalTime = saved.Capture.Pages[0].CapturedAt;
        await ElapsePacingAsync(database);
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await Assert.ThrowsAsync<SourceRateLimitException>(() => fetcher.FetchAsync(owner, (url, _) =>
        {
            Assert.Contains("page_index=2&", url);
            throw new SourceRateLimitException(retryAt, HttpStatusCode.TooManyRequests);
        }, CancellationToken.None, 1));
        var requests = 0;
        var blocked = await Assert.ThrowsAsync<SourceApiDeferredException>(() => new FreeProxyDbSourceApiFetcher(database.Factory)
            .FetchAsync(SourceApiCaptureOwner.From(vpn), (_, _) => { requests++; throw new InvalidOperationException(); }, CancellationToken.None));
        Assert.Equal(0, requests);
        Assert.True(blocked.NotBefore >= retryAt.AddMilliseconds(-1));
        await ElapsePacingAsync(database);
        await Assert.ThrowsAsync<SourceApiDeferredException>(() => fetcher.FetchAsync(owner, (url, _) =>
        {
            Assert.Contains("page_index=2&", url);
            return Task.FromResult(Response(Page(3, HttpRow(2, "1.1.1.1"), HttpRow(1, "9.9.9.9"))));
        }, CancellationToken.None, 1));
        await ElapsePacingAsync(database);
        var complete = await fetcher.FetchAsync(owner, (url, _) =>
        {
            Assert.Contains("page_index=1&", url);
            return Task.FromResult(Response(Page(4, HttpRow(4, "8.8.8.8"), HttpRow(3, "8.8.4.4"))));
        }, CancellationToken.None, 1);
        Assert.True(complete.NetworkObserved);
        Assert.Equal(originalTime, complete.ObservedAt);
        Assert.Equal(4, SourceFeedParser.ParseRequired(complete.Fetch.Content!, ProxyProtocol.Http).Count);
        await new SourceApiOriginGate(database.Factory).ExtendDeadlineAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        var cached = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner,
            (_, _) => throw new InvalidOperationException("Cached body performed HTTP."), CancellationToken.None);
        Assert.False(cached.NetworkObserved);
        Assert.Equal(complete.Fetch.Content, cached.Fetch.Content);
        Assert.Equal(originalTime, cached.ObservedAt);
        Assert.Equal(complete.NextRefreshAt, cached.NextRefreshAt);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task FullHttpBodyStaysAvailableUntilOldTailAllowsWholeSnapshotAdmission()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddProxyAsync(database);
        var originalAt = DateTimeOffset.UtcNow.AddHours(-3);
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextFetchAt, DateTimeOffset.UtcNow.AddHours(6))
                .SetProperty(item => item.LastFetchedAt, originalAt).SetProperty(item => item.LastSucceededAt, originalAt)
                .SetProperty(item => item.LastContentFetchedAt, originalAt).SetProperty(item => item.LastItemCount, 3));
        await new ProxySourceImportStore(database.Factory).BeginAsync(source,
            ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80\n8.8.8.8:81\n8.8.8.8:82", ProxyProtocol.Http), CancellationToken.None);
        await PrimeAsync(database, SourceApiCaptureOwner.From(source), Page(3,
            HttpRow(3, "8.8.4.4"), HttpRow(2, "1.1.1.1"), HttpRow(1, "9.9.9.9")), originalAt);
        var clients = new NoHttpClients();
        using var collector = new ProxyCollector(database.Factory, clients, Options.Create(Settings()), NullLogger<ProxyCollector>.Instance);
        await collector.CollectAsync(CancellationToken.None, true);
        await using (var verify = database.Factory.CreateDbContext())
        {
            Assert.Single(await verify.SourceApiCaptureStates.ToArrayAsync());
            Assert.Equal(0, (await verify.ProxySourceImportStates.SingleAsync()).NextIndex);
        }
        for (var cycle = 0; cycle < 8; cycle++) await collector.CollectAsync(CancellationToken.None, true);
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal(6, await final.Proxies.CountAsync());
        Assert.Empty(await final.SourceApiCaptureStates.ToArrayAsync());
        var state = await final.ProxySourceImportStates.SingleAsync();
        Assert.Equal(3, state.NextIndex);
        Assert.Empty(state.Payload);
        var health = await final.Sources.SingleAsync();
        Assert.Equal(originalAt.UtcTicks / 10, health.LastSucceededAt!.Value.UtcTicks / 10);
        Assert.True(health.NextFetchAt > DateTimeOffset.UtcNow.AddHours(2));
        Assert.Equal(0, clients.Requests);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task VpnApiCachedReplayPreservesOlderEpochAndCannotOverwriteNewerUri()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddVpnAsync(database);
        var oldAt = DateTimeOffset.UtcNow.AddHours(-2);
        var latestAt = DateTimeOffset.UtcNow.AddHours(-1);
        const string oldUri = "vless://old@8.8.8.8:443?type=tcp#old";
        const string latestUri = "vless://latest@8.8.8.8:443?type=tcp#latest";
        const string neighbour = "ss://YWVzLTI1Ni1nY206cHc=@1.1.1.1:8388#neighbour";
        await using (var db = database.Factory.CreateDbContext())
        {
            db.VpnEndpoints.Add(new VpnEndpoint
            {
                Host = "8.8.8.8",
                Port = 443,
                Protocol = VpnProtocol.Vless,
                Transport = "tcp",
                ConnectionUri = latestUri,
                ConnectionUriObservedAt = latestAt,
                LastSeenAt = latestAt,
                FirstSeenAt = oldAt
            });
            await db.SaveChangesAsync();
        }
        await PrimeAsync(database, SourceApiCaptureOwner.From(source), Page(4,
            new { id = 4, protocol = "vless", connect_string = oldUri },
            new { id = 3, protocol = "ss", connect_string = neighbour },
            new { id = 2, protocol = "vless", connect_string = "vless://unsafe@127.0.0.1:443" },
            new { id = 1, protocol = "vless", connect_string = "vless://bad@9.9.9.9:443\nhttp://127.0.0.1:80" }), oldAt);
        var clients = new NoHttpClients();
        var service = new VpnCatalogService(database.Factory, clients, Options.Create(Settings()), NullLogger<VpnCatalogService>.Instance);
        await service.CollectAsync(true);
        await service.CollectAsync(true);
        await using var final = database.Factory.CreateDbContext();
        var endpoints = await final.VpnEndpoints.ToArrayAsync();
        Assert.Equal(2, endpoints.Length);
        Assert.Equal(latestUri, endpoints.Single(item => item.Port == 443).ConnectionUri);
        Assert.Equal(latestAt.UtcTicks / 10, endpoints.Single(item => item.Port == 443).ConnectionUriObservedAt!.Value.UtcTicks / 10);
        Assert.Equal(neighbour, endpoints.Single(item => item.Port == 8388).ConnectionUri);
        Assert.Equal(oldAt.UtcTicks / 10, endpoints.Single(item => item.Port == 8388).ConnectionUriObservedAt!.Value.UtcTicks / 10);
        var health = await final.VpnSources.SingleAsync();
        Assert.Null(health.LastSucceededAt);
        Assert.Null(health.LastFetchedAt);
        Assert.Null(health.LastError);
        Assert.True(health.NextFetchAt > DateTimeOffset.UtcNow.AddHours(3));
        Assert.Empty(await final.SourceApiCaptureStates.ToArrayAsync());
        Assert.Equal(0, clients.Requests);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task MalformedNextPageKeepsValidatedPrefixForRetry()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var owner = SourceApiCaptureOwner.From(await AddProxyAsync(database));
        var fetcher = new FreeProxyDbSourceApiFetcher(database.Factory);
        await Assert.ThrowsAsync<SourceApiDeferredException>(() => fetcher.FetchAsync(owner,
            (_, _) => Task.FromResult(Response(Page(2, HttpRow(2, "8.8.8.8")))), CancellationToken.None, 1));
        var store = new SourceApiCaptureStore(database.Factory);
        var first = Assert.IsType<SourceApiCaptureCheckpoint>(await store.LoadAsync(owner, CancellationToken.None));
        await ElapsePacingAsync(database);
        await Assert.ThrowsAsync<InvalidDataException>(() => fetcher.FetchAsync(owner,
            (_, _) => Task.FromResult(Response("{\"status\":0,\"data\":null}")), CancellationToken.None, 1));
        var retained = Assert.IsType<SourceApiCaptureCheckpoint>(await store.LoadAsync(owner, CancellationToken.None));
        Assert.Equal(first.Version, retained.Version);
        Assert.Equal(2, retained.Capture.Inspect(owner.MaximumBytes).NextPage);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task UnusableCompletedVpnBodyIsDiscardedWithoutPublishingSuccess()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddVpnAsync(database);
        await PrimeAsync(database, SourceApiCaptureOwner.From(source), Page(1,
            new { id = 1, protocol = "vless", connect_string = "vless://unsafe@127.0.0.1:443" }), DateTimeOffset.UtcNow.AddHours(-1));
        var clients = new NoHttpClients();
        var service = new VpnCatalogService(database.Factory, clients, Options.Create(Settings()), NullLogger<VpnCatalogService>.Instance);
        await service.CollectAsync(true);
        await using var db = database.Factory.CreateDbContext();
        Assert.Empty(await db.SourceApiCaptureStates.ToArrayAsync());
        Assert.Empty(await db.VpnEndpoints.ToArrayAsync());
        Assert.Null((await db.VpnSources.SingleAsync()).LastSucceededAt);
        Assert.Equal(0, clients.Requests);
    }

    private static CollectorOptions Settings() => new() { MaxProxiesPerSource = 1, MaxCandidatesPerRun = 1, SourceConcurrency = 1, SourceRetryCount = 0 };

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlternativeSearchUrlCannotBypassDurableOriginLane(bool vpn)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        if (vpn)
        {
            var source = await AddVpnAsync(database);
            await using var db = database.Factory.CreateDbContext();
            await db.VpnSources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Url, source.Url + "&page_size=1"));
        }
        else
        {
            var source = await AddProxyAsync(database);
            await using var db = database.Factory.CreateDbContext();
            await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Url, source.Url + "&page_size=1"));
        }
        var clients = new NoHttpClients();
        if (vpn)
            await new VpnCatalogService(database.Factory, clients, Options.Create(Settings()), NullLogger<VpnCatalogService>.Instance).CollectAsync(true);
        else
        {
            using var collector = new ProxyCollector(database.Factory, clients, Options.Create(Settings()), NullLogger<ProxyCollector>.Instance);
            await collector.CollectAsync(CancellationToken.None, true);
        }
        await using var final = database.Factory.CreateDbContext();
        var error = vpn ? (await final.VpnSources.SingleAsync()).LastError : (await final.Sources.SingleAsync()).LastError;
        Assert.Contains("канонический URL", error, StringComparison.Ordinal);
        Assert.Empty(await final.SourceApiCaptureStates.ToArrayAsync());
        Assert.Empty(await final.SourceApiOriginStates.ToArrayAsync());
        Assert.Equal(0, clients.Requests);
    }
    private static SourceFetchResult Response(string body) => new(body, false, null, null);
    private static object HttpRow(int id, string ip) => new { id, ip, port = 80, protocol = "http" };
    private static string Page(int total, params object[] rows) => JsonSerializer.Serialize(new { status = 1, data = new { total_count = total, data = rows } });
    private static async Task ElapsePacingAsync(SnapshotDatabase database)
    {
        await using var db = database.Factory.CreateDbContext();
        await db.SourceApiOriginStates.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NotBefore, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }
    private static async Task PrimeAsync(SnapshotDatabase database, SourceApiCaptureOwner owner, string page, DateTimeOffset at)
    {
        var store = new SourceApiCaptureStore(database.Factory);
        var capture = new FreeProxyDbPageCapture().Append(new(1, false, at, page), owner.MaximumBytes);
        var first = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(owner, null, capture, CancellationToken.None));
        capture = capture.Append(new(1, true, at, page), owner.MaximumBytes);
        Assert.NotNull(await store.SaveAsync(owner, first, capture, CancellationToken.None));
    }
    private static async Task<ProxySource> AddProxyAsync(SnapshotDatabase database)
    {
        await using var db = database.Factory.CreateDbContext();
        var source = new ProxySource { Name = "API HTTP", Url = FreeProxyDbFeedFetcher.Url };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return source;
    }
    private static async Task<VpnSource> AddVpnAsync(SnapshotDatabase database)
    {
        await using var db = database.Factory.CreateDbContext();
        var source = new VpnSource { Name = "API VPN", Provider = "Test fixture", License = "Test fixture", Url = FreeProxyDbPageCapture.VpnUrl };
        db.VpnSources.Add(source);
        await db.SaveChangesAsync();
        return source;
    }
    private sealed class NoHttpClients : HttpMessageHandler, IHttpClientFactory
    {
        internal int Requests;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            throw new InvalidOperationException("Completed cache or cooldown caused unexpected HTTP.");
        }
    }
}
