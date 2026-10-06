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
public sealed class VpnUriObservationIntegrationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 6, 7, 0, 0, TimeSpan.Zero);

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedUriCannotReplaceFreshOrReconfirmedUri(bool reconfirmSameUri)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "current", 20);
        await ImportAsync(database, [Batch(source, "current", Epoch)]);
        var confirmedAt = Epoch.AddHours(2);
        await ImportAsync(database, [Batch(source, reconfirmSameUri ? "current" : "rotated", confirmedAt)]);
        await ImportAsync(database, [Batch(source, "stale", Epoch.AddHours(1))]);
        await using var verify = database.Factory.CreateDbContext();
        var row = await verify.VpnEndpoints.SingleAsync();
        Assert.Equal(Uri(reconfirmSameUri ? "current" : "rotated"), row.ConnectionUri);
        Assert.Equal(confirmedAt, row.ConnectionUriObservedAt);
        Assert.Equal(Epoch, row.FirstSeenAt);
        Assert.Equal(confirmedAt, row.LastSeenAt);
        Assert.Equal(confirmedAt, (await verify.VpnEndpointSources.SingleAsync()).LastSeenAt);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshnessWinsBeforePriorityAndEqualEpochUsesPriority(bool equalEpoch)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var preferred = await AddSourceAsync(database, "preferred", 1);
        var secondary = await AddSourceAsync(database, "secondary", 20);
        var secondaryAt = equalEpoch ? Epoch : Epoch.AddHours(1);
        await ImportAsync(database, [Batch(secondary, "secondary", secondaryAt), Batch(preferred, "preferred", Epoch)]);
        await using var verify = database.Factory.CreateDbContext();
        var row = await verify.VpnEndpoints.SingleAsync();
        Assert.Equal(Uri(equalEpoch ? "preferred" : "secondary"), row.ConnectionUri);
        Assert.Equal(secondaryAt, row.ConnectionUriObservedAt);
        Assert.Equal(Epoch, row.FirstSeenAt);
        Assert.Equal(secondaryAt, row.LastSeenAt);
        Assert.Equal(2, await verify.VpnEndpointSources.CountAsync());
        Assert.Equal(Epoch, (await verify.VpnEndpointSources.SingleAsync(x => x.VpnSourceId == preferred.Id)).LastSeenAt);
        Assert.Equal(secondaryAt, (await verify.VpnEndpointSources.SingleAsync(x => x.VpnSourceId == secondary.Id)).LastSeenAt);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task NewerMetadataAdvancesDiscoveryWithoutErasingOrRedatingUri()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "metadata", 1);
        await ImportAsync(database, [Batch(source, "original", Epoch)]);
        await ImportAsync(database, [Batch(source, null, Epoch.AddHours(2))]);
        await using (var verify = database.Factory.CreateDbContext())
        {
            var row = await verify.VpnEndpoints.SingleAsync();
            Assert.Equal(Uri("original"), row.ConnectionUri);
            Assert.Equal(Epoch, row.ConnectionUriObservedAt);
            Assert.Equal(Epoch.AddHours(2), row.LastSeenAt);
        }
        // URI observed between the old URI and newer metadata is still a genuine update.
        await ImportAsync(database, [Batch(source, "updated", Epoch.AddHours(1))]);
        await using var final = database.Factory.CreateDbContext();
        var updated = await final.VpnEndpoints.SingleAsync();
        Assert.Equal(Uri("updated"), updated.ConnectionUri);
        Assert.Equal(Epoch.AddHours(1), updated.ConnectionUriObservedAt);
        Assert.Equal(Epoch.AddHours(2), updated.LastSeenAt);
        Assert.Equal(Epoch.AddHours(2), (await final.VpnEndpointSources.SingleAsync()).LastSeenAt);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task CombinedBatchKeepsReadyUriAndAccountsForNewerMetadataFromAnotherSource()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var ready = await AddSourceAsync(database, "ready", 20);
        var metadata = await AddSourceAsync(database, "metadata-only", 1);
        await ImportAsync(database, [Batch(metadata, null, Epoch.AddHours(2)), Batch(ready, "ready", Epoch)]);
        await using var verify = database.Factory.CreateDbContext();
        var row = await verify.VpnEndpoints.SingleAsync();
        Assert.Equal(Uri("ready"), row.ConnectionUri);
        Assert.Equal(Epoch, row.ConnectionUriObservedAt);
        Assert.Equal(Epoch, row.FirstSeenAt);
        Assert.Equal(Epoch.AddHours(2), row.LastSeenAt);
        Assert.Equal(ready.Id, row.FirstSourceId);
        Assert.Equal(2, await verify.VpnEndpointSources.CountAsync());
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task LegacyBackupWithoutEpochUsesLastSeenAsConservativeBoundary()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "legacy", 1);
        await ImportAsync(database, [Batch(source, "legacy", Epoch.AddHours(1))]);
        await using (var seed = database.Factory.CreateDbContext())
            await seed.VpnEndpoints.ExecuteUpdateAsync(set => set.SetProperty(x => x.ConnectionUriObservedAt, (DateTimeOffset?)null));
        await ImportAsync(database, [Batch(source, "old-cache", Epoch)]);
        await using (var verify = database.Factory.CreateDbContext())
        {
            var row = await verify.VpnEndpoints.SingleAsync();
            Assert.Equal(Uri("legacy"), row.ConnectionUri);
            Assert.Null(row.ConnectionUriObservedAt);
        }
        await ImportAsync(database, [Batch(source, "fresh", Epoch.AddHours(2))]);
        await using var final = database.Factory.CreateDbContext();
        var updated = await final.VpnEndpoints.SingleAsync();
        Assert.Equal(Uri("fresh"), updated.ConnectionUri);
        Assert.Equal(Epoch.AddHours(2), updated.ConnectionUriObservedAt);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task RollbackDoesNotPublishUriEpochOrProvenanceAndReplayDoesNotRewriteRows()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "rollback", 1);
        await ImportAsync(database, [Batch(source, "original", Epoch)]);
        await using (var db = database.Factory.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await AcquireMutationAsync(db, transaction);
            await VpnCatalogService.BulkUpsertAsync(db, [Batch(source, "rolled-back", Epoch.AddHours(1))], Epoch.AddDays(1), 60, default);
            await transaction.RollbackAsync();
        }
        var before = await ReadVersionsAsync(database);
        await ImportAsync(database, [Batch(source, "original", Epoch)]);
        Assert.Equal(before, await ReadVersionsAsync(database));
        await using var verify = database.Factory.CreateDbContext();
        var row = await verify.VpnEndpoints.SingleAsync();
        Assert.Equal(Uri("original"), row.ConnectionUri);
        Assert.Equal(Epoch, row.ConnectionUriObservedAt);
        Assert.Equal(Epoch, (await verify.VpnEndpointSources.SingleAsync()).LastSeenAt);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task MigrationBackfillsPublishedUrisAndLeavesMetadataEpochNull()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await using var db = database.Factory.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261006075738_AddVpnSourceImportSnapshots");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "VpnEndpoints" ("Id", "Host", "Port", "Protocol", "Transport",
                "ConnectionUri", "Status", "FirstSeenAt", "LastSeenAt", "SuccessfulChecks", "FailedChecks")
            VALUES ({Guid.NewGuid()}, '8.8.8.8', 443, 0, 'tcp', {Uri("existing")}, 0, {Epoch}, {Epoch.AddHours(1)}, 0, 0),
                   ({Guid.NewGuid()}, '8.8.4.4', 443, 0, 'tcp', NULL, 0, {Epoch}, {Epoch.AddHours(1)}, 0, 0)
            """);
        await migrator.MigrateAsync();
        var rows = await db.VpnEndpoints.AsNoTracking().ToArrayAsync();
        Assert.Equal(Epoch.AddHours(1), rows.Single(x => x.ConnectionUri is not null).ConnectionUriObservedAt);
        Assert.Null(rows.Single(x => x.ConnectionUri is null).ConnectionUriObservedAt);
    }

    private static string Uri(string marker) => $"vless://{marker}@8.8.8.8:443?type=tcp";

    private static VpnImportBatch Batch(VpnSource source, string? marker, DateTimeOffset observedAt) =>
        new(source, [new("8.8.8.8", 443, VpnProtocol.Vless, "tcp", marker is null ? null : Uri(marker))], observedAt);

    private static async Task<VpnSource> AddSourceAsync(SnapshotDatabase database, string name, int priority)
    {
        var source = new VpnSource { Name = name, Provider = "Integration test", Url = $"https://8.8.8.8/{name}", Priority = priority, License = "MIT" };
        await using var db = database.Factory.CreateDbContext();
        db.VpnSources.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private static async Task ImportAsync(SnapshotDatabase database, VpnImportBatch[] batches)
    {
        await using var db = database.Factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AcquireMutationAsync(db, transaction);
        await VpnCatalogService.BulkUpsertAsync(db, batches, Epoch.AddDays(1), 60, default);
        await transaction.CommitAsync();
    }

    private static Task AcquireMutationAsync(ProxyHarborDbContext db, IDbContextTransaction transaction) =>
        PostgresAdvisoryLock.AcquireTransactionAsync((NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)transaction.GetDbTransaction(), PostgresAdvisoryLock.VpnMutationKey, default);

    private static async Task<(string Endpoint, string Provenance)> ReadVersionsAsync(SnapshotDatabase database)
    {
        await using var db = database.Factory.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT e.xmin::text, p.xmin::text FROM "VpnEndpoints" e
            JOIN "VpnEndpointSources" p ON p."VpnEndpointId" = e."Id"
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }
}
