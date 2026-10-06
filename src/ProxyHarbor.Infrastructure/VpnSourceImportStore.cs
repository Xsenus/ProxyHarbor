using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Сохраняет полный snapshot до admission; CAS cursor подтверждается в общей import transaction.</summary>
internal sealed class VpnSourceImportStore(
    IDbContextFactory<ProxyHarborDbContext> dbFactory, long maxStoredBytes = VpnSourceImportStore.MaxStoredBytes)
{
    internal const long MaxStoredBytes = 512L * 1024 * 1024;

    internal async Task CleanupAsync(CancellationToken token)
    {
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await PostgresAdvisoryLock.AcquireTransactionAsync((NpgsqlConnection)db.Database.GetDbConnection(),
                (NpgsqlTransaction)transaction.GetDbTransaction(), PostgresAdvisoryLock.VpnMutationKey, token);
            await db.VpnSourceImportStates.Where(state => !db.VpnSources.Any(source => source.Id == state.VpnSourceId &&
                source.Enabled && source.Url == state.SourceUrl && source.DefaultProtocol == state.SourceProtocol))
                .ExecuteDeleteAsync(token);
            await transaction.CommitAsync(token);
        });
    }

    internal async Task DiscardAsync(VpnSourceImportCheckpoint state, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await db.VpnSourceImportStates.Where(item => item.VpnSourceId == state.VpnSourceId &&
            item.SnapshotId == state.SnapshotId && item.NextIndex == state.NextIndex && item.PreferFresh == state.PreferFresh)
            .ExecuteDeleteAsync(token);
    }

    internal async Task<VpnSourceImportState?> LoadAsync(VpnSource source, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var state = await db.VpnSourceImportStates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.VpnSourceId == source.Id, token);
        if (state is null) return null;
        if (!source.Enabled || !MatchesSource(state, source)) return null;
        if (ValidState(state)) return state;
        await db.VpnSourceImportStates.Where(item => item.VpnSourceId == state.VpnSourceId &&
            item.SnapshotId == state.SnapshotId && item.NextIndex == state.NextIndex).ExecuteDeleteAsync(token);
        return null;
    }

    internal async Task<VpnSourceImportState?> BeginAsync(
        VpnSource source, VpnCandidateSnapshot snapshot, DateTimeOffset observedAt, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStoredBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxStoredBytes, MaxStoredBytes);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            () => BeginAttemptAsync(source, snapshot, observedAt, token));
    }

    private async Task<VpnSourceImportState?> BeginAttemptAsync(
        VpnSource source, VpnCandidateSnapshot snapshot, DateTimeOffset observedAt, CancellationToken token)
    {
        var state = new VpnSourceImportState
        {
            VpnSourceId = source.Id,
            SourceUrl = source.Url,
            SourceProtocol = source.DefaultProtocol,
            CreatedAt = observedAt,
            CandidateCount = snapshot.UniqueCount,
            Payload = snapshot.Payload,
            PayloadHash = SHA256.HashData(snapshot.Payload),
            SnapshotBodyHash = snapshot.BodyHash,
            FreshBodyHash = snapshot.BodyHash
        };
        if (!ValidState(state)) throw new InvalidDataException("Некорректный VPN-снимок.");
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await PostgresAdvisoryLock.AcquireTransactionAsync((NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)transaction.GetDbTransaction(), PostgresAdvisoryLock.VpnMutationKey, token);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"VpnSourceImportStates\" IN SHARE ROW EXCLUSIVE MODE", token);
        if (!await db.VpnSources.AnyAsync(item => item.Id == source.Id && item.Enabled &&
            item.Url == source.Url && item.DefaultProtocol == source.DefaultProtocol, token)) return null;
        var existing = await db.VpnSourceImportStates.SingleOrDefaultAsync(item => item.VpnSourceId == source.Id, token);
        if (existing is not null && MatchesSource(existing, source) && existing.NextIndex < existing.CandidateCount && ValidState(existing))
            return existing;
        var stored = await db.VpnSourceImportStates.SumAsync(item => (long)item.StoredBytes, token);
        if (stored - (existing?.StoredBytes ?? 0) + state.Payload.Length > maxStoredBytes)
            throw new InvalidDataException("Хранилище незавершённых VPN-снимков заполнено; импорт будет повторён после освобождения места.");
        if (existing is null) db.VpnSourceImportStates.Add(state);
        else
        {
            if (MatchesSource(existing, source)) state.LastProgressAt = existing.LastProgressAt;
            db.Entry(existing).CurrentValues.SetValues(state);
            state = existing;
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return state;
    }

    internal static VpnSnapshotWindow ReadWindow(VpnSourceImportState state, int maximum, Func<VpnCandidate, bool> accept)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        ArgumentNullException.ThrowIfNull(accept);
        if (!ValidState(state)) throw new InvalidDataException("Сохранённый VPN-снимок повреждён.");
        if (state.NextIndex == state.CandidateCount) return new(0, state.NextIndex, true);
        return VpnCandidateSnapshotCodec.ReadWindow(state.Payload, state.NextIndex, maximum, accept);
    }

    /// <summary>Успешный UPDATE не является commit: вызывающий сохраняет endpoint и CAS атомарно.</summary>
    internal static async Task<bool> AcknowledgeAsync(
        ProxyHarborDbContext db, VpnSourceImportCheckpoint state, int nextIndex,
        DateTimeOffset committedAt, CancellationToken token, byte[]? freshBodyHash = null, bool? preferFresh = null)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("VPN cursor требует import transaction.");
        ArgumentOutOfRangeException.ThrowIfLessThan(nextIndex, state.NextIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nextIndex, state.CandidateCount);
        if (freshBodyHash is not null && freshBodyHash.Length != SHA256.HashSizeInBytes)
            throw new ArgumentException("Некорректный hash свежего VPN body.", nameof(freshBodyHash));
        if (nextIndex == state.NextIndex && (preferFresh is null || preferFresh == state.PreferFresh)) return false;
        await PostgresAdvisoryLock.AcquireTransactionAsync((NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction(), PostgresAdvisoryLock.VpnMutationKey, token);
        var completed = nextIndex == state.CandidateCount;
        var updated = await db.VpnSourceImportStates.Where(item => item.VpnSourceId == state.VpnSourceId &&
            item.SnapshotId == state.SnapshotId && item.NextIndex == state.NextIndex &&
            item.CandidateCount == state.CandidateCount && item.PreferFresh == state.PreferFresh &&
            item.SourceUrl == state.SourceUrl && item.SourceProtocol == state.SourceProtocol &&
            db.VpnSources.Any(source => source.Id == item.VpnSourceId && source.Enabled &&
                source.Url == item.SourceUrl && source.DefaultProtocol == item.SourceProtocol))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.NextIndex, nextIndex)
                .SetProperty(item => item.LastProgressAt, committedAt)
                .SetProperty(item => item.PreferFresh, preferFresh ?? true)
                .SetProperty(item => item.FreshBodyHash, item => freshBodyHash == null ? item.FreshBodyHash : freshBodyHash)
                .SetProperty(item => item.Payload, item => completed ? Array.Empty<byte>() : item.Payload)
                .SetProperty(item => item.PayloadHash, item => completed ? Array.Empty<byte>() : item.PayloadHash), token);
        return updated == 1;
    }

    private static bool MatchesSource(VpnSourceImportState state, VpnSource source) =>
        state.SourceUrl == source.Url && state.SourceProtocol == source.DefaultProtocol;

    private static bool ValidState(VpnSourceImportState state) =>
        state.CandidateCount is > 0 and <= VpnCandidateSnapshotCodec.MaxRecords &&
        state.NextIndex >= 0 && state.NextIndex <= state.CandidateCount &&
        state.SnapshotBodyHash.Length == SHA256.HashSizeInBytes && state.FreshBodyHash.Length == SHA256.HashSizeInBytes &&
        (state.NextIndex == state.CandidateCount ? state.Payload.Length == 0 && state.PayloadHash.Length == 0 :
            state.Payload.Length is > 16 and <= VpnCandidateSnapshotCodec.MaxPayloadBytes &&
            state.PayloadHash.Length == SHA256.HashSizeInBytes &&
            BinaryPrimitives.ReadInt32LittleEndian(state.Payload) == VpnCandidateSnapshotCodec.Magic &&
            BinaryPrimitives.ReadInt32LittleEndian(state.Payload.AsSpan(8)) == state.CandidateCount &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(state.Payload), state.PayloadHash));
}

/// <summary>Узкие metadata после admission не удерживают payload всех источников до commit.</summary>
internal sealed record VpnSourceImportCheckpoint(Guid VpnSourceId, string SourceUrl, VpnProtocol SourceProtocol,
    Guid SnapshotId, int CandidateCount, int NextIndex, bool PreferFresh)
{
    internal static VpnSourceImportCheckpoint Capture(VpnSourceImportState state) => new(state.VpnSourceId,
        state.SourceUrl, state.SourceProtocol, state.SnapshotId, state.CandidateCount, state.NextIndex, state.PreferFresh);
}
