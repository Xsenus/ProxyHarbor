using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class RoundProxiesSourceApiIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RestartKeepsOwnerSchemaAndRoundLeaseIsIndependentOfBothOtherProviders()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new ProxySource { Name = "Public Round fixture", Url = RoundProxiesPublicApi.Url };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }
        var owner = SourceApiCaptureOwner.From(source);
        var store = new SourceApiCaptureStore(database.Factory);
        const string head = """{"total":2,"page":1,"limit":500,"data":[{"_id":"a","ip":"8.8.8.8","port":1080,"protocols":["socks4","socks5"]}]}""";
        var first = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow, head), owner.MaximumBytes);
        var committed = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(owner, null, first, CancellationToken.None));
        var restored = Assert.IsType<SourceApiCaptureCheckpoint>(await new SourceApiCaptureStore(database.Factory).LoadAsync(owner, CancellationToken.None));
        Assert.Equal(committed.Version, restored.Version);
        Assert.Equal(2, restored.Capture.Inspect(owner.MaximumBytes, owner.Url).NextPage);
        var wrong = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            """{"total_proxies":1,"proxies":[{"addr":"8.8.8.8","port":80,"protocol":"http"}]}"""), owner.MaximumBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(owner, null, wrong, CancellationToken.None));
        await using var freeLease = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory).TryAcquireAsync(CancellationToken.None));
        await using var proxiwareLease = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory, ProxiwarePublicApi.Url).TryAcquireAsync(CancellationToken.None));
        var requests = 0;
        var result = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner, (url, _) =>
        {
            requests++;
            var body = url.Contains("page=2&", StringComparison.Ordinal)
                ? """{"total":2,"page":2,"limit":500,"data":[{"_id":"b","ip":"1.1.1.1","port":443,"protocols":["https"]}]}""" : head;
            return Task.FromResult(new SourceFetchResult(body, false, null, null));
        }, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.True(result.NetworkObserved);
        Assert.Equal(first.Pages[0].CapturedAt, result.ObservedAt);
        Assert.Equal(result.Checkpoint.Capture.Pages[^1].CapturedAt.AddMinutes(5), result.NextRefreshAt);
        Assert.Equal(3, SourceFeedParser.ParseRequired(result.Fetch.Content!, ProxyProtocol.Http).Count);
        var cached = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner,
            (_, _) => throw new InvalidOperationException("Completed capture requested HTTP"), CancellationToken.None);
        Assert.False(cached.NetworkObserved);
        Assert.Equal(result.Fetch.Content, cached.Fetch.Content);
        await using (var db = database.Factory.CreateDbContext())
        {
            Assert.True((await db.SourceApiCaptureStates.SingleAsync()).Complete);
            Assert.Null((await db.Sources.SingleAsync()).LastSucceededAt);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DurableRoundCooldownCannotBeShortenedOrDelayOtherProviderOrigins()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new ProxySource { Name = "Public Round cooldown fixture", Url = RoundProxiesPublicApi.Url };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }
        var gate = new SourceApiOriginGate(database.Factory, source.Url);
        Assert.Equal(TimeSpan.FromSeconds(1), gate.RequestInterval);
        await using (var lease = Assert.IsType<PostgresAdvisoryLock>(await gate.TryAcquireAsync(CancellationToken.None)))
        {
            Assert.Null(await new SourceApiOriginGate(database.Factory, source.Url).TryAcquireAsync(CancellationToken.None));
            await gate.ExtendDeadlineAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
            await gate.ReserveRequestAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }
        Assert.Null(await new SourceApiOriginGate(database.Factory).ReadDeadlineAsync(CancellationToken.None));
        Assert.Null(await new SourceApiOriginGate(database.Factory, ProxiwarePublicApi.Url).ReadDeadlineAsync(CancellationToken.None));
        var calls = 0;
        await Assert.ThrowsAsync<SourceApiDeferredException>(() => new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(
            SourceApiCaptureOwner.From(source), (_, _) => { calls++; throw new InvalidOperationException("Cooldown was bypassed"); }, CancellationToken.None));
        Assert.Equal(0, calls);
    }
}
