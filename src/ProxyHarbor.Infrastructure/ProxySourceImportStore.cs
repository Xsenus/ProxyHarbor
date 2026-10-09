using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Сохраняет immutable snapshots до импорта, а cursor подтверждает только после bulk commit.</summary>
internal sealed class ProxySourceImportStore(
    IDbContextFactory<ProxyHarborDbContext> dbFactory, long maxStoredBytes = ProxySourceImportStore.MaxStoredBytes)
{
    internal static int ParserVersionForUrl(string url) => PxysCsvFeedAdapter.Supports(url) || Au1rxxSingBoxFeedAdapter.Supports(url) ? 1 : 0;

    internal const long MaxStoredBytes = 512L * 1_024 * 1_024;

    internal async Task<ProxySourceImportState?> LoadAsync(ProxySource source, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var state = await db.ProxySourceImportStates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProxySourceId == source.Id, token);
        if (state is null || !MatchesSource(state, source)) return null;
        if (state.NextIndex == state.CandidateCount || ValidPayload(state)) return state;
        // Повреждённый ephemeral cache не должен навсегда блокировать полный re-fetch.
        await db.ProxySourceImportStates.Where(item => item.ProxySourceId == state.ProxySourceId &&
                item.SnapshotId == state.SnapshotId)
            .ExecuteDeleteAsync(token);
        return null;
    }

    internal async Task<ProxySourceImportState?> BeginAsync(
        ProxySource source, ProxyCandidateSnapshot snapshot, CancellationToken token)
    {
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        return await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(
            () => BeginAttemptAsync(source, snapshot, token));
    }

    private async Task<ProxySourceImportState?> BeginAttemptAsync(
        ProxySource source, ProxyCandidateSnapshot snapshot, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStoredBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxStoredBytes, MaxStoredBytes);
        var state = new ProxySourceImportState
        {
            ProxySourceId = source.Id,
            SourceUrl = source.Url,
            SourceProtocol = source.DefaultProtocol,
            ParserVersion = ParserVersionForUrl(source.Url),
            CandidateCount = snapshot.Count,
            Payload = snapshot.Payload,
            PayloadHash = SHA256.HashData(snapshot.Payload),
            FreshBodyHash = snapshot.BodyHash
        };
        if (!ValidPayload(state)) throw new InvalidDataException("Некорректный снимок proxy-источника.");
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Бюджет считается в той же transaction, что и admission. Два producer или
        // другой процесс не могут одновременно занять один остаток storage-квоты.
        await db.Database.ExecuteSqlRawAsync(
            "LOCK TABLE \"ProxySourceImportStates\" IN SHARE ROW EXCLUSIVE MODE", token);
        var configured = await db.Sources.AnyAsync(item => item.Id == source.Id && item.Enabled &&
            item.Url == source.Url && item.DefaultProtocol == source.DefaultProtocol, token);
        if (!configured) return null;
        var existing = await db.ProxySourceImportStates.SingleOrDefaultAsync(
            item => item.ProxySourceId == source.Id, token);
        // Новый HTTP body не вытесняет ещё не импортированный хвост старого snapshot.
        if (existing is not null && MatchesSource(existing, source) &&
            existing.NextIndex < existing.CandidateCount && ValidPayload(existing))
            return existing;
        var stored = await db.ProxySourceImportStates.SumAsync(item => (long)item.StoredBytes, token);
        if (stored - (existing?.StoredBytes ?? 0) + snapshot.Payload.Length > maxStoredBytes)
            throw new InvalidDataException("Хранилище незавершённых proxy-снимков заполнено; импорт будет повторён после освобождения места.");
        if (existing is null)
            db.ProxySourceImportStates.Add(state);
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

    internal static ProxySnapshotWindow ReadWindow(
        ProxySourceImportState state, int maxResults, Func<ProxyCandidateKey, bool> accept)
    {
        if (state.ParserVersion != ParserVersionForUrl(state.SourceUrl) || !ValidPayload(state)) throw new InvalidDataException("Сохранённый proxy-снимок повреждён.");
        return ProxyCandidateSnapshotCodec.ReadWindow(state.Payload, state.NextIndex, maxResults, accept);
    }

    internal async Task<bool> IsCurrentOrDiscardAsync(ProxySourceImportState state, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        if (state.ParserVersion == ParserVersionForUrl(state.SourceUrl) && await db.Sources.AnyAsync(source => source.Id == state.ProxySourceId && source.Enabled &&
                source.Url == state.SourceUrl && source.DefaultProtocol == state.SourceProtocol, token))
            return true;
        await db.ProxySourceImportStates.Where(item => item.ProxySourceId == state.ProxySourceId &&
                item.SnapshotId == state.SnapshotId).ExecuteDeleteAsync(token);
        return false;
    }

    /// <summary>Вызывается после успешного commit всех admitted candidates; replay при crash остаётся безопасным.</summary>
    internal Task<bool> AcknowledgeCommittedImportAsync(
        ProxySourceImportState state, int nextIndex, DateTimeOffset committedAt, CancellationToken token)
        => AcknowledgeCommittedImportAsync(ProxySourceImportCheckpoint.Capture(state), nextIndex, committedAt, token);

    internal async Task<bool> AcknowledgeCommittedImportAsync(
        ProxySourceImportCheckpoint state, int nextIndex, DateTimeOffset committedAt, CancellationToken token,
        byte[]? freshBodyHash = null, bool? preferFresh = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nextIndex, state.NextIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nextIndex, state.CandidateCount);
        if (freshBodyHash is not null && freshBodyHash.Length != SHA256.HashSizeInBytes)
            throw new ArgumentException("Некорректный hash свежего body.", nameof(freshBodyHash));
        if (nextIndex == state.NextIndex && preferFresh == state.PreferFresh) return false;
        if (nextIndex == state.NextIndex && preferFresh is null) return false;
        if (state.ParserVersion != ParserVersionForUrl(state.SourceUrl)) return false;
        var completed = nextIndex == state.CandidateCount;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var updated = await db.ProxySourceImportStates.Where(item =>
                item.ProxySourceId == state.ProxySourceId && item.SnapshotId == state.SnapshotId &&
                item.NextIndex == state.NextIndex && item.CandidateCount == state.CandidateCount &&
                item.PreferFresh == state.PreferFresh && item.ParserVersion == state.ParserVersion &&
                item.SourceUrl == state.SourceUrl && item.SourceProtocol == state.SourceProtocol &&
                db.Sources.Any(source => source.Id == item.ProxySourceId && source.Enabled &&
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

    private static bool MatchesSource(ProxySourceImportState state, ProxySource source) =>
        string.Equals(state.SourceUrl, source.Url, StringComparison.Ordinal) && state.SourceProtocol == source.DefaultProtocol &&
        state.ParserVersion == ParserVersionForUrl(source.Url);

    private static bool ValidPayload(ProxySourceImportState state) =>
        state.CandidateCount is > 0 and <= ProxyCandidateSnapshotCodec.MaxCandidates &&
        state.NextIndex >= 0 && state.NextIndex < state.CandidateCount &&
        state.Payload.Length is > 8 and <= ProxyCandidateSnapshotCodec.MaxPayloadBytes &&
        state.PayloadHash.Length == SHA256.HashSizeInBytes &&
        BinaryPrimitives.ReadInt32LittleEndian(state.Payload) == ProxyCandidateSnapshotCodec.Magic &&
        BinaryPrimitives.ReadInt32LittleEndian(state.Payload.AsSpan(4)) == state.CandidateCount &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(state.Payload), state.PayloadHash);
}

/// <summary>После admission сохраняются только узкие metadata; payload не удерживается до конца всего run.</summary>
internal sealed record ProxySourceImportCheckpoint(
    Guid ProxySourceId, string SourceUrl, ProxyProtocol SourceProtocol,
    Guid SnapshotId, int CandidateCount, int NextIndex, bool PreferFresh, int ParserVersion)
{
    internal static ProxySourceImportCheckpoint Capture(ProxySourceImportState state) => new(
        state.ProxySourceId, state.SourceUrl, state.SourceProtocol,
        state.SnapshotId, state.CandidateCount, state.NextIndex, state.PreferFresh, state.ParserVersion);
}
