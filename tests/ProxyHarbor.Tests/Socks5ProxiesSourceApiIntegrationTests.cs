using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class Socks5ProxiesSourceApiIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RestartRestoresCompactPagesWhileOtherProviderLeasesRemainHeld()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new ProxySource { Name = "Public offset fixture", Url = Socks5ProxiesPublicApi.Url };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }
        var owner = SourceApiCaptureOwner.From(source);
        var store = new SourceApiCaptureStore(database.Factory);
        const string head = """{"meta":{"total":2,"limit":100,"offset":0},"data":[{"ip":"8.8.8.8","port":1080,"protocols":["socks4","socks5"]}]}""";
        var first = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            Socks5ProxiesPublicApi.CompactPage(head, 1)), owner.MaximumBytes);
        var committed = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(owner, null, first, CancellationToken.None));
        var restored = Assert.IsType<SourceApiCaptureCheckpoint>(await new SourceApiCaptureStore(database.Factory).LoadAsync(owner, CancellationToken.None));
        Assert.Equal(committed.Version, restored.Version);
        Assert.Equal(2, restored.Capture.Inspect(owner.MaximumBytes, owner.Url).NextPage);
        await using var freeLease = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory).TryAcquireAsync(CancellationToken.None));
        await using var proxiwareLease = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory, ProxiwarePublicApi.Url).TryAcquireAsync(CancellationToken.None));
        await using var roundLease = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory, RoundProxiesPublicApi.Url).TryAcquireAsync(CancellationToken.None));
        var calls = 0;
        var result = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner, (url, _) =>
        {
            calls++;
            return Task.FromResult(new SourceFetchResult(url.Contains("offset=100&", StringComparison.Ordinal)
                ? """{"meta":{"total":2,"limit":100,"offset":100},"data":[{"ip":"1.1.1.1","port":443,"protocols":["https"]}]}"""
                : head, false, null, null));
        }, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.True(result.NetworkObserved);
        Assert.Equal(first.Pages[0].CapturedAt, result.ObservedAt);
        Assert.Equal(result.Checkpoint.Capture.Pages[^1].CapturedAt.AddMinutes(30), result.NextRefreshAt);
        Assert.Equal(3, SourceFeedParser.ParseRequired(result.Fetch.Content!, ProxyProtocol.Http).Count);
        var cached = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner,
            (_, _) => throw new InvalidOperationException("Completed capture requested HTTP"), CancellationToken.None);
        Assert.False(cached.NetworkObserved);
        Assert.Equal(result.Fetch.Content, cached.Fetch.Content);
        await using var read = database.Factory.CreateDbContext();
        Assert.True((await read.SourceApiCaptureStates.SingleAsync()).Complete);
        Assert.Null((await read.Sources.SingleAsync()).LastSucceededAt);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OriginLeaseAndPersistedCooldownPreventRequestsWithoutDelayingOtherProviders()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new ProxySource { Name = "Public offset cooldown fixture", Url = Socks5ProxiesPublicApi.Url };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }
        var gate = new SourceApiOriginGate(database.Factory, source.Url);
        await using (var lease = Assert.IsType<PostgresAdvisoryLock>(await gate.TryAcquireAsync(CancellationToken.None)))
        {
            Assert.Null(await new SourceApiOriginGate(database.Factory, source.Url).TryAcquireAsync(CancellationToken.None));
            await gate.ExtendDeadlineAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
            await gate.ReserveRequestAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }
        foreach (var url in new[] { FreeProxyDbFeedFetcher.Url, ProxiwarePublicApi.Url, RoundProxiesPublicApi.Url })
            Assert.Null(await new SourceApiOriginGate(database.Factory, url).ReadDeadlineAsync(CancellationToken.None));
        await Assert.ThrowsAsync<SourceApiDeferredException>(() => new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(
            SourceApiCaptureOwner.From(source), (_, _) => throw new InvalidOperationException("Cooldown was bypassed"), CancellationToken.None));
    }
}
