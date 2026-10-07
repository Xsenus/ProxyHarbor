using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyScrapeSourceApiIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SavedPrefixResumesOffsetTailAndReconcilesHeadBeforeCollectorHandoff()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new ProxySource { Name = "Public ProxyScrape restart fixture", Url = ProxyScrapePublicApi.Url };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }
        var owner = SourceApiCaptureOwner.From(source);
        var store = new SourceApiCaptureStore(database.Factory);
        var head = ProxyScrapePublicApiTests.Page(0, 3, true, Row("8.8.8.8"), Row("1.1.1.1", "socks4"));
        var first = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            ProxyScrapePublicApi.CompactPage(head, 1)), owner.MaximumBytes);
        var committed = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(owner, null, first, CancellationToken.None));
        var restored = Assert.IsType<SourceApiCaptureCheckpoint>(await new SourceApiCaptureStore(database.Factory).LoadAsync(owner, CancellationToken.None));
        Assert.Equal(committed.Version, restored.Version);
        Assert.Equal(2, restored.Capture.Inspect(owner.MaximumBytes, owner.Url).NextPage);
        await using var otherLease = Assert.IsType<PostgresAdvisoryLock>(await new SourceApiOriginGate(database.Factory, ProxoraPublicApi.Url).TryAcquireAsync(CancellationToken.None));
        var urls = new List<string>();
        var bodies = new Queue<string>([ProxyScrapePublicApiTests.Page(2000, 2000, false, Row("9.9.9.9", "socks5")),
            ProxyScrapePublicApiTests.Page(4000, 3, false),
            ProxyScrapePublicApiTests.Page(0, 4, true, Row("8.8.8.8"), Row("208.67.222.222"))]);
        var result = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner, (url, _) =>
        {
            urls.Add(url);
            return Task.FromResult(new SourceFetchResult(bodies.Dequeue(), false, null, null));
        }, CancellationToken.None);
        Assert.Equal(3, urls.Count);
        Assert.Contains("skip=2000&", urls[0]);
        Assert.Contains("skip=4000&", urls[1]);
        Assert.Equal(ProxyScrapePublicApi.Url, urls[2]);
        Assert.True(result.NetworkObserved);
        Assert.Equal(first.Pages[0].CapturedAt, result.ObservedAt);
        Assert.Equal(result.Checkpoint.Capture.Pages[^1].CapturedAt.AddMinutes(5), result.NextRefreshAt);
        Assert.Equal(4, SourceFeedParser.ParseRequired(result.Fetch.Content!, ProxyProtocol.Http).Count);
        var cached = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner,
            (_, _) => throw new InvalidOperationException("Completed capture requested HTTP"), CancellationToken.None);
        Assert.False(cached.NetworkObserved);
        Assert.Equal(result.Fetch.Content, cached.Fetch.Content);
        await using var verify = database.Factory.CreateDbContext();
        Assert.True((await verify.SourceApiCaptureStates.SingleAsync()).Complete);
        Assert.Null((await verify.Sources.SingleAsync()).LastSucceededAt);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OriginLeaseAndRetryAfterRemainDurableWithoutBlockingOtherProviders()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = new ProxySource { Name = "Public ProxyScrape quota fixture", Url = ProxyScrapePublicApi.Url };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }
        var gate = new SourceApiOriginGate(database.Factory, source.Url);
        await using (var lease = Assert.IsType<PostgresAdvisoryLock>(await gate.TryAcquireAsync(CancellationToken.None)))
            Assert.Null(await new SourceApiOriginGate(database.Factory, source.Url).TryAcquireAsync(CancellationToken.None));
        var retryAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        var requests = 0;
        await Assert.ThrowsAsync<SourceRateLimitException>(() => new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(
            SourceApiCaptureOwner.From(source), (_, _) => { requests++; throw new SourceRateLimitException(retryAt, System.Net.HttpStatusCode.TooManyRequests); }, CancellationToken.None));
        Assert.Equal(1, requests);
        await gate.ReserveRequestAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(retryAt, await new SourceApiOriginGate(database.Factory, source.Url).ReadDeadlineAsync(CancellationToken.None));
        Assert.Null(await new SourceApiOriginGate(database.Factory, ProxoraPublicApi.Url).ReadDeadlineAsync(CancellationToken.None));
        Assert.Null(await new SourceApiOriginGate(database.Factory).ReadDeadlineAsync(CancellationToken.None));
        await Assert.ThrowsAsync<SourceApiDeferredException>(() => new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(
            SourceApiCaptureOwner.From(source), (_, _) => { requests++; throw new InvalidOperationException("Quota was bypassed"); }, CancellationToken.None));
        Assert.Equal(1, requests);
    }

    private static object Row(string host, string protocol = "http") => new { ip = host, port = 1080, protocol };
}
