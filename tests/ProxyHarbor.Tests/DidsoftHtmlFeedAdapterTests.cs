using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class DidsoftHtmlFeedAdapterTests
{
    private const string Url = "https://free-proxy-list.net/";

    private static string Row(string host = "8.8.8.8", string port = "8080", string https = "yes") =>
        $"<tr><td>{host}</td><td>{port}</td><td>US</td><td class='hm'>United States</td><td>elite proxy</td><td class='hm'></td><td class='hx'>{https}</td><td class='hm'>1 secs ago</td></tr>";

    private static string Page(string rows) => "<!doctype html><html><body><table class=\"table table-striped table-bordered\"><thead><tr>" +
        "<th>IP Address</th><th>Port</th><th>Code</th><th class='hm'>Country</th><th>Anonymity</th><th class='hm'>Google</th><th class='hx'>Https</th><th class='hm'>Last Checked</th>" +
        "</tr></thead><tbody>" + rows + "</tbody></table></body></html>";

    [Theory]
    [InlineData("https://free-proxy-list.net/")]
    [InlineData("https://free-proxy-list.net/uk-proxy.html")]
    [InlineData("https://free-proxy-list.net/anonymous-proxy.html")]
    [InlineData("https://free-proxy-list.net/ssl-proxy.html")]
    [InlineData("https://free-proxy-list.net/us-proxy.html")]
    [InlineData("https://free-proxy-list.net/google-proxy.html")]
    [InlineData("https://www.sslproxies.org/")]
    [InlineData("https://www.us-proxy.org/")]
    [InlineData("https://www.google-proxy.net/")]
    public void PreservesBothHttpAndConnectWithoutTlsToProxy(string url)
    {
        var html = Page(Row() + Row() + Row("1.1.1.1", "80", "no") + Row("127.0.0.1"));
        var text = DidsoftHtmlFeedAdapter.Extract(url, html);
        var expected = new HashSet<(string Host, int Port, ProxyProtocol Protocol)>
        {
            ("8.8.8.8", 8080, ProxyProtocol.Http), ("8.8.8.8", 8080, ProxyProtocol.Https), ("1.1.1.1", 80, ProxyProtocol.Http)
        };
        Assert.True(expected.SetEquals(SourceFeedParser.ParseRequired(text, ProxyProtocol.Socks5)));
        var snapshot = ProxyCandidateSnapshotCodec.Encode(text, ProxyProtocol.Socks5);
        var decoded = new HashSet<(string Host, int Port, ProxyProtocol Protocol)>();
        var cursor = 0;
        while (true)
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 1, candidate => decoded.Add(candidate.ToEndpoint()));
            if (window.Completed) break;
            Assert.True(window.NextIndex > cursor);
            cursor = window.NextIndex;
        }
        Assert.True(expected.SetEquals(decoded));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddMinutes(10), SourceFetchSchedule.NextSuccessAttempt(url, now));
    }

    [Fact]
    public void SupportsPublicIpv6AndHtmlEntities()
    {
        var text = DidsoftHtmlFeedAdapter.Extract(Url, Page(Row("2606:4700:4700::1111").Replace("United States", "US &amp; Islands")));
        Assert.Equal(2, SourceFeedParser.ParseRequired(text, ProxyProtocol.Http).Count);
        Assert.Contains("https://[2606:4700:4700::1111]:8080", text);
    }

    [Theory]
    [InlineData("http://free-proxy-list.net/")]
    [InlineData("https://free-proxy-list.net:444/")]
    [InlineData("https://free-proxy-list.net.example.org/")]
    [InlineData("https://user@free-proxy-list.net/")]
    [InlineData("https://free-proxy-list.net/?key=x")]
    [InlineData("https://free-proxy-list.net/#list")]
    [InlineData("https://free-proxy-list.net/login")]
    [InlineData("https://free-proxy-list.net/api/get-proxies")]
    [InlineData("https://www.us-proxy.org/us-proxy.html")]
    public void RejectsUnregisteredUrls(string url)
    {
        Assert.False(DidsoftHtmlFeedAdapter.Supports(url));
        Assert.Throws<InvalidDataException>(() => DidsoftHtmlFeedAdapter.Extract(url, Page(Row())));
    }

    [Theory]
    [InlineData("8.8.8.8", "0", "yes")]
    [InlineData("8.8.8.8", "65536", "yes")]
    [InlineData("8.8.8.8", "8080:user:pass", "yes")]
    [InlineData("example.org", "80", "yes")]
    [InlineData("0x08080808", "80", "yes")]
    [InlineData("8.8.8.8", "80", "unknown")]
    [InlineData("8.8.8.8", "80", "<script>yes</script>")]
    public void MalformedRowInvalidatesWholeFeed(string host, string port, string https) =>
        Assert.Throws<InvalidDataException>(() => DidsoftHtmlFeedAdapter.Extract(Url, Page(Row() + Row(host, port, https))));

    [Fact]
    public void RejectsMissingDuplicateSpoofedOrChangedTables()
    {
        var good = Page(Row());
        foreach (var invalid in new[]
        {
            "<html>8.8.8.8:80</html>", Page(""), Page(Row("127.0.0.1")), good + good,
            "<!--" + good + "-->", "<script>" + good + "</script>", "<textarea>" + good + "</textarea>",
            "<style>" + good + "</style>", "<title>" + good + "</title>", "<template>" + good + "</template>",
            "<template><template></template>" + good + "</template>", "<noscript>" + good + "</noscript>",
            "<script></scriptx>" + good + "</script>", "<plaintext></plaintext>" + good,
            "<div data-test='" + good + "'></div>", good.Replace("<th>Port</th>", "<th>Password</th>"),
            good.Replace("</tbody>", "unexpected</tbody>"), good.Replace("<td>US</td>", "<td><span>US</span></td>")
        }) Assert.Throws<InvalidDataException>(() => DidsoftHtmlFeedAdapter.Extract(Url, invalid));
    }

    [Fact]
    public void RejectsExcessiveRowsAndBody()
    {
        Assert.Throws<InvalidDataException>(() => DidsoftHtmlFeedAdapter.Extract(Url, Page(string.Concat(Enumerable.Repeat(Row(), 10_001)))));
        Assert.Throws<InvalidDataException>(() => DidsoftHtmlFeedAdapter.Extract(Url, new string(' ', 10_000_001)));
    }

    [Fact]
    public async Task CollectorConvertsHtmlAndKeepsValidators()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page(Row())) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        response.Headers.ETag = new EntityTagHeaderValue("\"didsoft-version\"");
        using var client = new HttpClient(new Handler(response));
        using var collector = Collector();
        var result = await collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None);
        Assert.Equal("\"didsoft-version\"", result.HttpETag);
        Assert.Equal(2, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
    }

    [Fact]
    public async Task CollectorPreservesNotModifiedWithoutParsingHtml()
    {
        using var client = new HttpClient(new Handler(new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var collector = Collector();
        var result = await collector.FetchSourceStateAsync(client, Url, "\"cached\"", null, CancellationToken.None);
        Assert.True(result.NotModified);
        Assert.Null(result.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task CollectorRejectsForeignRedirectAndHonorsRateLimit(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);
        if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://1.1.1.1/list");
        else response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        using var client = new HttpClient(new Handler(response));
        using var collector = Collector();
        if (status == HttpStatusCode.Found)
            await Assert.ThrowsAsync<HttpRequestException>(() => collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None));
        else
            await Assert.ThrowsAsync<SourceRateLimitException>(() => collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None));
    }

    private static ProxyCollector Collector() => new(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
