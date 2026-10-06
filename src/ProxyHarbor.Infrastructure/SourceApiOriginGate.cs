using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>One distributed FreeProxyDB request lane, with durable pacing and Retry-After.</summary>
internal sealed class SourceApiOriginGate(IDbContextFactory<ProxyHarborDbContext> dbFactory)
{
    internal const string Origin = "https://freeproxydb.com";
    internal static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(10);

    internal Task<PostgresAdvisoryLock?> TryAcquireAsync(CancellationToken token) =>
        PostgresAdvisoryLock.TryAcquireAsync(dbFactory, PostgresAdvisoryLock.FreeProxyDbApiKey, token);

    internal async Task<DateTimeOffset?> ReadDeadlineAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.SourceApiOriginStates.Where(state => state.Origin == Origin)
            .Select(state => (DateTimeOffset?)state.NotBefore).SingleOrDefaultAsync(token);
    }

    // Call under the origin session lease. Write pacing BEFORE the network request,
    // so a crashed worker cannot immediately be replaced by a burst from another worker.
    internal Task ReserveRequestAsync(DateTimeOffset startedAt, CancellationToken token) =>
        ExtendDeadlineAsync(startedAt.Add(MinimumRequestInterval), token);

    internal async Task ExtendDeadlineAsync(DateTimeOffset notBefore, CancellationToken token)
    {
        if (notBefore.Offset != TimeSpan.Zero || notBefore < DateTimeOffset.UnixEpoch ||
            notBefore == DateTimeOffset.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(notBefore));
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            // Atomic max prevents any shorter local pacing deadline from erasing a quota pause.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SourceApiOriginStates" ("Origin", "NotBefore") VALUES ({Origin}, {notBefore})
                ON CONFLICT ("Origin") DO UPDATE
                SET "NotBefore" = GREATEST("SourceApiOriginStates"."NotBefore", EXCLUDED."NotBefore")
                """, token);
        });
    }
}
