using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class HideIpConnectFeedAdapterTests
{
    private const string Url = "https://raw.githubusercontent.com/zloi-user/hideip.me/main/connect.txt";

    [Theory]
    [InlineData(Url)]
    [InlineData("https://raw.githubusercontent.com/zloi-user/hideip.me/refs/heads/main/connect.txt")]
    public void ReadsOnlyPublicCanonicalEndpointsAndRetainsConnectProtocol(string url)
    {
        var converted = HideIpConnectFeedAdapter.Extract(url,
            "\uFEFF8.8.8.8:8080:United States\r\n1.1.1.1:3128:Türkiye\n\n" +
            "127.0.0.1:80:Local\n10.0.0.1:80:Private\n010.0.0.1:80:Octal\n8.8.4.4:0:Invalid\n" +
            "8.8.8.8:8080:United States\n");
        var parsed = SourceFeedParser.ParseRequired(converted, ProxyProtocol.Socks5);
        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, endpoint => Assert.Equal(ProxyProtocol.Https, endpoint.Protocol));
        Assert.Contains(parsed, endpoint => endpoint.Host == "8.8.8.8" && endpoint.Port == 8080);
        Assert.Contains(parsed, endpoint => endpoint.Host == "1.1.1.1" && endpoint.Port == 3128);
        Assert.Equal(2, ProxyCandidateSnapshotCodec.Encode(converted, ProxyProtocol.Http).Count);
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/zloi-user/hideip.me/main/connect.txt")]
    [InlineData("https://raw.githubusercontent.com:444/zloi-user/hideip.me/main/connect.txt")]
    [InlineData("https://raw.githubusercontent.com.evil.test/zloi-user/hideip.me/main/connect.txt")]
    [InlineData("https://user@raw.githubusercontent.com/zloi-user/hideip.me/main/connect.txt")]
    [InlineData(Url + "?token=example")]
    [InlineData(Url + "#fragment")]
    [InlineData("https://raw.githubusercontent.com/zloi-user/hideip.me/main/http.txt")]
    [InlineData("https://raw.githubusercontent.com/other/project/main/connect.txt")]
    public void DoesNotTransformOtherUrls(string url)
    {
        Assert.False(HideIpConnectFeedAdapter.Supports(url));
        Assert.Throws<InvalidDataException>(() => HideIpConnectFeedAdapter.Extract(url, "8.8.8.8:80:Country"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:80:Country")]
    [InlineData("8.8.8.8:80:user:password")]
    [InlineData("8.8.8.8:80:https://example.com")]
    [InlineData("8.8.8.8:80:")]
    [InlineData("<html>8.8.8.8:80:Country</html>")]
    [InlineData("8.8.8.8:80:Country\nmalformed")]
    public void RejectsEmptyPrivateCredentialAndUnknownEnvelopes(string content) =>
        Assert.Throws<InvalidDataException>(() => HideIpConnectFeedAdapter.Extract(Url, content));

    [Fact]
    public async Task CollectorTransformsBodyAndPreservesValidators()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("8.8.8.8:8080:United States") };
        response.Headers.ETag = new EntityTagHeaderValue("\"country-feed\"");
        using var client = new HttpClient(new Handler(response));
        using var collector = CreateCollector();
        var result = await collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None);
        Assert.Equal("https://8.8.8.8:8080\n", result.Content);
        Assert.Equal("\"country-feed\"", result.HttpETag);
        Assert.False(result.NotModified);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddMinutes(10), SourceFetchSchedule.NextSuccessAttempt(Url, now));
    }

    [Fact]
    public async Task CollectorPreserves304WithoutConvertingMissingBody()
    {
        using var client = new HttpClient(new Handler(new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var collector = CreateCollector();
        var result = await collector.FetchSourceStateAsync(client, Url, "\"previous\"", null, CancellationToken.None);
        Assert.True(result.NotModified);
        Assert.Null(result.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    public async Task CollectorHonorsRateLimitsAndRejectsForeignRedirects(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);
        if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://1.1.1.1/other.txt");
        else response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));
        using var client = new HttpClient(new Handler(response));
        using var collector = CreateCollector();
        if (status == HttpStatusCode.Found)
            await Assert.ThrowsAsync<HttpRequestException>(() => collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None));
        else
            await Assert.ThrowsAsync<SourceRateLimitException>(() => collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None));
    }

    private static ProxyCollector CreateCollector() => new(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
