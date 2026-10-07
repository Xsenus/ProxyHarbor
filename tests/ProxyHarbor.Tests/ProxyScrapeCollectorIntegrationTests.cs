using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyScrapeCollectorIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgresIntegration")]
    public async Task FullCaptureDrainsThroughNormalQuotaEvenWhenRefreshIsRateLimited(bool rateLimitRefresh)
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("proxyscrape");
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.Where(item => item.Id == source.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.Url, ProxyScrapePublicApi.Url));
        using var handler = new ApiHandler();
        DateTimeOffset? firstSuccess = null;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            if (cycle == 1 && rateLimitRefresh)
            {
                await using var db = database.Factory.CreateDbContext();
                await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextFetchAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
                await db.SourceApiOriginStates.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NotBefore, DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
            using var collector = new ProxyCollector(database.Factory, new ApiClients(handler),
                Options.Create(new CollectorOptions { MaxProxiesPerSource = 1, MaxCandidatesPerRun = 10, SourceRetryCount = 2 }),
                NullLogger<ProxyCollector>.Instance);
            var run = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);
            Assert.Equal(1, run.NewProxies);
            await using var verify = database.Factory.CreateDbContext();
            var state = await verify.ProxySourceImportStates.SingleAsync();
            Assert.Equal(4, state.CandidateCount);
            Assert.Equal(cycle + 1, state.NextIndex);
            Assert.Equal(cycle + 1, await verify.Proxies.CountAsync());
            var health = await verify.Sources.SingleAsync();
            firstSuccess ??= health.LastSucceededAt;
            Assert.NotNull(firstSuccess);
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
                Assert.True(health.NextFetchAt > DateTimeOffset.UtcNow.AddMinutes(4));
            }
            if (cycle == 3) Assert.Empty(state.Payload);
        }
        Assert.Equal(rateLimitRefresh ? 5 : 4, handler.Requests);
        await using var final = database.Factory.CreateDbContext();
        Assert.Equal(3, await final.Proxies.Select(proxy => proxy.Protocol).Distinct().CountAsync());
    }

    private sealed class ApiClients(ApiHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ApiHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            if (Requests > 4)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("limited") };
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
                return Task.FromResult(limited);
            }
            var expectedOffset = Requests switch { 1 => 0, 2 => 2000, 3 => 4000, _ => 0 };
            Assert.Contains($"skip={expectedOffset}&", request.RequestUri!.Query);
            var body = Requests switch
            {
                1 => ProxyScrapePublicApiTests.Page(0, 3, true, Row("8.8.8.8"), Row("1.1.1.1", "socks4")),
                2 => ProxyScrapePublicApiTests.Page(2000, 2000, false, Row("9.9.9.9", "socks5")),
                3 => ProxyScrapePublicApiTests.Page(4000, 3, false),
                _ => ProxyScrapePublicApiTests.Page(0, 4, true, Row("8.8.8.8"), Row("208.67.222.222"))
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Headers.ETag = new EntityTagHeaderValue("\"page-only\"");
            return Task.FromResult(response);
        }
        private static object Row(string host, string protocol = "http") => new { ip = host, port = 1080, protocol };
    }
}
