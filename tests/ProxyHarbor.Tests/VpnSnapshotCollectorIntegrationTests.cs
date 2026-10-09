using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using SnapshotDatabase = ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests.SnapshotDatabase;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class VpnSnapshotCollectorIntegrationTests
{
    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task TextBoundedClashWindowsPersistEveryAliasExpandedRecordThroughCachedTail()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "long-clash");
        var setting = new string('x', 14_000);
        var yaml = "setting: &long '" + setting + "'\nproxies: [" + string.Join(',', Enumerable.Range(1, 400).Select(port =>
            $"{{name: node{port}, type: vless, server: 8.8.8.8, port: {port}, uuid: published, future-option: *long}}")) + "]";
        using var clients = new FeedClients(_ => yaml) { MediaType = "application/yaml" };
        var service = Service(database, clients, Settings(10_000));
        var first = await service.CollectAsync();
        Assert.InRange(first.Added, 1, 399);
        await using (var db = database.Factory.CreateDbContext())
            Assert.Equal(first.Added, (await db.VpnSourceImportStates.SingleAsync()).NextIndex);
        var second = await service.CollectAsync();
        Assert.Equal(400, first.Added + second.Added);
        Assert.Equal(1, second.NotModified);
        await using var verify = database.Factory.CreateDbContext();
        var rows = await verify.VpnEndpoints.ToArrayAsync();
        Assert.Equal(Enumerable.Range(1, 400), rows.Select(row => row.Port).Order());
        Assert.All(rows, row => Assert.Contains(setting, row.ClashConfiguration));
        var state = await verify.VpnSourceImportStates.SingleAsync();
        Assert.Equal(400, state.NextIndex);
        Assert.Empty(state.Payload);
        Assert.Equal(400, await verify.VpnEndpointSources.CountAsync());
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData("text/plain")]
    [InlineData("text/yaml")]
    [InlineData("application/yaml")]
    [InlineData("application/x-yaml")]
    public async Task ClashResponsePersistsCompleteConfigurationsAndDrainsCachedTailOn304(string mediaType)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "clash");
        const string yaml = "proxies: [{name: primary, type: anytls, server: 8.8.8.8, port: 443, password: '0012', dialer-proxy: peer, reality-opts: {public-key: preserved}}, {name: peer, type: socks5, server: 1.1.1.1, port: 1080, username: user, password: published}]";
        using var clients = new FeedClients(_ => yaml) { MediaType = mediaType };
        var service = Service(database, clients, Settings(1));
        Assert.Equal(1, (await service.CollectAsync()).Added);
        var resumed = await service.CollectAsync();
        Assert.Equal(1, resumed.Added);
        Assert.Equal(1, resumed.NotModified);
        await using var db = database.Factory.CreateDbContext();
        Assert.Null((await db.VpnSources.SingleAsync()).LastError);
        var state = await db.VpnSourceImportStates.SingleAsync();
        Assert.Equal(2, state.NextIndex);
        Assert.Empty(state.Payload);
        var rows = await db.VpnEndpoints.ToArrayAsync();
        Assert.Equal(2, rows.Length);
        var expected = VpnFeedParser.Parse(yaml, VpnProtocol.OpenVpn);
        foreach (var row in rows)
        {
            var candidate = expected.Single(item => item.Host == row.Host);
            Assert.Equal(candidate.Protocol, row.Protocol);
            Assert.Equal(candidate.ClashConfiguration, row.ClashConfiguration);
            Assert.Equal(state.CreatedAt, row.ClashConfigurationObservedAt);
            Assert.Null(row.ConnectionUri);
        }
        Assert.Equal(2, await db.VpnEndpointSources.CountAsync());
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData("missing")]
    [InlineData("private")]
    public async Task IncompleteOrUnsafeClashChainCannotPublishPartialCatalogOrQueue(string failure)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "unsafe-clash");
        var yaml = failure == "private"
            ? "proxies: [{name: independent, type: http, server: 9.9.9.9, port: 80}, {name: primary, type: vless, server: 8.8.8.8, port: 443, uuid: published-secret, dialer-proxy: peer}, {name: peer, type: http, server: 127.0.0.1, port: 80}]"
            : "proxies: [{name: independent, type: http, server: 9.9.9.9, port: 80}, {name: primary, type: vless, server: 8.8.8.8, port: 443, uuid: published-secret, dialer-proxy: missing}]";
        using var clients = new FeedClients(_ => yaml) { MediaType = "application/yaml" };
        Assert.Equal(0, (await Service(database, clients, Settings(1)).CollectAsync()).Succeeded);
        await using var db = database.Factory.CreateDbContext();
        Assert.Empty(await db.VpnEndpoints.ToArrayAsync());
        Assert.Empty(await db.VpnEndpointSources.ToArrayAsync());
        Assert.Empty(await db.VpnSourceImportStates.ToArrayAsync());
        var error = Assert.IsType<string>((await db.VpnSources.SingleAsync()).LastError);
        Assert.DoesNotContain("published-secret", error);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task LegacyShadowsocksPersistsOriginalUriAndDrainsCachedTailOn304()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "legacy-shadowsocks");
        var first = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:old@8.8.8.8:443")) + "#old,,";
        var last = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:new@8.8.8.8:443")) + "#new,,";
        var neighbour = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:other@1.1.1.1:8388"));
        var unsafeUri = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:private@127.0.0.1:443"));
        using var clients = new FeedClients(_ => first + "\n" + last + "\n" + unsafeUri + "\n" + neighbour);
        var service = Service(database, clients, Settings(1));
        Assert.Equal(1, (await service.CollectAsync()).Added);
        var resumed = await service.CollectAsync();
        Assert.Equal(1, resumed.Added);
        Assert.Equal(1, resumed.NotModified);
        await DrainOriginalProfilesAsync(database, service, clients, 3);
        await using var db = database.Factory.CreateDbContext();
        Assert.Null((await db.VpnSources.SingleAsync()).LastError);
        var endpoints = await db.VpnEndpoints.ToArrayAsync();
        Assert.Equal(2, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Equal(VpnProtocol.Shadowsocks, endpoint.Protocol));
        Assert.Equal(last, endpoints.Single(endpoint => endpoint.Port == 443).ConnectionUri);
        Assert.Equal(neighbour, endpoints.Single(endpoint => endpoint.Port == 8388).ConnectionUri);
        Assert.Equal(new[] { first, last, neighbour }.Order(),
            (await db.VpnConnectionProfiles.Select(profile => profile.ConnectionUri).ToArrayAsync()).Order());
        Assert.Equal(2, await db.VpnEndpointSources.CountAsync());
        var state = await db.VpnSourceImportStates.SingleAsync();
        Assert.Equal(2, state.NextIndex);
        Assert.Empty(state.Payload);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UriPunctuationInSupersededRecordDoesNotBlockImportOr304Continuation(bool quoted)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "punctuation");
        const string first = "vless://first@8.8.8.8:1#old,,";
        const string last = "vless://last@8.8.8.8:1#new";
        const string neighbour = "vless://id@8.8.8.8:2#kept,,";
        var body = (quoted ? System.Text.Json.JsonSerializer.Serialize(first) + "," : first) + "\n" + last + "\n" + neighbour;
        using var clients = new FeedClients(_ => body);
        var service = Service(database, clients, Settings(1));
        Assert.Equal(1, (await service.CollectAsync()).Added);
        var resumed = await service.CollectAsync();
        Assert.Equal(1, resumed.Added);
        Assert.Equal(1, resumed.NotModified);
        await DrainOriginalProfilesAsync(database, service, clients, 3);
        await using var db = database.Factory.CreateDbContext();
        Assert.Null((await db.VpnSources.SingleAsync()).LastError);
        Assert.Equal(last, (await db.VpnEndpoints.SingleAsync(x => x.Port == 1)).ConnectionUri);
        Assert.Equal(neighbour, (await db.VpnEndpoints.SingleAsync(x => x.Port == 2)).ConnectionUri);
        Assert.Equal(new[] { first, last, neighbour }.Order(),
            (await db.VpnConnectionProfiles.Select(profile => profile.ConnectionUri).ToArrayAsync()).Order());
        Assert.Equal(2, await db.VpnEndpointSources.CountAsync());
        var state = await db.VpnSourceImportStates.SingleAsync();
        Assert.Equal(2, state.NextIndex);
        Assert.Empty(state.Payload);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task SmallQuotaPersistsWholeFeedAnd304DrainsBeyondTenThousandWithCanonicalLatestUri()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "large");
        var body = Feed(10_003) + "\nvless://latest@8.8.8.8:1?type=tcp";
        using var clients = new FeedClients(_ => body);
        var options = Settings(200);
        var service = Service(database, clients, options);
        var first = await service.CollectAsync();
        Assert.Equal(200, first.Added);
        DateTimeOffset observedAt;
        await using (var db = database.Factory.CreateDbContext())
        {
            var state = await db.VpnSourceImportStates.SingleAsync();
            Assert.Equal(10_003, state.CandidateCount);
            Assert.Equal(200, state.NextIndex);
            Assert.NotEmpty(state.Payload);
            observedAt = state.CreatedAt;
            Assert.Equal(10_003, (await db.VpnSources.SingleAsync()).LastItemCount);
            Assert.Contains("latest", (await db.VpnEndpoints.SingleAsync(x => x.Port == 1)).ConnectionUri);
        }
        options.MaxProxiesPerSource = 10_000;
        options.MaxCandidatesPerRun = 10_000;
        var resumed = await service.CollectAsync();
        Assert.Equal(9_803, resumed.Added);
        Assert.Equal(1, resumed.NotModified);
        await DrainOriginalProfilesAsync(database, service, clients, 10_004);
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal(10_003, await final.VpnEndpoints.CountAsync());
        Assert.Equal(10_003, await final.VpnEndpointSources.CountAsync());
        Assert.Equal(observedAt, (await final.VpnEndpoints.SingleAsync(x => x.Port == 10_003)).LastSeenAt);
        var completed = await final.VpnSourceImportStates.SingleAsync();
        Assert.Equal(completed.CandidateCount, completed.NextIndex);
        Assert.Empty(completed.Payload);
        Assert.Empty(completed.PayloadHash);
        Assert.Equal(10_004, await final.VpnConnectionProfiles.CountAsync());
        Assert.Equal(2, await final.VpnConnectionProfiles.CountAsync(profile => profile.Port == 1));
        Assert.True(clients.Validators[1]);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task CachedTailDrainsDuringFailureBackoffWithoutFabricatingHttpSuccessOrFreshness()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "backoff");
        using var clients = new FeedClients(_ => Feed(5));
        var service = Service(database, clients, Settings(2));
        Assert.Equal(2, (await service.CollectAsync()).Added);
        clients.Fail = true;
        Assert.Equal(2, (await service.CollectAsync(forceAllSources: true)).Added);
        VpnSource failed;
        DateTimeOffset observedAt;
        await using (var db = database.Factory.CreateDbContext())
        {
            failed = await db.VpnSources.AsNoTracking().SingleAsync();
            observedAt = (await db.VpnSourceImportStates.SingleAsync()).CreatedAt;
            Assert.Equal(1, failed.ConsecutiveFailures);
            Assert.NotNull(failed.NextFetchAt);
        }
        var cached = await service.CollectAsync();
        Assert.Equal(1, cached.Added);
        Assert.Equal(1, cached.Candidates);
        Assert.Equal(0, cached.Succeeded);
        Assert.Equal(0, cached.ContentFetched);
        Assert.Equal(0, cached.NotModified);
        Assert.Equal(2, clients.Validators.Count);
        await using (var db = database.Factory.CreateDbContext())
        {
            var after = await db.VpnSources.SingleAsync();
            Assert.Equal(failed.LastFetchedAt, after.LastFetchedAt);
            Assert.Equal(failed.LastSucceededAt, after.LastSucceededAt);
            Assert.Equal(failed.LastContentFetchedAt, after.LastContentFetchedAt);
            Assert.Equal(failed.NextFetchAt, after.NextFetchAt);
            Assert.Equal(failed.LastError, after.LastError);
            Assert.Equal(observedAt, (await db.VpnEndpoints.SingleAsync(x => x.Port == 5)).LastSeenAt);
        }
        Assert.Equal(0, (await service.CollectAsync()).Sources);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(1)]
    [InlineData(200)]
    public async Task GlobalQuotaOneAlternatesChangedFreshUriAndTailThenRefetchesUnsavedNewBody(int sourceQuota)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "changed");
        var currentBody = Feed(4);
        using var clients = new FeedClients(_ => currentBody);
        var settings = Settings(sourceQuota);
        settings.MaxCandidatesPerRun = 1;
        var service = Service(database, clients, settings);
        await service.CollectAsync();
        currentBody = "vless://rotated@8.8.8.8:3?type=tcp\n" + Feed(2, 5);
        await service.CollectAsync(forceAllSources: true);
        await service.CollectAsync();
        await service.CollectAsync();
        await using (var db = database.Factory.CreateDbContext())
        {
            Assert.Contains("rotated", (await db.VpnEndpoints.SingleAsync(x => x.Port == 3)).ConnectionUri);
            Assert.Equal(3, (await db.VpnSourceImportStates.SingleAsync()).NextIndex);
        }
        await service.CollectAsync(); // Complete the original body with its own epoch.
        await service.CollectAsync(); // Must fetch a full newer body, even though its validators exist.
        Assert.False(clients.Validators[5]);
        await service.CollectAsync();
        await service.CollectAsync();
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal(6, await final.VpnEndpoints.CountAsync());
        Assert.Contains("rotated", (await final.VpnEndpoints.SingleAsync(x => x.Port == 3)).ConnectionUri);
        var state = await final.VpnSourceImportStates.SingleAsync();
        Assert.Equal(3, state.CandidateCount);
        Assert.Equal(3, state.NextIndex);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task ProgressOrderGivesEverySourceCapacityUnderGlobalPressure()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        for (var index = 1; index <= 3; index++) await AddSourceAsync(database, $"source{index}", index);
        using var clients = new FeedClients(path => Feed(4, host: $"9.9.9.{path[^1]}"));
        var service = Service(database, clients, Settings(2));
        for (var run = 0; run < 6; run++)
        {
            var result = await service.CollectAsync();
            Assert.InRange(result.Added, 1, 2);
            await using var progress = database.Factory.CreateDbContext();
            Assert.Equal(Math.Min(run + 1, 3), await progress.VpnEndpointSources
                .Select(link => link.VpnSourceId).Distinct().CountAsync());
        }
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal(12, await final.VpnEndpoints.CountAsync());
        Assert.All(await final.VpnSourceImportStates.ToArrayAsync(), state => Assert.Equal(4, state.NextIndex));
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task MalformedFrameWithMatchingHashIsDiscardedAndRecoveredWithoutConditionalGet()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "corrupt");
        using var clients = new FeedClients(_ => Feed(4));
        var service = Service(database, clients, Settings(1));
        await service.CollectAsync();
        Guid previous;
        await using (var db = database.Factory.CreateDbContext())
        {
            var state = await db.VpnSourceImportStates.SingleAsync();
            previous = state.SnapshotId;
            // Valid outer SHA/header with an invalid canonical-index page reference.
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(state.Payload.AsSpan(state.Payload.Length - (state.CandidateCount - state.NextIndex) * 8), int.MaxValue);
            state.PayloadHash = SHA256.HashData(state.Payload);
            db.Entry(state).Property(x => x.Payload).IsModified = true;
            await db.SaveChangesAsync();
        }
        await service.CollectAsync();
        Assert.False(clients.Validators[1]);
        await using var final = database.Factory.CreateDbContext();
        Assert.NotEqual(previous, (await final.VpnSourceImportStates.SingleAsync()).SnapshotId);
        Assert.Null((await final.VpnSources.SingleAsync()).LastError);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task ImportFailureRollsBackHttpHealthEndpointsAndCursorTogether()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "rollback");
        await using (var db = database.Factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_vpn_import() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected VPN import failure'; END $$;
                CREATE TRIGGER reject_vpn_import BEFORE INSERT ON "VpnEndpoints"
                FOR EACH ROW EXECUTE FUNCTION reject_vpn_import();
                """);
        using var clients = new FeedClients(_ => Feed(3));
        var service = Service(database, clients, Settings(2));
        await Assert.ThrowsAsync<PostgresException>(() => service.CollectAsync());
        await using (var verify = database.Factory.CreateDbContext())
        {
            Assert.Equal(0, (await verify.VpnSourceImportStates.SingleAsync()).NextIndex);
            Assert.Equal(0, await verify.VpnEndpoints.CountAsync());
            Assert.Equal(0, await verify.VpnEndpointSources.CountAsync());
            var source = await verify.VpnSources.SingleAsync();
            Assert.Null(source.LastFetchedAt);
            Assert.Null(source.LastSucceededAt);
            Assert.Equal(0, source.LastItemCount);
            await verify.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER reject_vpn_import ON "VpnEndpoints";
                DROP FUNCTION reject_vpn_import();
                """);
        }
        Assert.Equal(2, (await service.CollectAsync()).Added);
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal(2, (await final.VpnSourceImportStates.SingleAsync()).NextIndex);
        Assert.NotNull((await final.VpnSources.SingleAsync()).LastSucceededAt);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData("disable")]
    [InlineData("url")]
    [InlineData("protocol")]
    [InlineData("version")]
    public async Task InFlightConfigurationOrSnapshotChangeCannotPublishOldHealthCursorOrCandidates(string change)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "race");
        using var clients = new FeedClients(_ => Feed(4));
        var service = Service(database, clients, Settings(1));
        await service.CollectAsync();
        VpnSource before;
        await using (var db = database.Factory.CreateDbContext()) before = await db.VpnSources.AsNoTracking().SingleAsync();
        clients.BeforeResponse = async () =>
        {
            await using var db = database.Factory.CreateDbContext();
            if (change == "version")
                await db.VpnSourceImportStates.ExecuteUpdateAsync(set => set.SetProperty(x => x.SnapshotId, Guid.NewGuid()));
            else
            {
                var source = await db.VpnSources.SingleAsync();
                if (change == "disable") source.Enabled = false;
                if (change == "url") source.Url = "https://8.8.8.8/reconfigured";
                if (change == "protocol") source.DefaultProtocol = VpnProtocol.Trojan;
                await db.SaveChangesAsync();
            }
        };
        var discarded = await service.CollectAsync();
        Assert.Equal(0, discarded.Added);
        Assert.Equal(0, discarded.Succeeded);
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal(1, await final.VpnEndpoints.CountAsync());
        Assert.Equal(1, (await final.VpnSourceImportStates.SingleAsync()).NextIndex);
        var after = await final.VpnSources.SingleAsync();
        Assert.Equal(before.LastFetchedAt, after.LastFetchedAt);
        Assert.Equal(before.LastSucceededAt, after.LastSucceededAt);
        Assert.Equal(before.LastItemCount, after.LastItemCount);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrReconfiguredSourcesReleasePendingStorage(bool reconfigure)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await AddSourceAsync(database, "cleanup");
        using var clients = new FeedClients(_ => Feed(4));
        var service = Service(database, clients, Settings(1));
        await service.CollectAsync();
        Guid original;
        await using (var db = database.Factory.CreateDbContext())
        {
            original = (await db.VpnSourceImportStates.SingleAsync()).SnapshotId;
            var source = await db.VpnSources.SingleAsync();
            if (reconfigure) source.Url = "https://8.8.8.8/new-config";
            else source.Enabled = false;
            await db.SaveChangesAsync();
        }
        await service.CollectAsync();
        await using var final = database.Factory.CreateDbContext();
        if (reconfigure)
        {
            Assert.NotEqual(original, (await final.VpnSourceImportStates.SingleAsync()).SnapshotId);
            Assert.False(clients.Validators[1]);
        }
        else Assert.Empty(await final.VpnSourceImportStates.ToArrayAsync());
    }

    private static async Task DrainOriginalProfilesAsync(
        SnapshotDatabase database, VpnCatalogService service, FeedClients clients, int expectedRecords)
    {
        VpnSource before;
        await using (var db = database.Factory.CreateDbContext())
        {
            var source = await db.VpnSources.SingleAsync();
            source.NextFetchAt = DateTimeOffset.UtcNow.AddDays(1);
            await db.SaveChangesAsync();
            before = source;
        }
        var requests = clients.Validators.Count;
        for (var run = 0; run < 100; run++)
        {
            int previousIndex;
            await using (var db = database.Factory.CreateDbContext())
            {
                var state = await db.VpnSourceImportStates.SingleAsync();
                Assert.Equal(state.CandidateCount, state.NextIndex);
                Assert.Equal(expectedRecords, state.ProfileRecordCount);
                if (state.ProfileNextIndex == expectedRecords)
                {
                    Assert.Empty(state.Payload);
                    Assert.Empty(state.PayloadHash);
                    Assert.Equal(expectedRecords, await db.VpnConnectionProfiles.CountAsync());
                    var after = await db.VpnSources.SingleAsync();
                    Assert.Equal(before.LastFetchedAt, after.LastFetchedAt);
                    Assert.Equal(before.LastSucceededAt, after.LastSucceededAt);
                    Assert.Equal(before.NextFetchAt, after.NextFetchAt);
                    Assert.Equal(requests, clients.Validators.Count);
                    return;
                }
                Assert.NotEmpty(state.Payload);
                Assert.NotEmpty(state.PayloadHash);
                previousIndex = state.ProfileNextIndex;
            }
            var result = await service.CollectAsync();
            Assert.Equal(0, result.Added);
            Assert.Equal(0, result.Succeeded);
            Assert.Equal(0, result.NotModified);
            await using var progress = database.Factory.CreateDbContext();
            var next = (await progress.VpnSourceImportStates.SingleAsync()).ProfileNextIndex;
            Assert.InRange(next - previousIndex, 1, VpnSnapshotAdmission.MaximumProfileRecordsPerSource);
        }
        Assert.Fail("Original profile cursor did not complete within its bounded expected cycles.");
    }

    private static string Feed(int count, int firstPort = 1, string host = "8.8.8.8") =>
        string.Join('\n', Enumerable.Range(firstPort, count).Select(port => $"vless://original@{host}:{port}?type=tcp"));

    private static CollectorOptions Settings(int quota) => new()
    {
        MaxProxiesPerSource = quota,
        MaxCandidatesPerRun = quota,
        SourceConcurrency = 1,
        SourceRetryCount = 0,
        SourceTimeoutSeconds = 5
    };

    private static VpnCatalogService Service(SnapshotDatabase db, FeedClients clients, CollectorOptions settings) =>
        new(db.Factory, clients, Options.Create(settings), NullLogger<VpnCatalogService>.Instance);

    private static async Task AddSourceAsync(SnapshotDatabase database, string name, int priority = 10)
    {
        await using var db = database.Factory.CreateDbContext();
        db.VpnSources.Add(new VpnSource
        {
            Name = name,
            Provider = "Integration test",
            Url = $"https://8.8.8.8/{name}",
            License = "MIT",
            Priority = priority
        });
        await db.SaveChangesAsync();
    }

    private sealed class FeedClients(Func<string, string> body) : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Dictionary<string, string> versions = [];
        internal readonly List<bool> Validators = [];
        internal bool Fail;
        internal string MediaType = "text/plain";
        internal Func<Task>? BeforeResponse;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Validators.Add(request.Headers.IfNoneMatch.Count > 0);
            if (BeforeResponse is { } beforeResponse)
            {
                BeforeResponse = null;
                await beforeResponse();
            }
            if (Fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var path = request.RequestUri!.AbsolutePath;
            var content = body(path);
            if (!versions.TryGetValue(content, out var version)) versions[content] = version = $"\"v{versions.Count}\"";
            var response = new HttpResponseMessage(request.Headers.IfNoneMatch.Any(tag => tag.Tag == version)
                ? HttpStatusCode.NotModified : HttpStatusCode.OK);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(version);
            if (response.StatusCode == HttpStatusCode.OK) response.Content = new StringContent(content, Encoding.UTF8, MediaType);
            return response;
        }
    }
}
