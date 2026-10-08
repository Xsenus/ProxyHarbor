using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class LiveSocksHtmlFeedAdapterTests
{
    private const string Path = "/2026/10/08-10-26-socks-5-servers-3.html";
    private const string Url = "https://live-socks.net" + Path;
    private const string Title = "08-10-26 | Socks 5 Servers (3)";
    private static string Rows => Row("8.8.8.8:1080", "SOCKS5") + Row("1.1.1.1:1080", "SOCKS4") + Row("8.8.8.8:1080", "SOCKS4");
    private static string Row(string endpoint, string protocol) =>
        $"<tr data-addr=\"{endpoint}\"><td class=\"addr\">{endpoint}</td><td><span class=\"fi fi-us\"></span> United States</td>" +
        $"<td>{protocol}</td><td class=\"ok\">0.32 s</td><td><span class=\"bar\"><i style=\"width:89%\"></i></span> 89%</td></tr>";
    private static string Page(string rows) => "<!doctype html><html><body><h1>" + Title + " · Free SOCKS Proxy List</h1><table>" +
        "<caption class=\"sr\">SOCKS proxies published on 08 October 2026</caption><thead><tr><th>IP : port</th><th>Country</th><th>Type</th><th>Response</th><th>Uptime</th></tr></thead>" +
        "<tbody id=\"tb\">" + rows + "</tbody></table></body></html>";
    private static string Home(string path = Path, string title = Title) => "<html><body><section class=\"sec\"><div class=\"wrap\"><div class=\"sec__head\"><h2>Latest drop</h2><a href=\"/archive.html\">All lists &amp;rarr;</a></div><article class=\"drop\"><h3><a href=\"" + path + "\">" + title + "</a></h3></article></div></section></body></html>";

    [Fact]
    public void PreservesEveryRowsProtocolAndSnapshotTail()
    {
        var extracted = LiveSocksHtmlFeedAdapter.Extract(Url, Page(Rows));
        var parsed = SourceFeedParser.ParseRequired(extracted, ProxyProtocol.Https);
        Assert.Equal(3, parsed.Count);
        Assert.Contains(("8.8.8.8", 1080, ProxyProtocol.Socks5), parsed);
        Assert.Contains(("8.8.8.8", 1080, ProxyProtocol.Socks4), parsed);
        Assert.Contains(("1.1.1.1", 1080, ProxyProtocol.Socks4), parsed);
        var snapshot = ProxyCandidateSnapshotCodec.Encode(extracted, ProxyProtocol.Http);
        var decoded = new HashSet<(string, int, ProxyProtocol)>();
        var cursor = 0;
        while (true)
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 1, k => decoded.Add(k.ToEndpoint()));
            cursor = window.NextIndex;
            if (window.Completed) break;
        }
        Assert.True(decoded.SetEquals(parsed));
        Assert.Equal(Url, LiveSocksHtmlFeedAdapter.LatestUrl(Home()));
    }

    [Fact]
    public void SupportsPublisherCollisionSuffixWithoutChangingDeclaredRows()
    {
        var url = Url.Replace(".html", "_1.html");
        Assert.True(LiveSocksHtmlFeedAdapter.Supports(url));
        Assert.Equal(3, SourceFeedParser.ParseRequired(LiveSocksHtmlFeedAdapter.Extract(url, Page(Rows)), ProxyProtocol.Http).Count);
    }

    [Fact]
    public void DeduplicatesTypedRowsAndExcludesNonPublicAddresses()
    {
        var rows = Row("8.8.8.8:1080", "SOCKS5") + Row("127.0.0.1:80", "SOCKS5") + Row("8.8.8.8:1080", "SOCKS5");
        var parsed = SourceFeedParser.ParseRequired(LiveSocksHtmlFeedAdapter.Extract(Url, Page(rows)), ProxyProtocol.Http);
        var single = Assert.Single(parsed);
        Assert.Equal("8.8.8.8", single.Host);
        Assert.Equal(ProxyProtocol.Socks5, single.Protocol);
    }

    [Theory]
    [InlineData("http://live-socks.net/2026/10/08-10-26-socks-5-servers-3.html")]
    [InlineData("https://www.live-socks.net/2026/10/08-10-26-socks-5-servers-3.html")]
    [InlineData("https://live-socks.net.example.org/2026/10/08-10-26-socks-5-servers-3.html")]
    [InlineData("https://user@live-socks.net/2026/10/08-10-26-socks-5-servers-3.html")]
    [InlineData("https://live-socks.net:444/2026/10/08-10-26-socks-5-servers-3.html")]
    [InlineData("https://live-socks.net/2026/10/08-10-26-socks-5-servers-3.html?key=x")]
    [InlineData("https://live-socks.net/2026/10/08-10-26-socks-5-servers-3.html#tb")]
    [InlineData("https://live-socks.net/2026/10/%30%38-10-26-socks-5-servers-3.html")]
    [InlineData("https://live-socks.net/2026/10/08-09-26-socks-5-servers-3.html")]
    [InlineData("https://live-socks.net/2026/02/31-02-26-socks-5-servers-3.html")]
    [InlineData("https://live-socks.net/2026/10/08-10-25-socks-5-servers-3.html")]
    [InlineData("https://live-socks.net/2026/10/08-10-26-socks-5-servers-10001.html")]
    [InlineData("https://live-socks.net/2026/10/08-10-26-socks-5-servers-0.html")]
    [InlineData("https://live-socks.net/checker.html")]
    public void DoesNotAuthorizeOtherOriginsAndPages(string url)
    {
        Assert.False(LiveSocksHtmlFeedAdapter.Supports(url));
        Assert.Throws<InvalidDataException>(() => LiveSocksHtmlFeedAdapter.Extract(url, Page(Rows)));
    }

    [Fact]
    public void RejectsPageChangesInsteadOfImportingASuccessfulPrefix()
    {
        var valid = Page(Rows);
        foreach (var invalid in new[]
        {
            "<html>8.8.8.8:1080</html>", valid.Replace("SOCKS4", "HTTPS"), valid.Replace("Type</th>", "Protocol</th>"),
            Page(Row("8.8.8.8:1080", "SOCKS5")), Page(Rows + Row("9.9.9.9:1080", "SOCKS5")),
            valid.Replace("08 October 2026", "07 October 2026"), valid.Replace(Title, Title.Replace("(3)", "(4)")),
            valid.Replace("<tbody id=\"tb\">", "<tbody id=\"tb\"><script>8.8.8.8:1080</script>"),
            valid.Replace("data-addr=\"8.8.8.8:1080\"", "data-addr=\"9.9.9.9:1080\""),
            valid.Replace("8.8.8.8:1080", "8.8.8.8:65536"), valid.Replace("8.8.8.8:1080", "010.0.0.1:1080"),
            valid.Replace("8.8.8.8:1080", "example.com:1080"), valid.Replace("89%</td>", "88%</td>"),
            valid.Replace("89%", "101%"), valid + valid, valid.Replace("<table>", "<table><table>"),
            valid.Replace("United States", "<a href='/checker'>United States</a>"), new string(' ', 10_000_001),
            Page(Row("127.0.0.1:80", "SOCKS5") + Row("10.0.0.1:80", "SOCKS5") + Row("169.254.169.254:80", "SOCKS4"))
        }) Assert.Throws<InvalidDataException>(() => LiveSocksHtmlFeedAdapter.Extract(Url, invalid));
    }

    [Theory]
    [InlineData("<!--", "-->")]
    [InlineData("<script>", "</script>")]
    [InlineData("<style>", "</style>")]
    [InlineData("<template>", "</template>")]
    [InlineData("<textarea>", "</textarea>")]
    [InlineData("<noscript>", "</noscript>")]
    [InlineData("<xmp>", "</xmp>")]
    [InlineData("<plaintext>", "")]
    [InlineData("<div data-value='", "'>ignored</div>")]
    public void IgnoresInertAndQuotedMarkup(string before, string after)
    {
        Assert.Throws<InvalidDataException>(() => LiveSocksHtmlFeedAdapter.Extract(Url, before + Page(Rows) + after));
        Assert.Throws<InvalidDataException>(() => LiveSocksHtmlFeedAdapter.LatestUrl(before + Home() + after));
    }

    [Fact]
    public void LatestLinkUsesOnlyTheLatestDropSection()
    {
        var html = Home() + "<article class=\"drop\"><h3><a href=\"/notes.html\">Field notes</a></h3></article>";
        Assert.Equal(Url, LiveSocksHtmlFeedAdapter.LatestUrl(html));
    }

    [Fact]
    public void RejectsAmbiguousOrForeignLatestLinks()
    {
        foreach (var invalid in new[] { Home() + Home(), Home("//1.1.1.1/list"), Home("/checker.html"), Home(Path, "Wrong list"),
            Home().Replace("<h3>", "<h3><a href='/ignore'>ignore</a>"), "<html>8.8.8.8:1080</html>" })
            Assert.Throws<InvalidDataException>(() => LiveSocksHtmlFeedAdapter.LatestUrl(invalid));
    }

    [Fact]
    public async Task LatestFetchUsesTwoBoundedRequestsAndDiscardsUnrelatedValidators()
    {
        var calls = new List<(string, int)>();
        var home = Home();
        var result = await LiveSocksHtmlFeedAdapter.FetchLatestAsync((url, maximum, _) =>
        {
            calls.Add((url, maximum));
            return Task.FromResult(new SourceFetchResult(url == LiveSocksHtmlFeedAdapter.Url ? home : Page(Rows), false, "\"unrelated\"", DateTimeOffset.UtcNow));
        }, 100_000, CancellationToken.None);
        Assert.Equal(new[] { (LiveSocksHtmlFeedAdapter.Url, 100_000), (Url, 100_000 - System.Text.Encoding.UTF8.GetByteCount(home)) }, calls);
        Assert.Null(result.HttpETag);
        Assert.Null(result.HttpLastModifiedAt);
        Assert.Equal(3, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
    }

    [Fact]
    public async Task LatestFetchRejectsUnexpected304AndExhaustedBodyBudget()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => LiveSocksHtmlFeedAdapter.FetchLatestAsync(
            (_, _, _) => Task.FromResult(new SourceFetchResult(null, true, null, null)), 100_000, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => LiveSocksHtmlFeedAdapter.FetchLatestAsync(
            (_, _, _) => Task.FromResult(new SourceFetchResult(Home(), false, null, null)), 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => LiveSocksHtmlFeedAdapter.FetchLatestAsync(
            (url, _, _) => Task.FromResult(url == LiveSocksHtmlFeedAdapter.Url ? new SourceFetchResult(Home(), false, null, null) : new SourceFetchResult(null, true, null, null)), 100_000, CancellationToken.None));
    }

    [Fact]
    public async Task CollectorTransformsDatedPageAndPreservesItsOwnValidators()
    {
        using var client = new HttpClient(new Handler((_, _) => Response(Page(Rows))));
        using var collector = Collector();
        var result = await collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None);
        Assert.Equal("\"page-version\"", result.HttpETag);
        Assert.Equal(3, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
    }

    [Fact]
    public async Task CollectorLatestDoesNotForwardSavedCompositeValidators()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Response(request.RequestUri.AbsolutePath == "/" ? Home() : Page(Rows));
        }));
        using var collector = Collector();
        var result = await collector.FetchSourceStateAsync(client, LiveSocksHtmlFeedAdapter.Url, "\"old-composite\"", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(new[] { LiveSocksHtmlFeedAdapter.Url, Url }, calls);
        Assert.Null(result.HttpETag);
        Assert.Equal(3, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
    }

    [Fact]
    public async Task CollectorKeepsDated304AndHourlySchedule()
    {
        using var client = new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var collector = Collector();
        var result = await collector.FetchSourceStateAsync(client, Url, "\"saved\"", null, CancellationToken.None);
        Assert.True(result.NotModified);
        Assert.Null(result.Content);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddHours(1), SourceFetchSchedule.NextSuccessAttempt(Url, now));
        Assert.Equal(now.AddHours(1), SourceFetchSchedule.NextSuccessAttempt(LiveSocksHtmlFeedAdapter.Url, now));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Found)]
    [InlineData(true, HttpStatusCode.Found)]
    [InlineData(false, HttpStatusCode.TooManyRequests)]
    [InlineData(true, HttpStatusCode.TooManyRequests)]
    public async Task CollectorPreservesRedirectAndRateLimitProtection(bool latest, HttpStatusCode status)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (latest && calls == 1) return Response(Home());
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://1.1.1.1/list");
            else response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        }));
        using var collector = Collector();
        if (status == HttpStatusCode.Found)
            await Assert.ThrowsAsync<HttpRequestException>(() => collector.FetchSourceStateAsync(client, latest ? LiveSocksHtmlFeedAdapter.Url : Url, null, null, CancellationToken.None));
        else
            await Assert.ThrowsAsync<SourceRateLimitException>(() => collector.FetchSourceStateAsync(client, latest ? LiveSocksHtmlFeedAdapter.Url : Url, null, null, CancellationToken.None));
        Assert.Equal(latest ? 2 : 1, calls);
    }

    private static HttpResponseMessage Response(string body)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        result.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        result.Headers.ETag = new EntityTagHeaderValue("\"page-version\"");
        return result;
    }
    private static ProxyCollector Collector() => new(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request, cancellationToken));
    }
}
