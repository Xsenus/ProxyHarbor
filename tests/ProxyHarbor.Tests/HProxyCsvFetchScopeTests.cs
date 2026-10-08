using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class HProxyCsvFetchScopeTests
{
    private const string Csv = "ip,port,protocols,anonymity,country,city,latency_ms,uptime_pct,alive,uptime_24h,uptime_7d,reliability\n8.8.8.8,8080,http|https,elite,US,City,1,99,true,99,99,99\n1.1.1.1,1080,socks5,elite,DE,City,1,99,true,99,99,99\n";

    [Fact]
    public async Task ConcurrentCountrySourcesShareOneFullFetchAndTheirOwnProjection()
    {
        using var scope = new HProxyCsvFetchScope();
        var count = 0;
        async Task<SourceFetchResult> Fetch(string? etag, DateTimeOffset? modified, CancellationToken token)
        {
            Interlocked.Increment(ref count);
            await Task.Yield();
            return new(Csv, false, "\"new\"", null);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 80).Select(index =>
            scope.FetchAsync(HProxyCsvFeedAdapter.Url + (index % 2 == 0 ? "?country=US" : "?country=DE"),
                null, null, Fetch, CancellationToken.None)));
        Assert.Equal(1, count);
        for (var index = 0; index < results.Length; index++)
            Assert.Equal(index % 2 == 0 ? 2 : 1, SourceFeedParser.ParseRequired(results[index].Content!, ProxyProtocol.Http).Count);
    }

    [Fact]
    public async Task NotModifiedIsSharedOnlyWithMatchingValidatorsAndFullBodyFetchIsShared()
    {
        using var scope = new HProxyCsvFetchScope();
        var sent = new List<(string?, DateTimeOffset?)>();
        var date = DateTimeOffset.UtcNow.AddDays(-1);
        Task<SourceFetchResult> Fetch(string? etag, DateTimeOffset? modified, CancellationToken token)
        {
            sent.Add((etag, modified));
            return Task.FromResult(etag is null ? new SourceFetchResult(Csv, false, "\"new\"", null) : new(null, true, etag, modified));
        }
        Assert.True((await scope.FetchAsync(HProxyCsvFeedAdapter.Url, "\"old\"", date, Fetch, CancellationToken.None)).NotModified);
        Assert.True((await scope.FetchAsync(HProxyCsvFeedAdapter.Url + "?country=US", "\"old\"", date, Fetch, CancellationToken.None)).NotModified);
        Assert.False((await scope.FetchAsync(HProxyCsvFeedAdapter.Url + "?country=DE", "\"old\"", null, Fetch, CancellationToken.None)).NotModified);
        Assert.False((await scope.FetchAsync(HProxyCsvFeedAdapter.Url, null, null, Fetch, CancellationToken.None)).NotModified);
        Assert.Equal([("\"old\"", (DateTimeOffset?)date), (null, null)], sent);
    }

    [Fact]
    public async Task PublisherFailureIsSharedButEmptyCountryDoesNotPoisonOthers()
    {
        using var failed = new HProxyCsvFetchScope();
        var count = 0;
        Task<SourceFetchResult> Fail(string? etag, DateTimeOffset? modified, CancellationToken token)
        {
            count++;
            throw new HttpRequestException("Publisher unavailable");
        }
        await Assert.ThrowsAsync<HttpRequestException>(() => failed.FetchAsync(HProxyCsvFeedAdapter.Url, null, null, Fail, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => failed.FetchAsync(HProxyCsvFeedAdapter.Url + "?country=US", null, null, Fail, CancellationToken.None));
        Assert.Equal(1, count);
        using var scope = new HProxyCsvFetchScope();
        Task<SourceFetchResult> Success(string? etag, DateTimeOffset? modified, CancellationToken token) => Task.FromResult(new SourceFetchResult(Csv, false, null, null));
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.FetchAsync(HProxyCsvFeedAdapter.Url + "?country=FR", null, null, Success, CancellationToken.None));
        Assert.False((await scope.FetchAsync(HProxyCsvFeedAdapter.Url, null, null, Success, CancellationToken.None)).NotModified);
    }

    [Fact]
    public async Task CollectorUsesCanonicalPublisherUrlAndPreservesConditionalHeaders()
    {
        var requests = new List<(string, string?)>();
        using var handler = new Handler(request =>
        {
            requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.IfNoneMatch.SingleOrDefault()?.Tag));
            var response = new HttpResponseMessage(requests.Count == 1 ? HttpStatusCode.OK : HttpStatusCode.NotModified);
            if (requests.Count == 1) response.Content = new StringContent(Csv);
            response.Headers.ETag = new EntityTagHeaderValue("\"version\"");
            return response;
        });
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
        var url = HProxyCsvFeedAdapter.Url + "?country=US";
        var result = await collector.FetchSourceStateAsync(client, url, null, null, CancellationToken.None);
        Assert.Equal(2, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
        var cached = await collector.FetchSourceStateAsync(client, url, result.HttpETag, null, CancellationToken.None);
        Assert.True(cached.NotModified);
        Assert.Equal([(HProxyCsvFeedAdapter.Url, (string?)null), (HProxyCsvFeedAdapter.Url, "\"version\"")], requests);
    }

    [Fact]
    public async Task CancellationDoesNotPoisonTheScopeAndUnconditionalNotModifiedIsRejected()
    {
        using var scope = new HProxyCsvFetchScope();
        var count = 0;
        Task<SourceFetchResult> Fetch(string? etag, DateTimeOffset? modified, CancellationToken token)
        {
            count++;
            return Task.FromResult(new SourceFetchResult(Csv, false, null, null));
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.FetchAsync(HProxyCsvFeedAdapter.Url, null, null, Fetch, cancelled.Token));
        Assert.Equal(0, count);
        Assert.False((await scope.FetchAsync(HProxyCsvFeedAdapter.Url, null, null, Fetch, CancellationToken.None)).NotModified);
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => collector.FetchSourceStateAsync(client, HProxyCsvFeedAdapter.Url, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task CollectorRejectsCrossOriginRedirectAndHonorsRetryAfter()
    {
        foreach (var status in new[] { HttpStatusCode.Found, HttpStatusCode.TooManyRequests })
        {
            using var handler = new Handler(_ =>
            {
                var response = new HttpResponseMessage(status);
                if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://1.1.1.1/list");
                else response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return response;
            });
            using var client = new HttpClient(handler);
            using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
            if (status == HttpStatusCode.Found)
                await Assert.ThrowsAsync<HttpRequestException>(() => collector.FetchSourceStateAsync(client, HProxyCsvFeedAdapter.Url, null, null, CancellationToken.None));
            else
                await Assert.ThrowsAsync<SourceRateLimitException>(() => collector.FetchSourceStateAsync(client, HProxyCsvFeedAdapter.Url, null, null, CancellationToken.None));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
