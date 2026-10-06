using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;
using SnapshotDatabase = ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests.SnapshotDatabase;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class FreeProxyDbCollectorIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgresIntegration")]
    public async Task CompleteApiListContinuesFromCacheWhileHttpCooldownIsHonored(bool rateLimitRefresh)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("freeproxydb");
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.Where(item => item.Id == source.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.Url, FreeProxyDbFeedFetcher.Url));
        using var handler = new ApiHandler(rateLimitRefresh);
        var clients = new ApiClients(handler);
        DateTimeOffset? firstSuccess = null;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            if (cycle == 1 && rateLimitRefresh)
            {
                await using var db = database.Factory.CreateDbContext();
                await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextFetchAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
            using var collector = new ProxyCollector(database.Factory, clients,
                Options.Create(new CollectorOptions { MaxProxiesPerSource = 1, MaxCandidatesPerRun = 10, SourceRetryCount = 2 }),
                NullLogger<ProxyCollector>.Instance);
            var run = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);
            Assert.Equal(1, run.NewProxies);
            await using var verify = database.Factory.CreateDbContext();
            var state = await verify.ProxySourceImportStates.SingleAsync();
            Assert.Equal(3, state.CandidateCount);
            Assert.Equal(cycle + 1, state.NextIndex);
            Assert.Equal(cycle + 1, await verify.Proxies.CountAsync());
            var health = await verify.Sources.SingleAsync();
            firstSuccess ??= health.LastSucceededAt;
            Assert.Equal(firstSuccess, health.LastSucceededAt);
            Assert.Null(health.HttpETag);
            if (rateLimitRefresh && cycle > 0)
            {
                Assert.NotNull(health.LastError);
                Assert.Equal(1, health.ConsecutiveFailures);
                Assert.True(health.NextFetchAt > DateTimeOffset.UtcNow.AddMinutes(29));
            }
            else
            {
                Assert.Null(health.LastError);
                Assert.True(health.NextFetchAt > DateTimeOffset.UtcNow.AddHours(5));
            }
            if (cycle == 2) Assert.Empty(state.Payload);
        }
        // Forced collection cannot defeat provider cooldown, including Retry-After.
        Assert.Equal(rateLimitRefresh ? 4 : 2, handler.Requests);
    }

    private sealed class ApiClients(ApiHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class ApiHandler(bool rateLimitRefresh) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            if (Requests == 4 && rateLimitRefresh)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("limited") };
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
                return Task.FromResult(limited);
            }
            var first = request.RequestUri!.Query.Contains("page_index=1&", StringComparison.Ordinal);
            object[] rows = first
                ? [new { id = 1, ip = "8.8.8.8", port = 80, protocol = "http" }, new { id = 2, ip = "1.1.1.1", port = 1080, protocol = "socks4" }]
                : [new { id = 3, ip = "9.9.9.9", port = 1080, protocol = "socks5" }];
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { status = 1, data = new { total_count = 3, data = rows } }))
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"page-only\"");
            return Task.FromResult(response);
        }
    }
}
