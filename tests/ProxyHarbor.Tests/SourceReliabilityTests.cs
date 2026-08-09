using System.Net;
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

    private sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;
        internal int Requests => _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index) - 1;
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
}
