using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>
/// Coalesces checker credential reads into a short-lived, fail-closed snapshot of enabled nodes.
/// </summary>
public sealed class CheckerNodeCredentialCache(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    TimeProvider timeProvider) : IDisposable
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(2);
    private static readonly byte[] MissingCredentialHash = new byte[SHA256.HashSizeInBytes];
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CredentialSnapshot? snapshot;
    private long generation;
    private long authenticationAttempts;
    private long authenticationFailures;
    private long snapshotHits;
    private long databaseReads;
    private long invalidations;

    internal long AuthenticationAttempts => Interlocked.Read(ref authenticationAttempts);
    internal long AuthenticationFailures => Interlocked.Read(ref authenticationFailures);
    internal long SnapshotHits => Interlocked.Read(ref snapshotHits);
    internal long DatabaseReads => Interlocked.Read(ref databaseReads);
    internal long Invalidations => Interlocked.Read(ref invalidations);

    /// <summary>Authenticates one node token against the current enabled-node snapshot.</summary>
    public async ValueTask<bool> AuthenticateAsync(
        Guid nodeId,
        string rawToken,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref authenticationAttempts);
        if (nodeId == Guid.Empty || rawToken.Length is < 32 or > 256)
        {
            Interlocked.Increment(ref authenticationFailures);
            return false;
        }

        var current = await GetSnapshotAsync(cancellationToken);
        var found = current.Credentials.TryGetValue(nodeId, out var expected);
        expected ??= MissingCredentialHash;
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        var authenticated = CryptographicOperations.FixedTimeEquals(expected, actual) && found;
        if (!authenticated)
            Interlocked.Increment(ref authenticationFailures);
        return authenticated;
    }

    /// <summary>
    /// Invalidates cached credentials after a node is created, disabled, rotated or deleted.
    /// </summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref invalidations);
        Interlocked.Increment(ref generation);
        Volatile.Write(ref snapshot, null);
    }

    private async ValueTask<CredentialSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref snapshot);
        if (current is not null && IsFresh(current))
        {
            Interlocked.Increment(ref snapshotHits);
            return current;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            current = Volatile.Read(ref snapshot);
            if (current is not null && IsFresh(current))
            {
                Interlocked.Increment(ref snapshotHits);
                return current;
            }

            while (true)
            {
                var observedGeneration = Volatile.Read(ref generation);
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var rows = await db.CheckerNodes.AsNoTracking()
                    .Where(node => node.Enabled)
                    .Select(node => new { node.Id, node.TokenHash })
                    .ToListAsync(cancellationToken);
                Interlocked.Increment(ref databaseReads);

                var credentials = rows
                    .Where(row => row.TokenHash is { Length: SHA256.HashSizeInBytes })
                    .ToFrozenDictionary(row => row.Id, row => row.TokenHash);
                var refreshed = new CredentialSnapshot(timeProvider.GetTimestamp(), credentials);

                // An administrator may rotate or revoke a credential while the query is in
                // flight. Never publish a snapshot produced across that invalidation boundary.
                if (observedGeneration != Volatile.Read(ref generation))
                    continue;

                Volatile.Write(ref snapshot, refreshed);
                return refreshed;
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private bool IsFresh(CredentialSnapshot value) =>
        timeProvider.GetElapsedTime(value.CreatedTimestamp, timeProvider.GetTimestamp()) < MaximumAge;

    /// <inheritdoc />
    public void Dispose() => refreshGate.Dispose();

    private sealed record CredentialSnapshot(
        long CreatedTimestamp,
        FrozenDictionary<Guid, byte[]> Credentials);
}
