using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxySourceImportStoreIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FreshOnlyAcknowledgementRejectsDuplicateAndStaleLane()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("fresh-cas");
        var store = new ProxySourceImportStore(database.Factory);
        var state = Assert.IsType<ProxySourceImportState>(await store.BeginAsync(source,
            ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80\n8.8.8.8:81", ProxyProtocol.Http), CancellationToken.None));
        var checkpoint = ProxySourceImportCheckpoint.Capture(state);
        var hash = ProxyCandidateSnapshotCodec.HashBody("1.1.1.1:99");
        var at = DateTimeOffset.UtcNow;
        Assert.True(await store.AcknowledgeCommittedImportAsync(checkpoint, 0, at,
            CancellationToken.None, hash, preferFresh: false));
        Assert.False(await store.AcknowledgeCommittedImportAsync(checkpoint, 0, at.AddMinutes(1),
            CancellationToken.None, hash, preferFresh: false));
        Assert.False(await store.AcknowledgeCommittedImportAsync(checkpoint, 1, at.AddMinutes(1),
            CancellationToken.None));
        var fresh = Assert.IsType<ProxySourceImportState>(await store.LoadAsync(source, CancellationToken.None));
        Assert.Equal(0, fresh.NextIndex);
        Assert.False(fresh.PreferFresh);
        Assert.Equal(hash, fresh.FreshBodyHash);
        Assert.NotNull(fresh.LastProgressAt);
        Assert.True(await store.AcknowledgeCommittedImportAsync(fresh, 1, at.AddMinutes(2), CancellationToken.None));
        var tail = Assert.IsType<ProxySourceImportState>(await store.LoadAsync(source, CancellationToken.None));
        Assert.True(tail.PreferFresh);
        Assert.Equal(hash, tail.FreshBodyHash);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SnapshotSurvivesRestartAndChangingFeedUntilCommittedTail()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("original");
        var store = new ProxySourceImportStore(database.Factory);
        var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80\n1.1.1.1:443", ProxyProtocol.Http);
        var state = Assert.IsType<ProxySourceImportState>(await store.BeginAsync(source, snapshot, CancellationToken.None));
        Assert.Equal(snapshot.Payload.Length, state.StoredBytes);
        Assert.Equal(new ProxySnapshotWindow(1, 1, false), ProxySourceImportStore.ReadWindow(state, 1, _ => true));

        // Нет подтверждённого bulk commit: новый экземпляр обязан повторить начало.
        store = new ProxySourceImportStore(database.Factory);
        state = Assert.IsType<ProxySourceImportState>(await store.LoadAsync(source, CancellationToken.None));
        Assert.Equal(0, state.NextIndex);
        var changedBody = ProxyCandidateSnapshotCodec.Encode("9.9.9.9:1080", ProxyProtocol.Socks5);
        var retained = Assert.IsType<ProxySourceImportState>(await store.BeginAsync(source, changedBody, CancellationToken.None));
        Assert.Equal(state.SnapshotId, retained.SnapshotId);
        var committedAt = new DateTimeOffset(2026, 10, 6, 5, 0, 0, TimeSpan.Zero);
        Assert.True(await store.AcknowledgeCommittedImportAsync(state, 1, committedAt, CancellationToken.None));
        Assert.False(await store.AcknowledgeCommittedImportAsync(state, 1, committedAt, CancellationToken.None));
        var tail = Assert.IsType<ProxySourceImportState>(await store.LoadAsync(source, CancellationToken.None));
        Assert.Equal(committedAt, tail.LastProgressAt);
        Assert.Equal(snapshot.Payload.Length, tail.StoredBytes);
        ProxyCandidateKey accepted = default;
        Assert.Equal(new ProxySnapshotWindow(1, 2, true), ProxySourceImportStore.ReadWindow(tail, 1, candidate =>
        {
            accepted = candidate;
            return true;
        }));
        Assert.Equal(("1.1.1.1", 443, ProxyProtocol.Http), accepted.ToEndpoint());
        Assert.True(await store.AcknowledgeCommittedImportAsync(tail, 2, committedAt.AddMinutes(5), CancellationToken.None));
        var complete = Assert.IsType<ProxySourceImportState>(await store.LoadAsync(source, CancellationToken.None));
        Assert.Empty(complete.Payload);
        Assert.Empty(complete.PayloadHash);
        Assert.Equal(0, complete.StoredBytes);
        var next = Assert.IsType<ProxySourceImportState>(await store.BeginAsync(source, changedBody, CancellationToken.None));
        Assert.NotEqual(state.SnapshotId, next.SnapshotId);
        Assert.Equal(complete.LastProgressAt, next.LastProgressAt);
        Assert.False(await store.AcknowledgeCommittedImportAsync(tail, 2, committedAt.AddMinutes(10), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ConfigurationChangeCorruptionAndSourceDeletionDoNotLeaveHiddenCursor()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("changing");
        var store = new ProxySourceImportStore(database.Factory);
        var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80", ProxyProtocol.Http);
        var state = Assert.IsType<ProxySourceImportState>(await store.BeginAsync(source, snapshot, CancellationToken.None));
        source.Url = "https://8.8.8.8/replacement.txt";
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.Where(item => item.Id == source.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.Url, source.Url));
        Assert.Null(await store.LoadAsync(source, CancellationToken.None));
        Assert.False(await store.AcknowledgeCommittedImportAsync(state, 1, DateTimeOffset.UtcNow, CancellationToken.None));
        var replacement = Assert.IsType<ProxySourceImportState>(await store.BeginAsync(source, snapshot, CancellationToken.None));
        Assert.NotEqual(state.SnapshotId, replacement.SnapshotId);
        await using (var db = database.Factory.CreateDbContext())
        {
            var corrupt = await db.ProxySourceImportStates.SingleAsync();
            corrupt.Payload[8] ^= 1;
            db.Entry(corrupt).Property(item => item.Payload).IsModified = true;
            await db.SaveChangesAsync();
        }
        Assert.Null(await store.LoadAsync(source, CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
            Assert.Empty(await db.ProxySourceImportStates.ToArrayAsync());
        _ = await store.BeginAsync(source, snapshot, CancellationToken.None);
        await using (var db = database.Factory.CreateDbContext())
        {
            var wrongVersion = await db.ProxySourceImportStates.SingleAsync();
            wrongVersion.Payload[0] ^= 1;
            wrongVersion.PayloadHash = SHA256.HashData(wrongVersion.Payload);
            db.Entry(wrongVersion).Property(item => item.Payload).IsModified = true;
            await db.SaveChangesAsync();
        }
        Assert.Null(await store.LoadAsync(source, CancellationToken.None));
        _ = await store.BeginAsync(source, snapshot, CancellationToken.None);
        await using (var db = database.Factory.CreateDbContext())
        {
            await db.Sources.Where(item => item.Id == source.Id).ExecuteDeleteAsync();
            Assert.Empty(await db.ProxySourceImportStates.ToArrayAsync());
        }
        Assert.Null(await store.BeginAsync(source, snapshot, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ConcurrentAdmissionCannotOversubscribeStorageBudgetAndCompletionFreesSpace()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var sources = new[] { await database.AddSourceAsync("first"), await database.AddSourceAsync("second") };
        var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80", ProxyProtocol.Http);
        var store = new ProxySourceImportStore(database.Factory, snapshot.Payload.Length);
        var results = await Task.WhenAll(sources.Select(async source =>
        {
            try { return await store.BeginAsync(source, snapshot, CancellationToken.None); }
            catch (InvalidDataException exception)
            {
                Assert.Contains("заполнено", exception.Message, StringComparison.Ordinal);
                return null;
            }
        }));
        var admitted = Assert.Single(results.OfType<ProxySourceImportState>());
        await using (var db = database.Factory.CreateDbContext())
            Assert.Equal(snapshot.Payload.Length, await db.ProxySourceImportStates.SumAsync(item => item.StoredBytes));
        Assert.False(await store.AcknowledgeCommittedImportAsync(admitted, 0, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.True(await store.AcknowledgeCommittedImportAsync(admitted, 1, DateTimeOffset.UtcNow, CancellationToken.None));
        var waitingSource = sources.Single(source => source.Id != admitted.ProxySourceId);
        Assert.NotNull(await store.BeginAsync(waitingSource, snapshot, CancellationToken.None));
    }

    internal sealed class SnapshotDatabase(string connectionString, string schema, SnapshotDbFactory factory) : IAsyncDisposable
    {
        internal SnapshotDbFactory Factory => factory;

        internal static async Task<SnapshotDatabase?> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
            if (string.IsNullOrWhiteSpace(connection)) return null;
            var schema = $"proxy_snapshot_{Guid.NewGuid():N}";
            await using var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
                await create.ExecuteNonQueryAsync();
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>().UseNpgsql(
                new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString).Options;
            var database = new SnapshotDatabase(connection, schema, new SnapshotDbFactory(options));
            try
            {
                await using var db = database.Factory.CreateDbContext();
                await db.Database.MigrateAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        internal async Task<ProxySource> AddSourceAsync(string name)
        {
            var source = new ProxySource { Name = name, Url = $"https://8.8.8.8/{name}.txt" };
            await using var db = Factory.CreateDbContext();
            db.Sources.Add(source);
            await db.SaveChangesAsync();
            return source;
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    internal sealed class SnapshotDbFactory(DbContextOptions<ProxyHarborDbContext> options) : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
