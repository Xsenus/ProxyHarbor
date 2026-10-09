using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using SnapshotDatabase = ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests.SnapshotDatabase;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class VpnClashObservationIntegrationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSnapshotCannotReplaceRotatedOrReconfirmedConfiguration(bool reconfirm)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await Source(database, "current", 1);
        await Import(database, [Batch(source, "original", Epoch)]);
        var latest = reconfirm ? "original" : "rotated";
        await Import(database, [Batch(source, latest, Epoch.AddHours(2))]);
        await Import(database, [Batch(source, "old", Epoch.AddHours(1))]);
        await using var db = database.Factory.CreateDbContext();
        var row = await db.VpnEndpoints.SingleAsync();
        Assert.Equal(Configuration(latest), row.ClashConfiguration);
        Assert.Equal(Epoch.AddHours(2), row.ClashConfigurationObservedAt);
        Assert.Equal(Epoch, row.FirstSeenAt);
        Assert.Equal(Epoch.AddHours(2), row.LastSeenAt);
        Assert.Equal(Epoch.AddHours(2), (await db.VpnEndpointSources.SingleAsync()).LastSeenAt);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservationTimeWinsBeforeSourcePriority(bool equalEpoch)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var preferred = await Source(database, "preferred", 1);
        var secondary = await Source(database, "secondary", 20);
        var observed = equalEpoch ? Epoch : Epoch.AddHours(1);
        await Import(database, [Batch(secondary, "secondary", observed), Batch(preferred, "preferred", Epoch)]);
        await using var db = database.Factory.CreateDbContext();
        var row = await db.VpnEndpoints.SingleAsync();
        Assert.Equal(Configuration(equalEpoch ? "preferred" : "secondary"), row.ClashConfiguration);
        Assert.Equal(observed, row.ClashConfigurationObservedAt);
        Assert.Equal(2, await db.VpnEndpointSources.CountAsync());
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task CombinedBatchChoosesUriAndClashIndependentlyAndRetainsTheirEpochs()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var yaml = await Source(database, "yaml", 20);
        var uri = await Source(database, "uri", 10);
        var metadata = await Source(database, "metadata", 1);
        const string ready = "vless://ready@8.8.8.8:443";
        var uriBatch = new VpnImportBatch(uri, [new("8.8.8.8", 443, VpnProtocol.Vless, "tcp", ready)], Epoch.AddHours(1));
        var metadataBatch = new VpnImportBatch(metadata, [new("8.8.8.8", 443, VpnProtocol.Vless, "tcp")], Epoch.AddHours(2));
        await Import(database, [Batch(yaml, "yaml", Epoch), uriBatch, metadataBatch]);
        await using (var db = database.Factory.CreateDbContext())
        {
            var row = await db.VpnEndpoints.SingleAsync();
            Assert.Equal(ready, row.ConnectionUri);
            Assert.Equal(Epoch.AddHours(1), row.ConnectionUriObservedAt);
            Assert.Equal(Configuration("yaml"), row.ClashConfiguration);
            Assert.Equal(Epoch, row.ClashConfigurationObservedAt);
            Assert.Equal(Epoch.AddHours(2), row.LastSeenAt);
            Assert.Equal(3, await db.VpnEndpointSources.CountAsync());
        }
        await Import(database, [Batch(yaml, "new-yaml", Epoch.AddMinutes(30))]);
        await using var final = database.Factory.CreateDbContext();
        var updated = await final.VpnEndpoints.SingleAsync();
        Assert.Equal(Configuration("new-yaml"), updated.ClashConfiguration);
        Assert.Equal(Epoch.AddMinutes(30), updated.ClashConfigurationObservedAt);
        Assert.Equal(ready, updated.ConnectionUri);
        Assert.Equal(Epoch.AddHours(1), updated.ConnectionUriObservedAt);
        Assert.Equal(Epoch.AddHours(2), updated.LastSeenAt);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task RollbackDoesNotPublishSettingsAndExactReplayDoesNotRewriteRows()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await Source(database, "rollback", 1);
        await Import(database, [Batch(source, "original", Epoch)]);
        var before = await Versions(database);
        await using (var db = database.Factory.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Lock(db, transaction);
            await VpnCatalogService.BulkUpsertAsync(db, [Batch(source, "rollback", Epoch.AddHours(1))], Epoch.AddDays(1), 60, default);
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await Versions(database));
        await Import(database, [Batch(source, "original", Epoch)]);
        Assert.Equal(before, await Versions(database));
        await using var verify = database.Factory.CreateDbContext();
        var row = await verify.VpnEndpoints.SingleAsync();
        Assert.Equal(Configuration("original"), row.ClashConfiguration);
        Assert.Equal(Epoch, row.ClashConfigurationObservedAt);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData("anytls", VpnProtocol.AnyTls)]
    [InlineData("hysteria", VpnProtocol.Hysteria)]
    [InlineData("ssr", VpnProtocol.ShadowsocksR)]
    [InlineData("http", VpnProtocol.HttpProxy)]
    [InlineData("socks4", VpnProtocol.Socks4Proxy)]
    [InlineData("socks5", VpnProtocol.Socks5Proxy)]
    public async Task NewProtocolsPersistThroughSnapshotAndCatalog(string type, VpnProtocol protocol)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await Source(database, type, 1, protocol);
        var snapshot = VpnCandidateSnapshotCodec.Encode($"proxies: [{{name: node, type: {type}, server: 8.8.8.8, port: 443, password: published}}]", protocol);
        var candidates = new List<VpnCandidate>();
        Assert.True(VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, candidate => { candidates.Add(candidate); return true; }).Completed);
        var store = new VpnSourceImportStore(database.Factory);
        var state = Assert.IsType<VpnSourceImportState>(await store.BeginAsync(source, snapshot, Epoch, default));
        Assert.True(VpnSourceImportStore.ReadWindow(state, 1, _ => true).Completed);
        await Import(database, [new(source, candidates, Epoch)]);
        await using var db = database.Factory.CreateDbContext();
        var row = await db.VpnEndpoints.SingleAsync();
        Assert.Equal(protocol, row.Protocol);
        Assert.Equal(candidates[0].ClashConfiguration, row.ClashConfiguration);
        Assert.Equal(Epoch, row.ClashConfigurationObservedAt);
        Assert.Null(row.ConnectionUri);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task UpgradeRetainsExistingUriAndLeavesClashFieldsNull()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await using var db = database.Factory.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261006193647_AddTlsProxyTransports");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "VpnEndpoints" ("Id", "Host", "Port", "Protocol", "Transport", "ConnectionUri", "ConnectionUriObservedAt",
                "Status", "FirstSeenAt", "LastSeenAt", "SuccessfulChecks", "FailedChecks", "LastValidationDeferred")
            VALUES ({Guid.NewGuid()}, '8.8.8.8', 443, 2, 'tcp', 'vless://legacy@8.8.8.8:443', {Epoch}, 0, {Epoch}, {Epoch}, 0, 0, false)
            """);
        await migrator.MigrateAsync();
        var row = await db.VpnEndpoints.SingleAsync();
        Assert.Equal("vless://legacy@8.8.8.8:443", row.ConnectionUri);
        Assert.Equal(Epoch, row.ConnectionUriObservedAt);
        Assert.Null(row.ClashConfiguration);
        Assert.Null(row.ClashConfigurationObservedAt);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DowngradeCannotRemoveSavedConfigurationOrItsObservationTime(bool observationOnly)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await Source(database, "protected-clash", 1);
        await Import(database, [Batch(source, "protected", Epoch)]);
        await using (var db = database.Factory.CreateDbContext())
        {
            if (observationOnly)
            {
                var row = await db.VpnEndpoints.SingleAsync();
                row.ClashConfiguration = null;
                await db.SaveChangesAsync();
            }
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>()
                .MigrateAsync("20261006193647_AddTlsProxyTransports"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        await using var verify = database.Factory.CreateDbContext();
        var retained = await verify.VpnEndpoints.SingleAsync();
        Assert.Equal(observationOnly ? null : Configuration("protected"), retained.ClashConfiguration);
        Assert.Equal(Epoch, retained.ClashConfigurationObservedAt);
        // Completed newer rollback steps do not roll back with the rejected protected step.
        var migrations = verify.Database.GetMigrations().ToArray();
        var protectedIndex = Array.IndexOf(migrations, "20261007052123_AddClashConfigurations");
        Assert.True(protectedIndex >= 0);
        Assert.Equal(migrations[(protectedIndex + 1)..], await verify.Database.GetPendingMigrationsAsync());
        await verify.Database.MigrateAsync();
        var recovered = await verify.VpnEndpoints.AsNoTracking().SingleAsync();
        Assert.Equal(observationOnly ? null : Configuration("protected"), recovered.ClashConfiguration);
        Assert.Equal(Epoch, recovered.ClashConfigurationObservedAt);
        Assert.Empty(await verify.Database.GetPendingMigrationsAsync());
    }

    private static string Configuration(string marker) => VpnFeedParser.Parse(
        $"proxies: [{{name: node, type: vless, server: 8.8.8.8, port: 443, uuid: {marker}, tls: true, reality-opts: {{public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA}}}}]", VpnProtocol.Vless)[0].ClashConfiguration!;

    private static VpnImportBatch Batch(VpnSource source, string marker, DateTimeOffset observed) =>
        new(source, [new VpnCandidate("8.8.8.8", 443, VpnProtocol.Vless, "tcp") { ClashConfiguration = Configuration(marker) }], observed);

    private static async Task<VpnSource> Source(SnapshotDatabase database, string name, int priority, VpnProtocol protocol = VpnProtocol.Vless)
    {
        var source = new VpnSource { Name = name, Provider = "Integration test", License = "MIT", Url = $"https://example.com/{name}", Priority = priority, DefaultProtocol = protocol };
        await using var db = database.Factory.CreateDbContext();
        db.VpnSources.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private static async Task Import(SnapshotDatabase database, VpnImportBatch[] batches)
    {
        await using var db = database.Factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Lock(db, transaction);
        await VpnCatalogService.BulkUpsertAsync(db, batches, Epoch.AddDays(1), 60, default);
        await transaction.CommitAsync();
    }

    private static Task Lock(ProxyHarborDbContext db, IDbContextTransaction transaction) =>
        PostgresAdvisoryLock.AcquireTransactionAsync((NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)transaction.GetDbTransaction(), PostgresAdvisoryLock.VpnMutationKey, default);

    private static async Task<(string Endpoint, string Provenance)> Versions(SnapshotDatabase database)
    {
        await using var db = database.Factory.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT e.xmin::text, p.xmin::text FROM \"VpnEndpoints\" e JOIN \"VpnEndpointSources\" p ON p.\"VpnEndpointId\" = e.\"Id\"", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }
}
