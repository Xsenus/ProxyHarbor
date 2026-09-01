using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует семантическую проверку feed, retry-классификацию и bounded backoff.</summary>
public sealed class SourceReliabilityTests
{
    [Fact]
    public void ParserRejectsSuccessfulHttpResponseWithoutAnyProxy()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SourceFeedParser.ParseRequired("temporarily unavailable", ProxyProtocol.Http));

        Assert.Contains("не содержит", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!doctype html><html>8.8.8.8:8080</html>")]
    [InlineData("\uFEFF  <BODY>Cloud WAF at 8.8.8.8:8080</BODY>")]
    [InlineData("<!-- Cloud WAF challenge -->\n<html>8.8.8.8:8080</html>")]
    [InlineData("<meta charset=\"utf-8\"><title>Blocked at 8.8.8.8:8080</title>")]
    [InlineData("<title>Proxy error 8.8.8.8:8080</title>")]
    [InlineData("<script>const diagnostic = '8.8.8.8:8080';</script>")]
    public void ParserRejectsHtmlEnvelopeEvenWhenItContainsProxyLikeText(string content)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SourceFeedParser.ParseBoundedRequired(content, ProxyProtocol.Http, maxResults: 100));

        Assert.Contains("HTML", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("TEXT/HTML")]
    [InlineData("application/xhtml+xml")]
    public void HtmlMediaTypesAreRejected(string mediaType)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SourceFeedParser.EnsureSupportedMediaType(mediaType));

        Assert.Contains("HTML", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    [InlineData("application/octet-stream")]
    public void FeedMediaTypesRemainCompatible(string? mediaType) =>
        SourceFeedParser.EnsureSupportedMediaType(mediaType);

    [Fact]
    public async Task FetchRejectsHtmlContentTypeBeforeCandidateParsing()
    {
        var content = new StringContent("<html>8.8.8.8:8080</html>");
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(
            null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
            NullLogger<ProxyCollector>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            collector.FetchSourceAsync(client, "https://1.1.1.1/feed", CancellationToken.None));

        Assert.Contains("HTML", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void ParserAcceptsAtLeastOneValidProxy()
    {
        var parsed = SourceFeedParser.ParseRequired("ignored\n1.2.3.4:8080\n", ProxyProtocol.Http);

        var proxy = Assert.Single(parsed);
        Assert.Equal(("1.2.3.4", 8080, ProxyProtocol.Http), proxy);
    }

    [Fact]
    public void FeedParserAppliesLimitBeforeReturningCandidates()
    {
        var parsed = SourceFeedParser.ParseBoundedRequired(
            "1.1.1.1:80\n8.8.8.8:81\n9.9.9.9:82",
            ProxyProtocol.Http,
            maxResults: 2);

        Assert.Equal(2, parsed.Items.Count);
        Assert.True(parsed.Truncated);
    }

    [Fact]
    public void FeedParserDoesNotCallDuplicateTailTruncation()
    {
        var parsed = SourceFeedParser.ParseBoundedRequired(
            "1.1.1.1:80\n8.8.8.8:81\n1.1.1.1:80\n8.8.8.8:81",
            ProxyProtocol.Http,
            maxResults: 2);

        Assert.Equal(2, parsed.Items.Count);
        Assert.False(parsed.Truncated);
    }

    [Fact]
    public void CollectorFeedPathStreamsCandidatesWithoutAResultList()
    {
        var accepted = new List<ProxyCandidateKey>();

        var parsed = SourceFeedParser.ParseBoundedToRequired(
            "1.1.1.1:80\n8.8.8.8:81\n1.1.1.1:80",
            ProxyProtocol.Http,
            maxResults: 2,
            accepted.Add);

        Assert.Equal(new ProxyParseSummary(2, Truncated: false), parsed);
        Assert.Equal(2, accepted.Count);
    }

    [Fact]
    public void RetryPolicyDoesNotRepeatPermanentHttpStatus()
    {
        var exception = new HttpRequestException("not found", null, HttpStatusCode.NotFound);

        Assert.False(SourceHttpRetry.IsRetryable(exception, CancellationToken.None));
        Assert.True(SourceHttpRetry.IsRetryable(new HttpRequestException("connection reset"), CancellationToken.None));
        Assert.True(SourceHttpRetry.IsRetryable(new TaskCanceledException("timeout"), CancellationToken.None));
    }

    [Fact]
    public void RetryPolicyHonorsOuterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(SourceHttpRetry.IsRetryable(new HttpRequestException("network"), cancellation.Token));
        Assert.False(SourceHttpRetry.IsRetryable(new TaskCanceledException("timeout"), cancellation.Token));
    }

    [Fact]
    public async Task TransientHttpResponseIsDisposedBeforeRetryBackoff()
    {
        var firstContent = new DisposeTrackingContent();
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = firstContent },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("8.8.8.8:8080") });
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(
            null!, null!,
            Options.Create(new CollectorOptions { SourceRetryCount = 1, SourceTimeoutSeconds = 2 }),
            NullLogger<ProxyCollector>.Instance);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var fetch = collector.FetchSourceAsync(
            client,
            "https://1.1.1.1/feed.txt",
            CancellationToken.None,
            (_, _) =>
            {
                delayStarted.TrySetResult();
                return releaseDelay.Task;
            });
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposedBeforeBackoff = firstContent.Disposed.IsCompletedSuccessfully;
        releaseDelay.TrySetResult();
        Assert.True(disposedBeforeBackoff);
        Assert.Equal("8.8.8.8:8080", await fetch);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task ConditionalRequestAccepts304AndRefreshesResponseValidators()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotModified);
        response.Headers.ETag = new EntityTagHeaderValue("\"feed-v2\"");
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var handler = new SequencedHandler(response);
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(
            null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
            NullLogger<ProxyCollector>.Instance);
        var previousModifiedAt = new DateTimeOffset(2026, 8, 9, 11, 0, 0, TimeSpan.Zero);

        var result = await collector.FetchSourceStateAsync(
            client,
            "https://1.1.1.1/feed.txt",
            "W/\"feed-v1\"",
            previousModifiedAt,
            CancellationToken.None);

        Assert.True(result.NotModified);
        Assert.Null(result.Content);
        Assert.Equal("\"feed-v2\"", result.HttpETag);
        Assert.Equal(response.Content.Headers.LastModified, result.HttpLastModifiedAt);
        Assert.Equal("W/\"feed-v1\"", handler.LastIfNoneMatch);
        Assert.Equal(previousModifiedAt, handler.LastIfModifiedSince);
    }

    [Fact]
    public async Task Unsolicited304AndMalformedStoredETagAreRejected()
    {
        var handler = new SequencedHandler(new HttpResponseMessage(HttpStatusCode.NotModified));
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(
            null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
            NullLogger<ProxyCollector>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() => collector.FetchSourceStateAsync(
            client, "https://1.1.1.1/feed.txt", null, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => collector.FetchSourceStateAsync(
            client, "https://1.1.1.1/feed.txt", "not-an-etag", null, CancellationToken.None));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task SuccessfulResponseReturnsBoundedCacheValidators()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("8.8.8.8:8080")
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"feed-v1\"");
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
        var handler = new SequencedHandler(response);
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(
            null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
            NullLogger<ProxyCollector>.Instance);

        var result = await collector.FetchSourceStateAsync(
            client, "https://1.1.1.1/feed.txt", null, null, CancellationToken.None);

        Assert.Equal("8.8.8.8:8080", result.Content);
        Assert.False(result.NotModified);
        Assert.Equal("\"feed-v1\"", result.HttpETag);
        Assert.Equal(response.Content.Headers.LastModified, result.HttpLastModifiedAt);
    }

    [Fact]
    public async Task UnsafeStoredLastModifiedIsNotSentAndCannotAuthorize304()
    {
        var handler = new SequencedHandler(new HttpResponseMessage(HttpStatusCode.NotModified));
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(
            null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
            NullLogger<ProxyCollector>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() => collector.FetchSourceStateAsync(
            client,
            "https://1.1.1.1/feed.txt",
            httpETag: null,
            DateTimeOffset.MaxValue,
            CancellationToken.None));

        Assert.Null(handler.LastIfModifiedSince);
    }

    [Fact]
    public async Task UnsafeResponseLastModifiedIsNeverPersisted()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("8.8.8.8:8080")
        };
        response.Content.Headers.LastModified = DateTimeOffset.MaxValue;
        var handler = new SequencedHandler(response);
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(
            null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
            NullLogger<ProxyCollector>.Instance);

        var result = await collector.FetchSourceStateAsync(
            client, "https://1.1.1.1/feed.txt", null, null, CancellationToken.None);

        Assert.Equal("8.8.8.8:8080", result.Content);
        Assert.Null(result.HttpLastModifiedAt);
    }

    [Fact]
    public async Task ConditionalValidatorsNeverCrossRedirectOrPersistForRedirectedRepresentation()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://8.8.4.4/final.txt");
        var final = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("8.8.8.8:8080")
        };
        final.Headers.ETag = new EntityTagHeaderValue("\"target-etag\"");
        final.Content.Headers.LastModified = new DateTimeOffset(2026, 8, 10, 1, 0, 0, TimeSpan.Zero);
        var handler = new RedirectRecordingHandler(redirect, final);
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(
            null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
            NullLogger<ProxyCollector>.Instance);
        var sourceModifiedAt = new DateTimeOffset(2026, 8, 9, 1, 0, 0, TimeSpan.Zero);

        var result = await collector.FetchSourceStateAsync(
            client,
            "https://1.1.1.1/source.txt",
            "\"source-etag\"",
            sourceModifiedAt,
            CancellationToken.None);

        Assert.Equal("8.8.8.8:8080", result.Content);
        Assert.False(result.NotModified);
        Assert.Null(result.HttpETag);
        Assert.Null(result.HttpLastModifiedAt);
        Assert.Collection(handler.Snapshots,
            initial =>
            {
                Assert.Equal("1.1.1.1", initial.Uri.Host);
                Assert.Equal("\"source-etag\"", initial.IfNoneMatch);
                Assert.Equal(sourceModifiedAt, initial.IfModifiedSince);
            },
            redirected =>
            {
                Assert.Equal("8.8.4.4", redirected.Uri.Host);
                Assert.Null(redirected.IfNoneMatch);
                Assert.Null(redirected.IfModifiedSince);
            });
    }

    [Fact]
    public async Task RedirectedUnsolicited304IsRejected()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        redirect.Headers.Location = new Uri("https://8.8.4.4/final.txt");
        var handler = new RedirectRecordingHandler(
            redirect,
            new HttpResponseMessage(HttpStatusCode.NotModified));
        using var client = new HttpClient(handler);
        using var collector = new ProxyCollector(
            null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
            NullLogger<ProxyCollector>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            collector.FetchSourceStateAsync(
                client,
                "https://1.1.1.1/source.txt",
                "\"source-etag\"",
                null,
                CancellationToken.None));

        Assert.Contains("Redirect-target", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.Snapshots[1].IfNoneMatch);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(8, 1_440)]
    [InlineData(100, 1_440)]
    public void BackoffGrowsExponentiallyAndStopsAtMaximum(int failures, int expectedMinutes)
    {
        var failedAt = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);

        var next = SourceFetchSchedule.NextAttempt(failedAt, failures, baseMinutes: 15, maxHours: 24);

        Assert.Equal(failedAt.AddMinutes(expectedMinutes), next);
    }

    [Fact]
    public void ManualAuditBypassesBackoffButWorkerWaits()
    {
        var now = DateTimeOffset.UtcNow;
        var next = now.AddHours(1);

        Assert.False(SourceFetchSchedule.IsDue(next, now, forceAllSources: false));
        Assert.True(SourceFetchSchedule.IsDue(next, now, forceAllSources: true));
        Assert.True(SourceFetchSchedule.IsDue(null, now, forceAllSources: false));
    }

    [Theory]
    [InlineData(3, -23, true)]
    [InlineData(3, -24, false)]
    [InlineData(1, -11, true)]
    [InlineData(1, -12, false)]
    [InlineData(3, 1, false)]
    public void ConditionalValidatorsExpireBeforeDeadRetentionCanLoseMembership(
        int retentionDays,
        int contentAgeHours,
        bool expected)
    {
        var now = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var lastSucceededAt = now.AddMinutes(-5);

        Assert.Equal(expected, SourceConditionalFetchPolicy.ShouldUseValidators(
            now.AddHours(contentAgeHours), lastSucceededAt, 1, now, retentionDays));
        Assert.False(SourceConditionalFetchPolicy.ShouldUseValidators(
            null, lastSucceededAt, 1, now, retentionDays));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public void ConditionalValidatorsRequireReusableSuccessfulResult(
        bool hasSuccessfulFetch,
        int lastItemCount)
    {
        var now = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        Assert.False(SourceConditionalFetchPolicy.ShouldUseValidators(
            now.AddMinutes(-5),
            hasSuccessfulFetch ? now.AddMinutes(-5) : null,
            lastItemCount,
            now,
            deadRetentionDays: 3));
    }

    private sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;
        internal int Requests => _index;
        internal string? LastIfNoneMatch { get; private set; }
        internal DateTimeOffset? LastIfModifiedSince { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            LastIfNoneMatch = request.Headers.IfNoneMatch.SingleOrDefault()?.ToString();
            LastIfModifiedSince = request.Headers.IfModifiedSince;
            return Task.FromResult(responses[index]);
        }
    }

    private sealed class DisposeTrackingContent : ByteArrayContent
    {
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Disposed => _disposed.Task;

        internal DisposeTrackingContent() : base([]) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class RedirectRecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;
        internal List<RequestSnapshot> Snapshots { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Snapshots.Add(new RequestSnapshot(
                request.RequestUri!,
                request.Headers.IfNoneMatch.SingleOrDefault()?.ToString(),
                request.Headers.IfModifiedSince));
            return Task.FromResult(responses[_index++]);
        }
    }

    private sealed record RequestSnapshot(
        Uri Uri,
        string? IfNoneMatch,
        DateTimeOffset? IfModifiedSince);
}
