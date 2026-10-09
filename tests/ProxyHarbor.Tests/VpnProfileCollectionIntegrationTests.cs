using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using SnapshotDatabase = ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests.SnapshotDatabase;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class VpnProfileCollectionIntegrationTests
{
    private static readonly string[] LegacyUris = ["vless://first@8.8.8.8:443", "vless://second@8.8.8.8:443", "vless://third@1.1.1.1:443"];
    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task NormalCollectionDrainsEveryProfileWithoutRefetchingOrChangingPreferredEndpoint()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new VpnSource
        {
            Name = "Original profiles",
            Provider = "Fixture",
            License = "MIT",
            Url = "https://8.8.8.8/profiles.txt",
            DefaultProtocol = VpnProtocol.Vless
        };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.VpnSources.Add(source);
            await db.SaveChangesAsync();
        }
        using var clients = new FeedClients();
        var service = new VpnCatalogService(database.Factory, clients, Options.Create(new CollectorOptions
        {
            MaxProxiesPerSource = 1,
            SourceConcurrency = 1,
            SourceRetryCount = 0,
            SourceTimeoutSeconds = 5
        }), NullLogger<VpnCatalogService>.Instance);
        await service.CollectAsync();
        DateTimeOffset observed;
        await using (var db = database.Factory.CreateDbContext())
        {
            var state = await db.VpnSourceImportStates.SingleAsync();
            observed = state.CreatedAt;
            Assert.Equal(1, state.NextIndex);
            Assert.Equal(1, state.ProfileNextIndex);
            Assert.Equal(3, state.ProfileRecordCount);
            Assert.NotEmpty(state.Payload);
            Assert.Equal(2, await db.VpnConnectionProfiles.CountAsync()); // Preferred endpoint plus first original settings.
            Assert.Equal(FeedClients.Last, (await db.VpnEndpoints.SingleAsync()).ConnectionUri);
            var downgrade = await Assert.ThrowsAsync<PostgresException>(() =>
                db.GetService<IMigrator>().MigrateAsync("20261008191125_AddProxySourceParserVersion"));
            Assert.Contains("Finish pending VPN profile imports", downgrade.MessageText, StringComparison.Ordinal);
            Assert.Equal(2, await db.VpnConnectionProfiles.CountAsync());
        }
        await using (var db = database.Factory.CreateDbContext())
        {
            var coolingSource = await db.VpnSources.SingleAsync();
            coolingSource.NextFetchAt = DateTimeOffset.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }
        await service.CollectAsync();
        await service.CollectAsync();
        await using (var db = database.Factory.CreateDbContext())
        {
            var state = await db.VpnSourceImportStates.SingleAsync();
            Assert.Equal(3, state.ProfileNextIndex);
            Assert.Empty(state.Payload);
            var profiles = await db.VpnConnectionProfiles.ToArrayAsync();
            Assert.Equal(3, profiles.Length);
            Assert.All(profiles, profile => Assert.Equal(observed, profile.FirstSeenAt));
            Assert.All(profiles, VpnConnectionProfileIntegrity.Validate);
            Assert.Equal(FeedClients.Last, (await db.VpnEndpoints.SingleAsync()).ConnectionUri);
        }
        Assert.Equal(1, clients.Requests);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task MigrationKeepsExistingEndpointCursorAndRecoversAllOriginalProfileRecords()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new VpnSource { Name = "Legacy snapshot", Provider = "Fixture", License = "MIT", Url = "https://8.8.8.8/legacy.txt" };
        var snapshot = VpnCandidateSnapshotCodec.Encode(
            "vless://first@8.8.8.8:443\nvless://second@8.8.8.8:443\nvless://third@1.1.1.1:443", VpnProtocol.Vless);
        var snapshotId = Guid.NewGuid();
        await using (var db = database.Factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261008191125_AddProxySourceParserVersion");
            db.VpnSources.Add(source);
            await db.SaveChangesAsync();
            var hash = System.Security.Cryptography.SHA256.HashData(snapshot.Payload);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "VpnSourceImportStates" ("VpnSourceId", "SourceUrl", "SourceProtocol", "SnapshotId", "CreatedAt",
                    "CandidateCount", "NextIndex", "Payload", "PayloadHash", "SnapshotBodyHash", "FreshBodyHash", "PreferFresh")
                VALUES ({source.Id}, {source.Url}, {(int)source.DefaultProtocol}, {snapshotId}, {now},
                    {snapshot.UniqueCount}, 1, {snapshot.Payload}, {hash}, {snapshot.BodyHash}, {snapshot.BodyHash}, true)
                """);
            var cooldown = now.AddHours(1);
            var completedSource = new VpnSource
            {
                Name = "Completed legacy",
                Provider = "Fixture",
                License = "MIT",
                Url = "https://8.8.4.4/legacy.txt",
                HttpETag = "\"old-body\"",
                HttpLastModifiedAt = now.AddHours(-1),
                NextFetchAt = cooldown
            };
            db.VpnSources.Add(completedSource);
            await db.SaveChangesAsync();
            var empty = Array.Empty<byte>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "VpnSourceImportStates" ("VpnSourceId", "SourceUrl", "SourceProtocol", "SnapshotId", "CreatedAt",
                    "CandidateCount", "NextIndex", "Payload", "PayloadHash", "SnapshotBodyHash", "FreshBodyHash", "PreferFresh")
                VALUES ({completedSource.Id}, {completedSource.Url}, {(int)completedSource.DefaultProtocol}, {Guid.NewGuid()}, {now},
                    1, 1, {empty}, {empty}, {snapshot.BodyHash}, {snapshot.BodyHash}, true)
                """);
            await db.Database.MigrateAsync();
            var completed = await db.VpnSources.AsNoTracking().SingleAsync(item => item.Id == completedSource.Id);
            Assert.Null(completed.HttpETag);
            Assert.Null(completed.HttpLastModifiedAt);
            Assert.Equal(cooldown, completed.NextFetchAt);
        }
        var state = Assert.IsType<VpnSourceImportState>(await new VpnSourceImportStore(database.Factory).LoadAsync(source, default));
        Assert.Equal(snapshotId, state.SnapshotId);
        Assert.Equal(1, state.NextIndex);
        Assert.Equal(0, state.ProfileNextIndex);
        Assert.Equal(3, state.ProfileRecordCount);
        var profiles = new List<VpnCandidate>();
        Assert.True(VpnSourceImportStore.ReadProfilesWindow(state, 3, item => { profiles.Add(item); return true; }).Completed);
        Assert.Equal(LegacyUris,
            profiles.Select(profile => profile.ConnectionUri));
    }

    private sealed class FeedClients : HttpMessageHandler, IHttpClientFactory
    {
        internal const string Last = "vless://third@8.8.8.8:443?security=tls&sni=cdn.example.com#three";
        internal int Requests;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("vless://first@8.8.8.8:443#one\nvless://second@8.8.8.8:443#two\n" + Last)
            });
        }
    }
}
