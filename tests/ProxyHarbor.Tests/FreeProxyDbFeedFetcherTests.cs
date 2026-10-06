using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class FreeProxyDbFeedFetcherTests
{
    [Fact]
    public async Task FetchesEveryPageAndPreservesProtocolsWithoutPageValidators()
    {
        var requests = new List<string>();
        var bodies = new Queue<string>([
            Page(3, Row(1, "1.1.1.1", "http")),
            Page(3, Row(2, "8.8.8.8", "socks4")),
            Page(3, Row(3, "9.9.9.9", "socks5"))]);
        var result = await FreeProxyDbFeedFetcher.FetchAsync(100_000, (url, _) =>
        {
            requests.Add(url);
            return Task.FromResult(new SourceFetchResult(bodies.Dequeue(), false, "\"page\"", DateTimeOffset.UtcNow));
        }, CancellationToken.None);
        Assert.Equal(3, requests.Count);
        Assert.Contains("page_index=3&", requests[2]);
        Assert.Null(result.HttpETag);
        Assert.Null(result.HttpLastModifiedAt);
        Assert.False(result.NotModified);
        var candidates = SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http);
        Assert.Equal(3, candidates.Count);
        Assert.Contains(candidates, item => item.Protocol == ProxyProtocol.Socks4);
        Assert.Contains(candidates, item => item.Protocol == ProxyProtocol.Socks5);
    }

    [Fact]
    public async Task GrowingCountExtendsTraversalRatherThanKeepingInitialPagePlan()
    {
        var bodies = new Queue<string>([Page(2, Row(1)), Page(3, Row(2)), Page(3, Row(3))]);
        var result = await Fetch(bodies);
        Assert.Empty(bodies);
        Assert.Equal(3, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"status\":0,\"data\":{\"total_count\":1,\"data\":[]}}")]
    [InlineData("{\"status\":1,\"data\":null}")]
    [InlineData("{\"status\":1,\"data\":{\"total_count\":\"1\",\"data\":[]}}")]
    [InlineData("{\"status\":1,\"data\":{\"total_count\":-1,\"data\":[]}}")]
    [InlineData("{\"status\":1,\"data\":{\"total_count\":1,\"data\":[null]}}")]
    public async Task RejectsInvalidApiEnvelope(string body) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => Fetch(new Queue<string>([body])));

    [Fact]
    public async Task RejectsRepeatedPageEvenWhenMetadataChanges()
    {
        var bodies = new Queue<string>([Page(2, Row(1)), Page(2, Row(1, speed: 25))]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Fetch(bodies));
    }

    [Fact]
    public async Task DoesNotReturnSuccessfulPrefixWhenLaterPageFails()
    {
        var requests = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => FreeProxyDbFeedFetcher.FetchAsync(100_000, (_, _) =>
        {
            if (++requests == 2) throw new HttpRequestException("unavailable");
            return Task.FromResult(new SourceFetchResult(Page(2, Row(1)), false, null, null));
        }, CancellationToken.None));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task RejectsEmptyPrematureTailAndUnsolicited304()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Fetch(new Queue<string>([Page(2, Row(1)), Page(2)])));
        await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbFeedFetcher.FetchAsync(100_000,
            (_, _) => Task.FromResult(new SourceFetchResult(null, true, "\"page\"", null)), CancellationToken.None));
    }

    [Fact]
    public async Task EnforcesAggregateSizeNotJustIndividualPages()
    {
        var first = Page(2, Row(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbFeedFetcher.FetchAsync(first.Length + 10,
            (_, _) => Task.FromResult(new SourceFetchResult(first, false, null, null)), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsBeforeNextRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FreeProxyDbFeedFetcher.FetchAsync(100_000, (_, _) =>
        {
            requests++;
            cancellation.Cancel();
            return Task.FromResult(new SourceFetchResult(Page(2, Row(1)), false, null, null));
        }, cancellation.Token));
        Assert.Equal(1, requests);
    }

    [Fact]
    public void ApiPollingCannotBypassCooldownEvenForForcedCollection()
    {
        var now = DateTimeOffset.UtcNow;
        var next = SourceFetchSchedule.NextSuccessAttempt(FreeProxyDbFeedFetcher.Url, now);
        Assert.Equal(now.AddHours(6), next);
        Assert.False(SourceFetchSchedule.IsDue(next, now, true, FreeProxyDbFeedFetcher.Url));
        Assert.True(SourceFetchSchedule.IsDue(next, now.AddHours(6), true, FreeProxyDbFeedFetcher.Url));
        Assert.True(SourceFetchSchedule.IsDue(next, now, true, "https://example.com/feed"));
        Assert.False(FreeProxyDbFeedFetcher.Supports(FreeProxyDbFeedFetcher.Url + "&api_key=secret"));
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(429, false)]
    [InlineData(503, true)]
    public async Task RetryAfterDoesNotCauseEarlyRetry(int status, bool hasHeader)
    {
        var handler = new RateLimitedHandler(status, hasHeader);
        using var client = new HttpClient(handler);
        var now = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<SourceRateLimitException>(() => SourceHttpFetcher.FetchAsync(
            client, "https://1.1.1.1/feed", null, null, 1000, 2, 3, CancellationToken.None,
            respectRateLimit: true));
        Assert.Equal(1, handler.Requests);
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.False(SourceHttpRetry.IsRetryable(error, CancellationToken.None));
        Assert.True(error.RetryNotBefore >= now.AddMinutes(hasHeader ? 30 : 60));
        Assert.True(handler.ContentDisposed);
    }

    private static Task<SourceFetchResult> Fetch(Queue<string> bodies) => FreeProxyDbFeedFetcher.FetchAsync(100_000,
        (_, _) => Task.FromResult(new SourceFetchResult(bodies.Dequeue(), false, null, null)), CancellationToken.None);
    private static string Page(int total, params object[] rows) => JsonSerializer.Serialize(new
    {
        status = 1,
        data = new { total_count = total, data = rows }
    });
    private static object Row(int id, string? ip = null, string protocol = "http", int speed = 1) =>
        new { id, ip = ip ?? $"8.8.8.{id}", port = 8080, protocol, speed };

    private sealed class RateLimitedHandler(int status, bool hasHeader) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        internal bool ContentDisposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new TrackedContent(() => ContentDisposed = true) };
            if (hasHeader) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return Task.FromResult(response);
        }
    }
    private sealed class TrackedContent(Action disposed) : StringContent("rate limited")
    {
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }
}
