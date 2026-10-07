using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class MyProxyHtmlFeedAdapterTests
{
    [Fact]
    public void SuccessfulPagesRefreshHourly()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddHours(1), SourceFetchSchedule.NextSuccessAttempt("https://www.my-proxy.com/free-proxy-list.html", now));
        Assert.Null(SourceFetchSchedule.NextSuccessAttempt("https://example.com/free-proxy-list.html", now));
    }

    [Theory]
    [InlineData("free-proxy-list.html", ProxyProtocol.Http)]
    [InlineData("free-proxy-list-10.html", ProxyProtocol.Http)]
    [InlineData("free-elite-proxy.html", ProxyProtocol.Http)]
    [InlineData("free-socks-4-proxy.html", ProxyProtocol.Socks4)]
    [InlineData("free-socks-5-proxy.html", ProxyProtocol.Socks5)]
    public void UsesOnlyDeclaredListAndProtocol(string page, ProxyProtocol expected)
    {
        var html = "<!doctype html><html><body>9.9.9.9:9999<div class=\"list\">8.8.8.8:8080#US<br>" +
            "127.0.0.1:80#US<br />8.8.8.8:8080#CA<br>1.1.1.1:1080</div>4.4.4.4:80</body></html>";
        var extracted = MyProxyHtmlFeedAdapter.Extract("https://www.my-proxy.com/" + page, html);
        var parsed = SourceFeedParser.ParseRequired(extracted, ProxyProtocol.Https);
        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, item => Assert.Equal(expected, item.Protocol));
        Assert.DoesNotContain(parsed, item => item.Host is "127.0.0.1" or "9.9.9.9" or "4.4.4.4");
        var snapshot = ProxyCandidateSnapshotCodec.Encode(extracted, ProxyProtocol.Https);
        var decoded = new HashSet<(string Host, int Port, ProxyProtocol Protocol)>();
        var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 20, candidate =>
        {
            decoded.Add(candidate.ToEndpoint());
            return true;
        });
        Assert.True(window.Completed);
        Assert.True(decoded.SetEquals(parsed));
    }

    [Theory]
    [InlineData("http://www.my-proxy.com/free-proxy-list.html")]
    [InlineData("https://my-proxy.com/free-proxy-list.html")]
    [InlineData("https://www.my-proxy.com.example.org/free-proxy-list.html")]
    [InlineData("https://www.my-proxy.com:444/free-proxy-list.html")]
    [InlineData("https://user@www.my-proxy.com/free-proxy-list.html")]
    [InlineData("https://www.my-proxy.com/free-proxy-list.html?key=abc")]
    [InlineData("https://www.my-proxy.com/free-proxy-list.html#list")]
    [InlineData("https://www.my-proxy.com/free-proxy-list-11.html")]
    [InlineData("https://www.my-proxy.com/login")]
    public void DoesNotAuthorizeOtherEndpoints(string url) => Assert.False(MyProxyHtmlFeedAdapter.Supports(url));

    [Theory]
    [InlineData("<html>8.8.8.8:80</html>")]
    [InlineData("<div class=\"list\"></div>")]
    [InlineData("<div class=\"list\">127.0.0.1:80</div>")]
    [InlineData("<div class=\"list\">8.8.8.8:80<script>1.1.1.1:80</script></div>")]
    [InlineData("<div class=\"list\">8.8.8.8:80</div><div class=\"list\">1.1.1.1:80</div>")]
    [InlineData("<div class=\"list\">8.8.8.8:80<br>1.1.1.1:80:user:password</div>")]
    [InlineData("<div class=\"list\">example.org:80</div>")]
    [InlineData("<div class=\"list\">8.8.8.8:123456</div>")]
    [InlineData("<!-- <div class=\"list\">8.8.8.8:80</div> -->")]
    [InlineData("<script>let x='<div class=\"list\">8.8.8.8:80</div>';</script>")]
    [InlineData("<textarea><div class=\"list\">8.8.8.8:80</div></textarea>")]
    [InlineData("<div data-test='<div class=\"list\">8.8.8.8:80</div>'></div>")]
    public void RejectsUnknownAndErrorPageStructures(string html) =>
        Assert.Throws<InvalidDataException>(() => MyProxyHtmlFeedAdapter.Extract("https://www.my-proxy.com/free-proxy-list.html", html));

    [Fact]
    public void RejectsExcessiveRowsRatherThanPublishingPartialCapture()
    {
        var html = "<div class=\"list\">" + string.Concat(Enumerable.Repeat("8.8.8.8:80<br>", 10_002)) + "</div>";
        Assert.Throws<InvalidDataException>(() => MyProxyHtmlFeedAdapter.Extract("https://www.my-proxy.com/free-proxy-list.html", html));
    }

    [Fact]
    public async Task CollectorTransformsHtmlAndPreservesConditionalValidators()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><div class=\"list\">8.8.8.8:1080#US</div></html>")
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        response.Headers.ETag = new EntityTagHeaderValue("\"html-epoch\"");
        using var client = new HttpClient(new Handler(response));
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
        var result = await collector.FetchSourceStateAsync(client, "https://www.my-proxy.com/free-socks-5-proxy.html", null, null, CancellationToken.None);
        Assert.False(result.NotModified);
        Assert.Equal("socks5://8.8.8.8:1080\n", result.Content);
        Assert.Equal("\"html-epoch\"", result.HttpETag);
    }

    [Fact]
    public async Task CollectorKeeps304WithoutTryingToParseAnEmptyPage()
    {
        using var client = new HttpClient(new Handler(new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
        var result = await collector.FetchSourceStateAsync(client, "https://www.my-proxy.com/free-proxy-list.html", "\"html-epoch\"", null, CancellationToken.None);
        Assert.True(result.NotModified);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task CollectorDoesNotTreatHtmlFromOtherSourcesAsAFeed()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<div class=\"list\">8.8.8.8:80</div>")
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        using var client = new HttpClient(new Handler(response));
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => collector.FetchSourceStateAsync(client, "https://1.1.1.1/list.html", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task CollectorHonorsPublisherRateLimit()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));
        using var client = new HttpClient(new Handler(response));
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 2 }), NullLogger<ProxyCollector>.Instance);
        await Assert.ThrowsAsync<SourceRateLimitException>(() => collector.FetchSourceStateAsync(client, "https://www.my-proxy.com/free-proxy-list.html", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task CollectorRejectsCrossOriginRedirect()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://1.1.1.1/page.html");
        using var client = new HttpClient(new Handler(response));
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(() => collector.FetchSourceStateAsync(client, "https://www.my-proxy.com/free-proxy-list.html", null, null, CancellationToken.None));
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
