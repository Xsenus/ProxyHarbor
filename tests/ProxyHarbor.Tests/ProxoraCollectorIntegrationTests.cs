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
public sealed class ProxoraCollectorIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgresIntegration")]
    public async Task PartialPagesNeverPublishWhileCompletedSnapshotImportsDuringCooldown(bool rateLimitRefresh)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("proxora-public");
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.Where(item => item.Id == source.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.Url, ProxoraPublicApi.Url));
        using var handler = new ApiHandler(rateLimitRefresh);
        var clients = new ApiClients(handler);
        DateTimeOffset? firstSuccess = null;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            if (cycle is 1 or 2 || (cycle == 3 && rateLimitRefresh))
            {
                // Emulate elapsed pacing in this isolated test database; production never bypasses it.
                await using var db = database.Factory.CreateDbContext();
                await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextFetchAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
                await db.SourceApiOriginStates.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NotBefore, DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
            using var collector = new ProxyCollector(database.Factory, clients,
                Options.Create(new CollectorOptions { MaxProxiesPerSource = 1, MaxCandidatesPerRun = 10, SourceRetryCount = 2 }),
                NullLogger<ProxyCollector>.Instance);
            var run = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);
            await using var verify = database.Factory.CreateDbContext();
            var health = await verify.Sources.SingleAsync();
            Assert.Null(health.HttpETag);
            if (cycle < 2)
            {
                Assert.Equal(0, run.NewProxies);
                Assert.Empty(await verify.ProxySourceImportStates.ToArrayAsync());
                Assert.Null(health.LastSucceededAt);
                Assert.Null(health.LastError);
                Assert.Equal(0, health.ConsecutiveFailures);
                Assert.False((await verify.SourceApiCaptureStates.SingleAsync()).Complete);
                continue;
            }
            Assert.Equal(1, run.NewProxies);
            var state = await verify.ProxySourceImportStates.SingleAsync();
            Assert.Equal(3, state.CandidateCount);
            Assert.Equal(cycle - 1, state.NextIndex);
            Assert.Equal(cycle - 1, await verify.Proxies.CountAsync());
            firstSuccess ??= health.LastSucceededAt;
            Assert.NotNull(firstSuccess);
            Assert.Equal(firstSuccess, health.LastSucceededAt);
            if (rateLimitRefresh && cycle > 2)
            {
                Assert.NotNull(health.LastError);
                Assert.Equal(1, health.ConsecutiveFailures);
                Assert.True(health.NextFetchAt > DateTimeOffset.UtcNow.AddMinutes(59));
            }
            else
            {
                Assert.Null(health.LastError);
                Assert.True(health.NextFetchAt > DateTimeOffset.UtcNow.AddMinutes(4));
            }
            if (cycle == 4) Assert.Empty(state.Payload);
        }
        Assert.Equal(rateLimitRefresh ? 4 : 3, handler.Requests);
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
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return Task.FromResult(limited);
            }
            var first = request.RequestUri!.Query.Contains("page=1&", StringComparison.Ordinal);
            object[] items = first
                ? [new { host = "8.8.8.8", port = 80, protocol = "http" }, new { host = "1.1.1.1", port = 1080, protocol = "socks4" }]
                : [new { host = "9.9.9.9", port = 1080, protocol = "socks5" }];
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { total = 3, page = first ? 1 : 2, per_page = 500, items }))
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"page-only\"");
            return Task.FromResult(response);
        }
    }
}
