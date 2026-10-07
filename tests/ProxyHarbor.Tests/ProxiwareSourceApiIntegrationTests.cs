using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxiwareSourceApiIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DurableRestartUsesProviderSchemaAndFinishesUnderIndependentFreeProxyDbLease()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        ProxySource source;
        await using (var db = database.Factory.CreateDbContext())
        {
            source = new() { Name = "Public Proxiware fixture", Url = ProxiwarePublicApi.Url };
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }
        var owner = SourceApiCaptureOwner.From(source);
        var store = new SourceApiCaptureStore(database.Factory);
        var first = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            """{"total_proxies":2,"proxies":[{"addr":"8.8.8.8","port":1080,"protocol":"socks4socks5"}]}"""), owner.MaximumBytes);
        var checkpoint = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(owner, null, first, CancellationToken.None));
        var restarted = Assert.IsType<SourceApiCaptureCheckpoint>(await new SourceApiCaptureStore(database.Factory).LoadAsync(owner, CancellationToken.None));
        Assert.Equal(checkpoint.Version, restarted.Version);
        Assert.Equal(2, restarted.Capture.Inspect(owner.MaximumBytes, owner.Url).NextPage);
        var wrongProvider = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            """{"status":1,"data":{"total_count":2,"data":[{"id":1}]}}"""), owner.MaximumBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(owner, null, wrongProvider, CancellationToken.None));
        var freeGate = new SourceApiOriginGate(database.Factory);
        await using var freeLease = Assert.IsType<PostgresAdvisoryLock>(await freeGate.TryAcquireAsync(CancellationToken.None));
        var requested = new List<string>();
        var completed = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner, (url, _) =>
        {
            requested.Add(url);
            var response = url.Contains("page=2&", StringComparison.Ordinal)
                ? """{"total_proxies":2,"proxies":[{"addr":"1.1.1.1","port":443,"protocol":"https"}]}"""
                : """{"total_proxies":2,"proxies":[{"addr":"8.8.8.8","port":1080,"protocol":"socks4socks5"}]}""";
            return Task.FromResult(new SourceFetchResult(response, false, null, null));
        }, CancellationToken.None);
        Assert.Equal(2, requested.Count);
        Assert.True(completed.NetworkObserved);
        Assert.Equal(checkpoint.Capture.Pages[0].CapturedAt, completed.ObservedAt);
        Assert.Equal(completed.Checkpoint.Capture.Pages[^1].CapturedAt.AddMinutes(10), completed.NextRefreshAt);
        Assert.Equal(3, SourceFeedParser.ParseRequired(completed.Fetch.Content!, ProxyProtocol.Http).Count);
        var cached = await new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(owner,
            (_, _) => throw new InvalidOperationException("Completed capture must not request HTTP"), CancellationToken.None);
        Assert.False(cached.NetworkObserved);
        Assert.Equal(completed.Fetch.Content, cached.Fetch.Content);
        await using (var db = database.Factory.CreateDbContext())
        {
            Assert.True((await db.SourceApiCaptureStates.SingleAsync()).Complete);
            Assert.Null((await db.Sources.SingleAsync()).LastSucceededAt);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ProviderCooldownCannotDelayFreeProxyDbOrBeBypassedByRestart()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        ProxySource source;
        await using (var db = database.Factory.CreateDbContext())
        {
            source = new() { Name = "Public API cooldown fixture", Url = ProxiwarePublicApi.Url };
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }
        var freeGate = new SourceApiOriginGate(database.Factory);
        var proxiwareGate = new SourceApiOriginGate(database.Factory, ProxiwarePublicApi.Url);
        Assert.Equal(TimeSpan.FromSeconds(1), proxiwareGate.RequestInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), freeGate.RequestInterval);
        await using var freeLease = Assert.IsType<PostgresAdvisoryLock>(await freeGate.TryAcquireAsync(CancellationToken.None));
        await using (var lease = Assert.IsType<PostgresAdvisoryLock>(await proxiwareGate.TryAcquireAsync(CancellationToken.None)))
        {
            Assert.Null(await new SourceApiOriginGate(database.Factory, ProxiwarePublicApi.Url).TryAcquireAsync(CancellationToken.None));
            await proxiwareGate.ExtendDeadlineAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
            await proxiwareGate.ReserveRequestAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }
        Assert.Null(await freeGate.ReadDeadlineAsync(CancellationToken.None));
        var requests = 0;
        var exception = await Assert.ThrowsAsync<SourceApiDeferredException>(() =>
            new FreeProxyDbSourceApiFetcher(database.Factory).FetchAsync(SourceApiCaptureOwner.From(source), (_, _) =>
            {
                requests++;
                throw new InvalidOperationException("Cooldown must precede HTTP");
            }, CancellationToken.None));
        Assert.True(exception.NotBefore > DateTimeOffset.UtcNow.AddMinutes(59));
        Assert.Equal(0, requests);
    }
}
