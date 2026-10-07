using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Atomic, bounded page checkpoints guarded by owner configuration and generation.</summary>
internal sealed class SourceApiCaptureStore(
    IDbContextFactory<ProxyHarborDbContext> dbFactory, long maximumStoredBytes = SourceApiCaptureStore.MaximumStoredBytes)
{
    internal const long MaximumStoredBytes = 64L * 1024 * 1024;

    internal async Task<SourceApiCaptureCheckpoint?> LoadAsync(SourceApiCaptureOwner owner, CancellationToken token)
    {
        owner.EnsureSupported();
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var state = await Owned(db, owner).AsNoTracking().SingleOrDefaultAsync(token);
        if (state is null) return null;
        if (Matches(state, owner) && await ConfiguredAsync(db, owner, token))
        {
            try
            {
                var capture = FreeProxyDbPageCaptureCodec.Decode(state.Payload, state.PayloadHash, owner.MaximumBytes);
                if (capture.Inspect(owner.MaximumBytes, owner.Url).Complete == state.Complete)
                    return new(state.Id, state.Version, owner, capture);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException) { }
        }
        // A concurrent successful checkpoint cannot be removed by stale recovery.
        await Owned(db, owner).Where(item => item.Id == state.Id && item.Version == state.Version).ExecuteDeleteAsync(token);
        return null;
    }

    internal async Task<SourceApiCaptureCheckpoint?> SaveAsync(
        SourceApiCaptureOwner owner, SourceApiCaptureCheckpoint? expected,
        FreeProxyDbPageCapture capture, CancellationToken token)
    {
        owner.EnsureSupported();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumStoredBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumStoredBytes, MaximumStoredBytes);
        if (expected is not null && expected.Owner != owner) throw new ArgumentException("API checkpoint owner changed.", nameof(expected));
        var previousCount = expected?.Capture.Pages.Count ?? 0;
        if (capture.Pages.Count != previousCount + 1 ||
            (expected is not null && !expected.Capture.Pages.SequenceEqual(capture.Pages.Take(previousCount))))
            throw new InvalidDataException("API checkpoint must extend the committed pages by one validated page.");
        var complete = capture.Inspect(owner.MaximumBytes, owner.Url).Complete;
        var payload = FreeProxyDbPageCaptureCodec.Encode(capture, owner.MaximumBytes);
        var hash = SHA256.HashData(payload);
        // Stable tokens make an execution-strategy replay after a lost commit ACK idempotent.
        var id = expected?.Id ?? Guid.NewGuid();
        var version = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        return await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"SourceApiCaptureStates\" IN SHARE ROW EXCLUSIVE MODE", token);
            if (!await ConfiguredAsync(db, owner, token)) return null;
            var existing = await Owned(db, owner).SingleOrDefaultAsync(token);
            if (existing is not null && existing.Id == id && existing.Version == version && Matches(existing, owner))
                return new SourceApiCaptureCheckpoint(id, version, owner, capture);
            if (expected is null ? existing is not null :
                existing is null || existing.Id != expected.Id || existing.Version != expected.Version || !Matches(existing, owner))
                return null;
            var stored = await db.SourceApiCaptureStates.SumAsync(item => (long)item.StoredBytes, token);
            if (stored - (existing?.StoredBytes ?? 0) + payload.Length > maximumStoredBytes)
                throw new InvalidDataException("Хранилище API-страниц заполнено; загрузка отложена до освобождения места.");
            var state = existing ?? new SourceApiCaptureState
            {
                Id = id,
                ProxySourceId = owner.Vpn ? null : owner.SourceId,
                VpnSourceId = owner.Vpn ? owner.SourceId : null,
                SourceUrl = owner.Url,
                SourceProtocol = owner.Protocol
            };
            state.Version = version;
            state.Payload = payload;
            state.PayloadHash = hash;
            state.Complete = complete;
            state.UpdatedAt = now;
            if (existing is null) db.SourceApiCaptureStates.Add(state);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new SourceApiCaptureCheckpoint(id, version, owner, capture);
        });
    }

    internal async Task<bool> DiscardAsync(SourceApiCaptureCheckpoint expected, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await Owned(db, expected.Owner).Where(item => item.Id == expected.Id && item.Version == expected.Version)
            .ExecuteDeleteAsync(token) == 1;
    }

    internal async Task CleanupAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await db.SourceApiCaptureStates.Where(state =>
            (state.ProxySourceId != null && !db.Sources.Any(source => source.Id == state.ProxySourceId &&
                source.Enabled && source.Url == state.SourceUrl && (int)source.DefaultProtocol == state.SourceProtocol)) ||
            (state.VpnSourceId != null && !db.VpnSources.Any(source => source.Id == state.VpnSourceId &&
                source.Enabled && source.Url == state.SourceUrl && (int)source.DefaultProtocol == state.SourceProtocol)))
            .ExecuteDeleteAsync(token);
    }

    private static IQueryable<SourceApiCaptureState> Owned(ProxyHarborDbContext db, SourceApiCaptureOwner owner) =>
        owner.Vpn ? db.SourceApiCaptureStates.Where(item => item.VpnSourceId == owner.SourceId) :
            db.SourceApiCaptureStates.Where(item => item.ProxySourceId == owner.SourceId);

    private static bool Matches(SourceApiCaptureState state, SourceApiCaptureOwner owner) =>
        state.SourceUrl == owner.Url && state.SourceProtocol == owner.Protocol;

    private static Task<bool> ConfiguredAsync(ProxyHarborDbContext db, SourceApiCaptureOwner owner, CancellationToken token) =>
        owner.Vpn ? db.VpnSources.AnyAsync(source => source.Id == owner.SourceId && source.Enabled &&
            source.Url == owner.Url && (int)source.DefaultProtocol == owner.Protocol, token) :
            db.Sources.AnyAsync(source => source.Id == owner.SourceId && source.Enabled &&
                source.Url == owner.Url && (int)source.DefaultProtocol == owner.Protocol, token);
}

internal sealed record SourceApiCaptureOwner(Guid SourceId, bool Vpn, string Url, int Protocol)
{
    internal int MaximumBytes => Vpn ? FreeProxyDbPageCapture.MaximumCaptureBytes : 10_000_000;
    internal static SourceApiCaptureOwner From(ProxySource source) => new(source.Id, false, source.Url, (int)source.DefaultProtocol);
    internal static SourceApiCaptureOwner From(VpnSource source) => new(source.Id, true, source.Url, (int)source.DefaultProtocol);
    internal void EnsureSupported()
    {
        if (SourceId == Guid.Empty || Protocol < 0 || Protocol > (Vpn ? 14 : 3) ||
            !(Vpn ? FreeProxyDbPageCapture.SupportsVpn(Url) : FreeProxyDbPageCapture.SupportsHttp(Url)))
            throw new InvalidDataException("Unsupported public API source configuration.");
    }
}

internal sealed record SourceApiCaptureCheckpoint(Guid Id, Guid Version, SourceApiCaptureOwner Owner, FreeProxyDbPageCapture Capture);
