using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class LitportHttpsFeedAdapterTests
{
    private const string Url = "https://raw.githubusercontent.com/litportnet/free-proxy-list/live/proxies/https.json";
    private const string Row = """{"ip":"8.8.8.8","port":8080,"protocol":"http","https":true,"url":"http://8.8.8.8:8080"}""";
    private const string Body = "[" + Row + "]";

    [Theory]
    [InlineData(Url)]
    [InlineData("https://raw.githubusercontent.com/litportnet/free-proxy-list/main/proxies/https.json")]
    [InlineData("https://raw.githubusercontent.com/litportnet/free-proxy-list/refs/heads/live/proxies/https.json")]
    [InlineData("https://raw.githubusercontent.com/litportnet/free-proxy-list/refs/heads/main/proxies/https.json")]
    public void PreservesSocksAndMapsOnlyHttpToConnect(string url)
    {
        var socks4 = Row.Replace("\"http\"", "\"socks4\"", StringComparison.Ordinal).Replace("http://", "socks4://", StringComparison.Ordinal);
        var socks5 = Row.Replace("\"http\"", "\"socks5\"", StringComparison.Ordinal).Replace("http://", "socks5://", StringComparison.Ordinal);
        var converted = LitportHttpsFeedAdapter.Extract(url, "\uFEFF[" + Row + "," + socks4 + "," + socks5 + "," + Row + "]");
        var parsed = SourceFeedParser.ParseRequired(converted, ProxyProtocol.Http);
        Assert.Equal(new[] { ProxyProtocol.Https, ProxyProtocol.Socks4, ProxyProtocol.Socks5 }, parsed.Select(p => p.Protocol).Order());
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/litportnet/free-proxy-list/live/proxies/https.json")]
    [InlineData("https://raw.githubusercontent.com:444/litportnet/free-proxy-list/live/proxies/https.json")]
    [InlineData("https://raw.githubusercontent.com.evil.test/litportnet/free-proxy-list/live/proxies/https.json")]
    [InlineData("https://user@raw.githubusercontent.com/litportnet/free-proxy-list/live/proxies/https.json")]
    [InlineData(Url + "?key=example")]
    [InlineData(Url + "#fragment")]
    [InlineData("https://raw.githubusercontent.com/litportnet/free-proxy-list/live/proxies/all.json")]
    public void RejectsUnrelatedUrls(string url)
    {
        Assert.False(LitportHttpsFeedAdapter.Supports(url));
        Assert.Throws<InvalidDataException>(() => LitportHttpsFeedAdapter.Extract(url, Body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[\"8.8.8.8:8080\"]")]
    [InlineData("<html>8.8.8.8:8080</html>")]
    public void RejectsMalformedFeed(string body) =>
        Assert.Throws<InvalidDataException>(() => LitportHttpsFeedAdapter.Extract(Url, body));

    [Theory]
    [InlineData("\"https\":true", "\"https\":false")]
    [InlineData("\"https\":true", "\"https\":\"true\"")]
    [InlineData("\"https\":true", "\"https\":true,\"https\":false")]
    [InlineData("\"protocol\":\"http\"", "\"protocol\":\"https\"")]
    [InlineData("\"protocol\":\"http\"", "\"protocol\":\"socks5\"")]
    [InlineData("\"protocol\":\"http\"", "\"protocol\":\"ftp\"")]
    [InlineData("\"port\":8080", "\"port\":0")]
    [InlineData("\"port\":8080", "\"port\":65536")]
    [InlineData("\"port\":8080", "\"port\":8081")]
    [InlineData("\"port\":8080", "\"port\":true")]
    [InlineData("\"ip\":\"8.8.8.8\"", "\"ip\":\"1.1.1.1\"")]
    [InlineData("http://8.8.8.8:8080", "http://user:pass@8.8.8.8:8080")]
    [InlineData("http://8.8.8.8:8080", "http://8.8.8.8:8080/path")]
    [InlineData("http://8.8.8.8:8080", "http://8.8.8.8:8080?key=example")]
    [InlineData("http://8.8.8.8:8080", "https://8.8.8.8:8080")]
    [InlineData("8.8.8.8", "127.0.0.1")]
    [InlineData("8.8.8.8", "10.0.0.1")]
    [InlineData("8.8.8.8", "example.com")]
    [InlineData("\"ip\":", "\"password\":null,\"ip\":")]
    [InlineData("\"ip\":", "\"username\":\"user\",\"ip\":")]
    [InlineData("\"ip\":", "\"authorization\":\"secret\",\"ip\":")]
    public void RejectsWholeFeedForUnsafeOrInconsistentRecord(string before, string after) =>
        Assert.Throws<InvalidDataException>(() => LitportHttpsFeedAdapter.Extract(Url, "[" + Row + "," + Row.Replace(before, after, StringComparison.Ordinal) + "]"));

    [Fact]
    public void PreservesIpv6AndCompleteMixedSnapshotAcrossWindows()
    {
        static string Scheme(int port) => (port % 3) switch { 0 => "http", 1 => "socks4", _ => "socks5" };
        var rows = Enumerable.Range(1000, 801).Select(port => new { ip = "2606:4700:4700::1111", port, protocol = Scheme(port), https = true, url = Scheme(port) + $"://[2606:4700:4700::1111]:{port}" });
        var converted = LitportHttpsFeedAdapter.Extract(Url, JsonSerializer.Serialize(rows));
        var snapshot = ProxyCandidateSnapshotCodec.Encode(converted, ProxyProtocol.Http);
        var seen = new HashSet<int>();
        var cursor = 0;
        while (true)
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 200, candidate =>
            {
                var endpoint = candidate.ToEndpoint();
                Assert.Equal("2606:4700:4700::1111", endpoint.Host);
                Assert.Equal((endpoint.Port % 3) switch { 0 => ProxyProtocol.Https, 1 => ProxyProtocol.Socks4, _ => ProxyProtocol.Socks5 }, endpoint.Protocol);
                Assert.True(seen.Add(endpoint.Port));
                return true;
            });
            Assert.True(window.Completed || window.NextIndex > cursor);
            cursor = window.NextIndex;
            if (window.Completed) break;
        }
        Assert.Equal(801, snapshot.Count);
        Assert.Equal(Enumerable.Range(1000, 801), seen.Order());
    }

    [Fact]
    public async Task CollectorTransformsRefreshAndKeepsValidatorsAndSchedule()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) };
        response.Headers.ETag = new EntityTagHeaderValue("\"typed-generation\"");
        using var client = new HttpClient(new Handler(response));
        using var collector = CreateCollector();
        var result = await collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None);
        Assert.False(result.NotModified);
        Assert.Equal("\"typed-generation\"", result.HttpETag);
        Assert.Equal(ProxyProtocol.Https, Assert.Single(SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http)).Protocol);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddMinutes(5), SourceFetchSchedule.NextSuccessAttempt(Url, now));
    }

    [Fact]
    public async Task CollectorDoesNotParse304Body()
    {
        using var client = new HttpClient(new Handler(new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var collector = CreateCollector();
        var result = await collector.FetchSourceStateAsync(client, Url, "\"cached\"", null, CancellationToken.None);
        Assert.True(result.NotModified);
        Assert.Null(result.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task CollectorRejectsForeignRedirectAndHonorsRetryAfter(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);
        if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://1.1.1.1/export");
        else response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
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
