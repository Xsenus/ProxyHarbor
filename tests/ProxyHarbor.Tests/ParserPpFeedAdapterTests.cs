using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ParserPpFeedAdapterTests
{
    private const string Ndjson = "https://raw.githubusercontent.com/parserpp/ip_ports/main/proxy.list";
    private const string Grouped = "https://raw.githubusercontent.com/parserpp/ip_ports/main/proxyinfo.json";
    private const string HttpRow = "{\"host\":\"8.8.8.8\",\"port\":8080,\"type\":\"http\"}";

    [Theory]
    [InlineData(Ndjson)]
    [InlineData("https://raw.githubusercontent.com/parserpp/ip_ports/refs/heads/main/proxy.list")]
    [InlineData(Grouped)]
    [InlineData("https://raw.githubusercontent.com/parserpp/ip_ports/refs/heads/main/proxyinfo.json")]
    public void PreservesEveryExplicitProtocolAndIgnoresDiagnosticEndpoints(string url)
    {
        var rows = new[]
        {
            HttpRow,
            "{\"host\":\"1.1.1.1\",\"port\":\"3128\",\"type\":\"https\",\"export_address\":[\"9.9.9.9:80\"]}",
            "{\"host\":\"8.8.4.4\",\"port\":1080,\"type\":\"socks4\",\"from\":\"9.9.9.9:80\"}",
            "{\"host\":\"9.9.9.9\",\"port\":1080,\"type\":\"socks5\",\"password\":null}"
        };
        var content = url.EndsWith("proxy.list", StringComparison.Ordinal)
            ? "\uFEFF" + string.Join("\r\n", rows) + "\n\n" + HttpRow
            : "\uFEFF{\"http_transparent\":[" + rows[0] + "," + rows[0] + "],\"https_anonymous\":[" + rows[1] +
                "],\"socks4_high_anonymous\":[" + rows[2] + "],\"socks5_anonymous\":[" + rows[3] + "]}";
        var converted = ParserPpFeedAdapter.Extract(url, content);
        var parsed = SourceFeedParser.ParseRequired(converted, ProxyProtocol.Http);
        Assert.Equal(4, parsed.Count);
        Assert.Equal(new[] { ProxyProtocol.Http, ProxyProtocol.Https, ProxyProtocol.Socks4, ProxyProtocol.Socks5 },
            parsed.Select(endpoint => endpoint.Protocol).OrderBy(protocol => protocol));
        Assert.DoesNotContain(parsed, endpoint => endpoint.Host == "9.9.9.9" && endpoint.Port == 80);
        Assert.Equal(4, ProxyCandidateSnapshotCodec.Encode(converted, ProxyProtocol.HttpTls).Count);
    }

    [Fact]
    public void SkipsUnsafeAndInvalidEndpointsUsingSharedParser()
    {
        var rows = new[] { HttpRow, HttpRow.Replace("8.8.8.8", "127.0.0.1"), HttpRow.Replace("8.8.8.8", "10.0.0.1"),
            HttpRow.Replace("8.8.8.8", "010.0.0.1"), HttpRow.Replace("8.8.8.8", "example.org"),
            HttpRow.Replace("8.8.8.8", "http://1.1.1.1"), HttpRow.Replace("8080", "0"), HttpRow.Replace("8080", "65536") };
        var converted = ParserPpFeedAdapter.Extract(Ndjson, string.Join('\n', rows));
        Assert.Single(SourceFeedParser.ParseRequired(converted, ProxyProtocol.Http));
    }

    [Fact]
    public void ReplaysAllSnapshotWindowsWithoutTruncation()
    {
        var body = string.Join('\n', Enumerable.Range(1000, 1201).Select(port => HttpRow.Replace("8080", port.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var snapshot = ProxyCandidateSnapshotCodec.Encode(ParserPpFeedAdapter.Extract(Ndjson, body), ProxyProtocol.Socks5);
        var ports = new HashSet<int>();
        var cursor = 0;
        while (true)
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 200, candidate =>
            {
                var endpoint = candidate.ToEndpoint();
                Assert.Equal(ProxyProtocol.Http, endpoint.Protocol);
                Assert.True(ports.Add(endpoint.Port));
                return true;
            });
            cursor = window.NextIndex;
            if (window.Completed) break;
        }
        Assert.Equal(1201, snapshot.Count);
        Assert.Equal(Enumerable.Range(1000, 1201), ports.Order());
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/parserpp/ip_ports/main/proxy.list")]
    [InlineData("https://raw.githubusercontent.com:444/parserpp/ip_ports/main/proxy.list")]
    [InlineData("https://raw.githubusercontent.com.evil.test/parserpp/ip_ports/main/proxy.list")]
    [InlineData("https://user@raw.githubusercontent.com/parserpp/ip_ports/main/proxy.list")]
    [InlineData(Ndjson + "?key=example")]
    [InlineData(Ndjson + "#fragment")]
    [InlineData("https://raw.githubusercontent.com/parserpp/ip_ports/main/proxyinfo.txt")]
    [InlineData("https://raw.githubusercontent.com/other/project/main/proxy.list")]
    public void RestrictsTransformationToCanonicalPublisherUrls(string url)
    {
        Assert.False(ParserPpFeedAdapter.Supports(url));
        Assert.Throws<InvalidDataException>(() => ParserPpFeedAdapter.Extract(url, HttpRow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"host\":\"8.8.8.8\",\"port\":80}")]
    [InlineData("{\"host\":\"127.0.0.1\",\"port\":80,\"type\":\"http\"}")]
    [InlineData("{\"host\":\"8.8.8.8\",\"port\":80,\"type\":\"ftp\"}")]
    [InlineData("{\"host\":\"8.8.8.8\",\"port\":80,\"type\":\"http\",\"password\":\"secret\"}")]
    [InlineData("{\"host\":\"8.8.8.8\",\"port\":80,\"type\":\"http\",\"username\":false}")]
    [InlineData("{\"host\":\"8.8.8.8\",\"port\":80,\"type\":\"http\",\"password\":\" \"}")]
    [InlineData("{\"host\":\"8.8.8.8\",\"host\":\"1.1.1.1\",\"port\":80,\"type\":\"http\"}")]
    [InlineData(HttpRow + "\nnot-json")]
    [InlineData("<html>8.8.8.8:80</html>")]
    public void RejectsUnknownCredentialedOrMalformedNdjsonAsWholeFeed(string content) =>
        Assert.Throws<InvalidDataException>(() => ParserPpFeedAdapter.Extract(Ndjson, content));

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"http_transparent\":[]}")]
    [InlineData("{\"data\":[" + HttpRow + "]}")]
    [InlineData("{\"socks5_transparent\":[" + HttpRow + "]}")]
    [InlineData("{\"http_other\":[" + HttpRow + "]}")]
    [InlineData("{\"http_transparent\":{\"row\":" + HttpRow + "}}")]
    [InlineData("{\"http_transparent\":[" + HttpRow + "],\"http_transparent\":[]}")]
    public void RejectsUnknownGroupsDuplicateGroupsAndProtocolMismatches(string content) =>
        Assert.Throws<InvalidDataException>(() => ParserPpFeedAdapter.Extract(Grouped, content));

    [Fact]
    public void RejectsExcessiveJsonDepth()
    {
        var body = HttpRow[..^1] + ",\"metadata\":" + new string('[', 40) + "0" + new string(']', 40) + "}";
        Assert.Throws<InvalidDataException>(() => ParserPpFeedAdapter.Extract(Ndjson, body));
    }

    [Theory]
    [InlineData(ProxyProtocol.Http, false)]
    [InlineData(ProxyProtocol.Https, false)]
    [InlineData(ProxyProtocol.Socks4, false)]
    [InlineData(ProxyProtocol.Socks5, false)]
    [InlineData(ProxyProtocol.HttpTls, false)]
    [InlineData(ProxyProtocol.HttpTlsUnverified, false)]
    [InlineData(ProxyProtocol.Http, true)]
    [InlineData(ProxyProtocol.Https, true)]
    [InlineData(ProxyProtocol.Socks4, true)]
    [InlineData(ProxyProtocol.Socks5, true)]
    [InlineData(ProxyProtocol.HttpTls, true)]
    [InlineData(ProxyProtocol.HttpTlsUnverified, true)]
    public void RespectsConfiguredHttpsWireModeForBothPublisherShapes(ProxyProtocol fallback, bool grouped)
    {
        var row = HttpRow.Replace("http", "https", StringComparison.Ordinal);
        var content = grouped ? "{\"https_transparent\":[" + row + "]}" : row;
        var converted = ParserPpFeedAdapter.Extract(grouped ? Grouped : Ndjson, content);
        var expected = fallback is ProxyProtocol.HttpTls or ProxyProtocol.HttpTlsUnverified ? fallback : ProxyProtocol.Https;
        Assert.Equal(expected, Assert.Single(SourceFeedParser.ParseRequired(converted, fallback)).Protocol);
        var snapshot = ProxyCandidateSnapshotCodec.Encode(converted, fallback);
        var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 200, candidate =>
        {
            Assert.Equal(expected, candidate.ToEndpoint().Protocol);
            return true;
        });
        Assert.True(window.Completed);
    }

    [Theory]
    [InlineData(Ndjson, HttpRow)]
    [InlineData(Grouped, "{\"http_transparent\":[" + HttpRow + "]}")]
    [InlineData(Grouped, HttpRow)]
    [InlineData(Grouped, HttpRow + "\n" + HttpRow)]
    public async Task CollectorPreservesValidatorsAndSuccessfulRefreshDelay(string url, string content)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        response.Headers.ETag = new EntityTagHeaderValue("\"typed-feed\"");
        using var client = new HttpClient(new Handler(response));
        using var collector = CreateCollector();
        var fetched = await collector.FetchSourceStateAsync(client, url, null, null, CancellationToken.None);
        Assert.Equal("\"typed-feed\"", fetched.HttpETag);
        Assert.False(fetched.NotModified);
        Assert.Single(SourceFeedParser.ParseRequired(fetched.Content!, ProxyProtocol.Socks5));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddMinutes(30), SourceFetchSchedule.NextSuccessAttempt(url, now));
    }

    [Fact]
    public async Task CollectorPreserves304WithoutParsingMissingBody()
    {
        using var client = new HttpClient(new Handler(new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var collector = CreateCollector();
        var fetched = await collector.FetchSourceStateAsync(client, Ndjson, "\"old\"", null, CancellationToken.None);
        Assert.True(fetched.NotModified);
        Assert.Null(fetched.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    public async Task CollectorHonorsRateLimitsAndRejectsForeignRedirects(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);
        if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://1.1.1.1/other");
        else response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));
        using var client = new HttpClient(new Handler(response));
        using var collector = CreateCollector();
        if (status == HttpStatusCode.Found)
            await Assert.ThrowsAsync<HttpRequestException>(() => collector.FetchSourceStateAsync(client, Ndjson, null, null, CancellationToken.None));
        else
            await Assert.ThrowsAsync<SourceRateLimitException>(() => collector.FetchSourceStateAsync(client, Ndjson, null, null, CancellationToken.None));
    }

    private static ProxyCollector CreateCollector() => new(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
