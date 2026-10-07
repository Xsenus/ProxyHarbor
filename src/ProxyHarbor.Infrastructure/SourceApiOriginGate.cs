using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>One distributed request lane per supported provider, with durable pacing and Retry-After.</summary>
internal sealed class SourceApiOriginGate(IDbContextFactory<ProxyHarborDbContext> dbFactory,
    string sourceUrl = FreeProxyDbFeedFetcher.Url)
{
    internal const string Origin = "https://freeproxydb.com";
    internal static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(10);
    private readonly int _provider = Provider(sourceUrl);
    private string ProviderOrigin => _provider switch { 1 => ProxiwarePublicApi.Origin, 2 => RoundProxiesPublicApi.Origin, 3 => Socks5ProxiesPublicApi.Origin, _ => Origin };
    internal TimeSpan RequestInterval => _provider == 0 ? MinimumRequestInterval : TimeSpan.FromSeconds(1);

    internal Task<PostgresAdvisoryLock?> TryAcquireAsync(CancellationToken token) =>
        PostgresAdvisoryLock.TryAcquireAsync(dbFactory,
            _provider switch { 1 => PostgresAdvisoryLock.ProxiwareApiKey, 2 => PostgresAdvisoryLock.RoundProxiesApiKey, 3 => PostgresAdvisoryLock.Socks5ProxiesApiKey, _ => PostgresAdvisoryLock.FreeProxyDbApiKey }, token);

    internal async Task<DateTimeOffset?> ReadDeadlineAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.SourceApiOriginStates.Where(state => state.Origin == ProviderOrigin)
            .Select(state => (DateTimeOffset?)state.NotBefore).SingleOrDefaultAsync(token);
    }

    // Call under the origin session lease. Write pacing BEFORE the network request,
    // so a crashed worker cannot immediately be replaced by a burst from another worker.
    internal Task ReserveRequestAsync(DateTimeOffset startedAt, CancellationToken token) =>
        ExtendDeadlineAsync(startedAt.Add(RequestInterval), token);

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
                INSERT INTO "SourceApiOriginStates" ("Origin", "NotBefore") VALUES ({ProviderOrigin}, {notBefore})
                ON CONFLICT ("Origin") DO UPDATE
                SET "NotBefore" = GREATEST("SourceApiOriginStates"."NotBefore", EXCLUDED."NotBefore")
                """, token);
        });
    }

    private static int Provider(string url) => ProxiwarePublicApi.Supports(url) ? 1 : RoundProxiesPublicApi.Supports(url) ? 2 : Socks5ProxiesPublicApi.Supports(url) ? 3 :
        FreeProxyDbPageCapture.Supports(url) ? 0 : throw new InvalidDataException("Unsupported public API provider.");
}
