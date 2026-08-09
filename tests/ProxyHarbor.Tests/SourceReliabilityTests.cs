using System.Net;
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
            SourceFeedParser.ParseRequired("<html><body>temporarily unavailable</body></html>", ProxyProtocol.Http));

        Assert.Contains("не содержит", exception.Message, StringComparison.Ordinal);
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
        var parsed = SourceFeedParser.ParseRequired(
            "1.1.1.1:80\n8.8.8.8:81\n9.9.9.9:82",
            ProxyProtocol.Http,
            maxResults: 2);

        Assert.Equal(2, parsed.Count);
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
}
