using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using SnapshotDatabase = ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests.SnapshotDatabase;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class VpnSourceImportStoreIntegrationTests
{
    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData("count", "CK_VpnSourceImportStates_Cursor")]
    [InlineData("cursor", "CK_VpnSourceImportStates_Cursor")]
    [InlineData("protocol", "CK_VpnSourceImportStates_Cursor")]
    [InlineData("empty", "CK_VpnSourceImportStates_Payload")]
    [InlineData("complete", "CK_VpnSourceImportStates_Payload")]
    [InlineData("hash", "CK_VpnSourceImportStates_Payload")]
    [InlineData("original", "CK_VpnSourceImportStates_Payload")]
    [InlineData("fresh", "CK_VpnSourceImportStates_Payload")]
    [InlineData("oversize", "CK_VpnSourceImportStates_Payload")]
    public async Task DatabaseRejectsInvalidCursorHashOrPayloadEvenOutsideStore(string damage, string constraint)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "invalid");
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        var state = new VpnSourceImportState
        {
            VpnSourceId = source.Id,
            SourceUrl = source.Url,
            SourceProtocol = source.DefaultProtocol,
            CreatedAt = DateTimeOffset.UtcNow,
            CandidateCount = 1,
            Payload = snapshot.Payload,
            PayloadHash = SHA256.HashData(snapshot.Payload),
            SnapshotBodyHash = snapshot.BodyHash,
            FreshBodyHash = snapshot.BodyHash
        };
        switch (damage)
        {
            case "count": state.CandidateCount = 0; break;
            case "cursor": state.NextIndex = 2; break;
            case "protocol": state.SourceProtocol = (VpnProtocol)15; break;
            case "empty": state.Payload = []; break;
            case "complete": state.NextIndex = 1; break;
            case "hash": state.PayloadHash = []; break;
            case "original": state.SnapshotBodyHash = []; break;
            case "fresh": state.FreshBodyHash = []; break;
            case "oversize": state.Payload = new byte[VpnCandidateSnapshotCodec.MaxPayloadBytes + 1]; break;
        }
        await using var db = database.Factory.CreateDbContext();
        db.VpnSourceImportStates.Add(state);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task CanonicalTailSurvivesRestartAndChangedBodyUntilCommittedCompletion()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "tail");
        var store = new VpnSourceImportStore(database.Factory);
        var observedAt = new DateTimeOffset(2026, 10, 6, 7, 0, 0, TimeSpan.Zero);
        var snapshot = VpnCandidateSnapshotCodec.Encode(
            "vless://old@example.com:443\nvless://id@8.8.8.8:80\nvless://latest@example.com:443", VpnProtocol.Vless);
        var state = Assert.IsType<VpnSourceImportState>(await store.BeginAsync(source, snapshot, observedAt, default));
        Assert.Equal(2, state.CandidateCount);
        Assert.Equal(snapshot.Payload.Length, state.StoredBytes);
        var received = new List<VpnCandidate>();
        Assert.Equal(new VpnSnapshotWindow(1, 1, false), VpnSourceImportStore.ReadWindow(state, 1, item => { received.Add(item); return true; }));
        Assert.Equal("vless://latest@example.com:443", received[0].ConnectionUri);
        state = Assert.IsType<VpnSourceImportState>(await new VpnSourceImportStore(database.Factory).LoadAsync(source, default));
        Assert.Equal(0, state.NextIndex);
        var changed = VpnCandidateSnapshotCodec.Encode("vless://fresh@1.1.1.1:443", VpnProtocol.Vless);
        var retained = Assert.IsType<VpnSourceImportState>(await store.BeginAsync(source, changed, DateTimeOffset.UtcNow, default));
        Assert.Equal(state.SnapshotId, retained.SnapshotId);
        Assert.Equal(observedAt, retained.CreatedAt);
        Assert.True(await CommitAckAsync(database, state, 1));
        Assert.False(await CommitAckAsync(database, state, 1));
        var tail = Assert.IsType<VpnSourceImportState>(await store.LoadAsync(source, default));
        Assert.Equal(1, tail.NextIndex);
        Assert.True(VpnSourceImportStore.ReadWindow(tail, 1, item => { received.Add(item); return true; }).Completed);
        Assert.Equal("8.8.8.8", received[1].Host);
        Assert.True(await CommitAckAsync(database, tail, 2));
        var complete = Assert.IsType<VpnSourceImportState>(await store.LoadAsync(source, default));
        Assert.Empty(complete.Payload);
        Assert.Empty(complete.PayloadHash);
        Assert.Equal(0, complete.StoredBytes);
        Assert.True(VpnSourceImportStore.ReadWindow(complete, 1, _ => throw new InvalidOperationException()).Completed);
        var next = Assert.IsType<VpnSourceImportState>(await store.BeginAsync(source, changed, observedAt.AddMinutes(1), default));
        Assert.NotEqual(state.SnapshotId, next.SnapshotId);
        Assert.Equal(complete.LastProgressAt, next.LastProgressAt);
        Assert.False(await CommitAckAsync(database, tail, 2));
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task CursorHealthAndEndpointRollbackTogetherAndReplayCanCommit()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "atomic");
        var store = new VpnSourceImportStore(database.Factory);
        var state = Assert.IsType<VpnSourceImportState>(await store.BeginAsync(source,
            VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless), DateTimeOffset.UtcNow, default));
        foreach (var commit in new[] { false, true })
        {
            await using var db = database.Factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var tracked = await db.VpnSources.SingleAsync();
            tracked.LastFetchedAt = state.CreatedAt;
            db.VpnEndpoints.Add(new VpnEndpoint
            {
                Host = "8.8.8.8",
                Port = 443,
                Protocol = VpnProtocol.Vless,
                Transport = "tcp",
                ConnectionUri = "vless://id@8.8.8.8:443",
                FirstSeenAt = state.CreatedAt,
                LastSeenAt = state.CreatedAt
            });
            await db.SaveChangesAsync();
            Assert.True(await VpnSourceImportStore.AcknowledgeAsync(db, VpnSourceImportCheckpoint.Capture(state), 1, DateTimeOffset.UtcNow, default));
            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
            await using var verify = database.Factory.CreateDbContext();
            Assert.Equal(commit ? 1 : 0, await verify.VpnEndpoints.CountAsync());
            Assert.Equal(commit, (await verify.VpnSources.SingleAsync()).LastFetchedAt is not null);
            Assert.Equal(commit ? 1 : 0, (await verify.VpnSourceImportStates.SingleAsync()).NextIndex);
        }
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task FreshOnlyCheckpointUsesLaneCasAndKeepsOriginalObservation()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "fresh");
        var store = new VpnSourceImportStore(database.Factory);
        var observedAt = new DateTimeOffset(2026, 10, 6, 6, 0, 0, TimeSpan.Zero);
        var state = Assert.IsType<VpnSourceImportState>(await store.BeginAsync(source,
            VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless), observedAt, default));
        var hash = ProxyCandidateSnapshotCodec.HashBody("fresh-body");
        Assert.True(await CommitAckAsync(database, state, 0, hash, false));
        Assert.False(await CommitAckAsync(database, state, 0, hash, false));
        Assert.False(await CommitAckAsync(database, state, 1));
        var loaded = Assert.IsType<VpnSourceImportState>(await store.LoadAsync(source, default));
        Assert.Equal(0, loaded.NextIndex);
        Assert.False(loaded.PreferFresh);
        Assert.Equal(hash, loaded.FreshBodyHash);
        Assert.Equal(state.SnapshotBodyHash, loaded.SnapshotBodyHash);
        Assert.Equal(observedAt, loaded.CreatedAt);
        Assert.True(await CommitAckAsync(database, loaded, 1));
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task ConcurrentSnapshotsRespectBudgetAndCompletionReleasesSpace()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var sources = new[] { await AddSourceAsync(database, "one"), await AddSourceAsync(database, "two") };
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        var store = new VpnSourceImportStore(database.Factory, snapshot.Payload.Length);
        var attempts = await Task.WhenAll(sources.Select(async source =>
        {
            try { return await store.BeginAsync(source, snapshot, DateTimeOffset.UtcNow, default); }
            catch (InvalidDataException error) { Assert.Contains("заполнено", error.Message); return null; }
        }));
        var admitted = Assert.Single(attempts.OfType<VpnSourceImportState>());
        await using (var db = database.Factory.CreateDbContext())
            Assert.Equal(snapshot.Payload.Length, await db.VpnSourceImportStates.SumAsync(item => item.StoredBytes));
        Assert.True(await CommitAckAsync(database, admitted, 1));
        Assert.NotNull(await store.BeginAsync(sources.Single(item => item.Id != admitted.VpnSourceId), snapshot, DateTimeOffset.UtcNow, default));
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task ChangedDisabledOrDeletedSourceCannotAcknowledgeOldConfiguration()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "config");
        var store = new VpnSourceImportStore(database.Factory);
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        var state = Assert.IsType<VpnSourceImportState>(await store.BeginAsync(source, snapshot, DateTimeOffset.UtcNow, default));
        await using (var db = database.Factory.CreateDbContext())
            await db.VpnSources.Where(item => item.Id == source.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Enabled, false));
        Assert.False(await CommitAckAsync(database, state, 1));
        Assert.Null(await store.BeginAsync(source, snapshot, DateTimeOffset.UtcNow, default));
        source.Url += "?changed=true";
        await using (var db = database.Factory.CreateDbContext())
            await db.VpnSources.Where(item => item.Id == source.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Enabled, true).SetProperty(item => item.Url, source.Url));
        Assert.Null(await store.LoadAsync(source, default));
        var replacement = Assert.IsType<VpnSourceImportState>(await store.BeginAsync(source, snapshot, DateTimeOffset.UtcNow, default));
        Assert.NotEqual(state.SnapshotId, replacement.SnapshotId);
        Assert.False(await CommitAckAsync(database, state, 1));
        await using (var db = database.Factory.CreateDbContext())
        {
            await db.VpnSources.ExecuteDeleteAsync();
            Assert.Empty(await db.VpnSourceImportStates.ToArrayAsync());
        }
        Assert.False(await CommitAckAsync(database, replacement, 1));
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task CorruptionIsDiscardedAndAckRequiresTransactionAndValidBounds()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddSourceAsync(database, "corrupt");
        var store = new VpnSourceImportStore(database.Factory);
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        var state = Assert.IsType<VpnSourceImportState>(await store.BeginAsync(source, snapshot, DateTimeOffset.UtcNow, default));
        await using (var db = database.Factory.CreateDbContext())
        {
            var checkpoint = VpnSourceImportCheckpoint.Capture(state);
            await Assert.ThrowsAsync<InvalidOperationException>(() => VpnSourceImportStore.AcknowledgeAsync(db, checkpoint, 1, DateTimeOffset.UtcNow, default));
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => VpnSourceImportStore.AcknowledgeAsync(db, checkpoint, 2, DateTimeOffset.UtcNow, default));
            await Assert.ThrowsAsync<ArgumentException>(() => VpnSourceImportStore.AcknowledgeAsync(db, checkpoint, 0, DateTimeOffset.UtcNow, default, [1], false));
            var corrupt = await db.VpnSourceImportStates.SingleAsync();
            corrupt.Payload[0] = 0;
            corrupt.PayloadHash = SHA256.HashData(corrupt.Payload);
            db.Entry(corrupt).Property(item => item.Payload).IsModified = true;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        Assert.Null(await store.LoadAsync(source, default));
        await using (var db = database.Factory.CreateDbContext()) Assert.Empty(await db.VpnSourceImportStates.ToArrayAsync());
        Assert.NotNull(await store.BeginAsync(source, snapshot, DateTimeOffset.UtcNow, default));
    }

    private static async Task<VpnSource> AddSourceAsync(SnapshotDatabase database, string name)
    {
        var source = new VpnSource { Name = name, Provider = "test", License = "test", Url = $"https://example.com/{name}", DefaultProtocol = VpnProtocol.Vless };
        await using var db = database.Factory.CreateDbContext();
        db.VpnSources.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private static async Task<bool> CommitAckAsync(SnapshotDatabase database, VpnSourceImportState state,
        int nextIndex, byte[]? hash = null, bool? preferFresh = null)
    {
        await using var db = database.Factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var changed = await VpnSourceImportStore.AcknowledgeAsync(db, VpnSourceImportCheckpoint.Capture(state), nextIndex,
            DateTimeOffset.UtcNow, default, hash, preferFresh);
        await transaction.CommitAsync();
        return changed;
    }
}
