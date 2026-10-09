using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class PxysCsvFeedAdapterTests
{
    internal const string Header = "protocol,proxy_address,latency_ms,ssl_support,country,anonymity,source,last_tested\r\n";
    internal const string Row = "SOCKS5,8.8.8.8:1080,1,false,US,elite,https://example.test,2026-10-08\r\n";

    [Fact]
    public void PreservesKnownTransportsDeduplicatesAndHoldsAmbiguousHttps()
    {
        var csv = "\uFEFF" + Header + Row + Row.Replace("SOCKS5", "socks5") + Row.Replace("SOCKS5", "SOCKS4") +
            Row.Replace("SOCKS5", "HTTP") + Row.Replace("SOCKS5", "HTTPS") +
            Row.Replace("8.8.8.8:1080", "[2606:4700:4700::1111]:1080")
                .Replace("elite", "\"A, B\r\n\"\"quoted\"\"\"")
                .Replace("https://example.test", "https://example.test/9.9.9.9:9999");
        var body = PxysCsvFeedAdapter.Extract(csv);
        var parsed = SourceFeedParser.ParseRequired(body, ProxyProtocol.Http);
        Assert.Equal(4, parsed.Count);
        Assert.Contains(("8.8.8.8", 1080, ProxyProtocol.Socks4), parsed);
        Assert.Contains(("8.8.8.8", 1080, ProxyProtocol.Socks5), parsed);
        Assert.Contains(("8.8.8.8", 1080, ProxyProtocol.Http), parsed);
        Assert.DoesNotContain(parsed, item => item.Protocol is ProxyProtocol.Https or ProxyProtocol.HttpTls or ProxyProtocol.HttpTlsUnverified);
        Assert.True(SourceFeedParser.ParseBoundedRequired(body, ProxyProtocol.Http, 1).Truncated);
        var snapshot = ProxyCandidateSnapshotCodec.Encode(body, ProxyProtocol.Http);
        var decoded = new List<ProxyCandidateKey>();
        var cursor = 0;
        while (cursor < snapshot.Count)
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 1, key => { decoded.Add(key); return true; });
            Assert.True(window.NextIndex > cursor);
            cursor = window.NextIndex;
        }
        Assert.Equal(parsed, decoded.Select(key => key.ToEndpoint()).ToArray());
    }

    [Fact]
    public void RejectsMalformedUnknownPrivateAndCredentialBearingRowsWithoutPartialResults()
    {
        foreach (var invalid in new[]
        {
            Header, Header.Replace("protocol,", "password,") + Row,
            Header + Row.Replace("SOCKS5", "unknown"), Header + Row.Replace("SOCKS5", "HTTPS"),
            Header + Row + Row.Replace("8.8.8.8:1080", "127.0.0.1:1080"),
            Header + Row.Replace("8.8.8.8:1080", "user:password@8.8.8.8:1080"),
            Header + Row.Replace("8.8.8.8:1080", "8.8.8.8:65536"),
            Header + Row.Replace("8.8.8.8:1080", "8.8.8.8:1080/trailing"),
            Header + Row.Replace("elite", "\"unclosed"), Header + Row.Replace("elite", "un\"quoted"),
            Header + Row.Replace("elite", "\"closed\"extra"), Header + Row.Replace("elite", new string('x', 16_385)),
            Header + Row + "HTTP,1.1.1.1:80\r\n", Header + Row.Replace("elite,", "elite,extra,")
        }) Assert.Throws<InvalidDataException>(() => PxysCsvFeedAdapter.Extract(invalid));
        Assert.Throws<InvalidDataException>(() => PxysCsvFeedAdapter.Extract(new string(' ', 10_000_001)));
        Assert.Throws<InvalidDataException>(() => PxysCsvFeedAdapter.Extract(Header + string.Concat(Enumerable.Repeat(Row, 100_001))));
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/Pxys-io/DailyProxyList/master/working_proxies.csv")]
    [InlineData("https://raw.githubusercontent.com/Pxys-io/DailyProxyList/master/working_proxies.csv?extra=1")]
    [InlineData("https://raw.githubusercontent.com/Pxys-io/DailyProxyList/master/working_proxies.csv#fragment")]
    [InlineData("https://user@raw.githubusercontent.com/Pxys-io/DailyProxyList/master/working_proxies.csv")]
    [InlineData("https://raw.githubusercontent.com.evil.test/Pxys-io/DailyProxyList/master/working_proxies.csv")]
    public void DoesNotClaimOtherUrls(string url) => Assert.False(PxysCsvFeedAdapter.Supports(url));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectorTransformsCompleteBodyAndPreservesConditionalResponse(bool notModified)
    {
        foreach (var url in PxysCsvFeedAdapter.Urls)
        {
            using var response = new HttpResponseMessage(notModified ? HttpStatusCode.NotModified : HttpStatusCode.OK)
            { Content = notModified ? null : new StringContent(Header + Row) };
            response.Headers.ETag = new EntityTagHeaderValue("\"pxys\"");
            using var client = new HttpClient(new Handler(response));
            using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
            var result = await collector.FetchSourceStateAsync(client, url, "\"previous\"", null, CancellationToken.None);
            Assert.Equal(notModified, result.NotModified);
            Assert.Equal("\"pxys\"", result.HttpETag);
            if (notModified) Assert.Null(result.Content);
            else Assert.Equal(ProxyProtocol.Socks5, Assert.Single(SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http)).Protocol);
        }
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
