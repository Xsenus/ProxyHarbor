using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxoraSourceApiIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RestartReconcilesTypedPagesIndependentlyOfOtherProviderLeases()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new ProxySource { Name = "Proxora restart fixture", Url = ProxoraPublicApi.Url };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Sources.Add(source); await db.SaveChangesAsync();
        }
        var owner = SourceApiCaptureOwner.From(source);
        const string head = """{"total":2,"page":1,"per_page":500,"items":[{"host":"8.8.8.8","port":1080,"protocol":"socks4"}]}""";
        const string tail = """{"total":2,"page":2,"per_page":500,"items":[{"host":"8.8.8.8","port":1080,"protocol":"socks5"}]}""";
        var first = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            ProxoraPublicApi.CompactPage(head, 1)), owner.MaximumBytes);
        var store = new SourceApiCaptureStore(database.Factory);
        var committed = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(owner, null, first, CancellationToken.None));
        var second = first.Append(new(2, false, DateTimeOffset.UtcNow, ProxoraPublicApi.CompactPage(tail, 2)), owner.MaximumBytes);
        _ = await store.SaveAsync(owner, committed, second, CancellationToken.None);
        var restored = Assert.IsType<SourceApiCaptureCheckpoint>(await new SourceApiCaptureStore(database.Factory).LoadAsync(owner, CancellationToken.None));
        Assert.True(restored.Capture.Inspect(owner.MaximumBytes).Reconciliation);
        await using var free = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory).TryAcquireAsync(CancellationToken.None));
        await using var proxiware = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory, ProxiwarePublicApi.Url).TryAcquireAsync(CancellationToken.None));
        await using var round = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory, RoundProxiesPublicApi.Url).TryAcquireAsync(CancellationToken.None));
        await using var socks = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory, Socks5ProxiesPublicApi.Url).TryAcquireAsync(CancellationToken.None));
        var requests = 0;
        var result = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner, (url, _) =>
        {
            requests++; Assert.Equal(ProxoraPublicApi.Url, url);
            return Task.FromResult(new SourceFetchResult(head, false, null, null));
        }, CancellationToken.None);
        Assert.Equal(1, requests);
        Assert.True(result.NetworkObserved);
        Assert.Equal(2, SourceFeedParser.ParseRequired(result.Fetch.Content!, ProxyProtocol.Http).Count);
        Assert.Equal(result.Checkpoint.Capture.Pages[^1].CapturedAt.AddMinutes(5), result.NextRefreshAt);
        var cached = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner,
            (_, _) => throw new InvalidOperationException("Cached snapshot requested HTTP"), CancellationToken.None);
        Assert.False(cached.NetworkObserved);
        Assert.Equal(result.Fetch.Content, cached.Fetch.Content);
        await using var verify = database.Factory.CreateDbContext();
        Assert.Null((await verify.Sources.SingleAsync()).LastSucceededAt);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DefaultCycleCapturesOnePageAndPersistsMinutePacingAcrossRestarts()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new ProxySource { Name = "Proxora pacing fixture", Url = ProxoraPublicApi.Url };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Sources.Add(source); await db.SaveChangesAsync();
        }
        var owner = SourceApiCaptureOwner.From(source);
        var before = DateTimeOffset.UtcNow;
        var requests = 0;
        await Assert.ThrowsAsync<SourceApiDeferredException>(() => new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner, (_, _) =>
        {
            requests++;
            return Task.FromResult(new SourceFetchResult("""{"total":2,"page":1,"per_page":500,"items":[{"host":"8.8.8.8","port":1080,"protocol":"http"}]}""", false, null, null));
        }, CancellationToken.None));
        Assert.Equal(1, requests);
        var gate = new SourceApiOriginGate(database.Factory, source.Url);
        Assert.True(await gate.ReadDeadlineAsync(CancellationToken.None) >= before.AddMinutes(1));
        await Assert.ThrowsAsync<SourceApiDeferredException>(() => new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner,
            (_, _) => throw new InvalidOperationException("Minute pacing bypassed"), CancellationToken.None));
        await using (var lease = Assert.IsType<PostgresAdvisoryLock>(await gate.TryAcquireAsync(CancellationToken.None)))
        {
            Assert.Null(await new SourceApiOriginGate(database.Factory, source.Url).TryAcquireAsync(CancellationToken.None));
            await gate.ExtendDeadlineAsync(DateTimeOffset.UtcNow.AddHours(2), CancellationToken.None);
            await gate.ReserveRequestAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(await gate.ReadDeadlineAsync(CancellationToken.None) > DateTimeOffset.UtcNow.AddHours(1));
        }
        foreach (var url in new[] { FreeProxyDbFeedFetcher.Url, ProxiwarePublicApi.Url, RoundProxiesPublicApi.Url, Socks5ProxiesPublicApi.Url })
            Assert.Null(await new SourceApiOriginGate(database.Factory, url).ReadDeadlineAsync(CancellationToken.None));
        var checkpoint = Assert.IsType<SourceApiCaptureCheckpoint>(await new SourceApiCaptureStore(database.Factory).LoadAsync(owner, CancellationToken.None));
        Assert.False(checkpoint.Capture.Inspect(owner.MaximumBytes).Complete);
        Assert.Equal(2, checkpoint.Capture.Inspect(owner.MaximumBytes).NextPage);
    }
}
