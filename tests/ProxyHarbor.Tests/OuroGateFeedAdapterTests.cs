using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class OuroGateFeedAdapterTests
{
    private const string Url = "https://raw.githubusercontent.com/NotoriusP/ourogate-proxies/main/proxies_public.json";
    private const string Body = "{\"updatedAt\":\"2026-10-08T04:00:50.791Z\",\"source\":\"ourogate-proxy\",\"countries\":[\"US\"],\"proxies\":{\"US\":[\"socks5://8.8.8.8:1080\"]}}";

    [Theory]
    [InlineData(Url)]
    [InlineData("https://raw.githubusercontent.com/NotoriusP/ourogate-proxies/refs/heads/main/proxies_public.json")]
    public void PreservesAllPublishedProtocolsAndDeduplicatesAcrossCountries(string url)
    {
        var body = Body.Replace("[\"US\"]", "[\"US\",\"GB\"]", StringComparison.Ordinal)
            .Replace("\"socks5://8.8.8.8:1080\"]", "\"socks5://8.8.8.8:1080\",\"http://1.1.1.1:8080\",\"socks4://[2606:4700:4700::1111]:1080\"],\"GB\":[\"socks5://8.8.8.8:1080\"]", StringComparison.Ordinal);
        var parsed = SourceFeedParser.ParseRequired(OuroGateFeedAdapter.Extract(url, "\uFEFF" + body), ProxyProtocol.HttpTls);
        Assert.Equal(3, parsed.Count);
        Assert.Equal(new[] { ProxyProtocol.Http, ProxyProtocol.Socks4, ProxyProtocol.Socks5 }, parsed.Select(p => p.Protocol).Order());
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/NotoriusP/ourogate-proxies/main/proxies_public.json")]
    [InlineData("https://raw.githubusercontent.com:444/NotoriusP/ourogate-proxies/main/proxies_public.json")]
    [InlineData("https://raw.githubusercontent.com.evil.test/NotoriusP/ourogate-proxies/main/proxies_public.json")]
    [InlineData("https://user@raw.githubusercontent.com/NotoriusP/ourogate-proxies/main/proxies_public.json")]
    [InlineData(Url + "?token=example")]
    [InlineData(Url + "#fragment")]
    [InlineData("https://raw.githubusercontent.com/other/project/main/proxies_public.json")]
    public void RejectsNoncanonicalUrls(string url)
    {
        Assert.False(OuroGateFeedAdapter.Supports(url));
        Assert.Throws<InvalidDataException>(() => OuroGateFeedAdapter.Extract(url, Body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("<html>8.8.8.8:1080</html>")]
    [InlineData("{\"source\":\"ourogate-proxy\"}")]
    public void RejectsMalformedEnvelopes(string body) =>
        Assert.Throws<InvalidDataException>(() => OuroGateFeedAdapter.Extract(Url, body));

    [Theory]
    [InlineData("socks5://user:password@8.8.8.8:1080")]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("socks5://10.0.0.1:1080")]
    [InlineData("socks5://010.0.0.1:1080")]
    [InlineData("socks5://example.org:1080")]
    [InlineData("socks5://8.8.8.8:0")]
    [InlineData("socks5://8.8.8.8:65536")]
    [InlineData("socks5://8.8.8.8:1080/path")]
    [InlineData("socks5://8.8.8.8:1080?key=example")]
    [InlineData("https://8.8.8.8:1080")]
    [InlineData("http+tls-unverified://8.8.8.8:1080")]
    [InlineData("ftp://8.8.8.8:1080")]
    [InlineData("8.8.8.8:1080")]
    public void RejectsUnsafeCredentialedOrAmbiguousEndpointsAsWholeFeed(string endpoint) =>
        Assert.Throws<InvalidDataException>(() => OuroGateFeedAdapter.Extract(Url, Body.Replace("socks5://8.8.8.8:1080", endpoint, StringComparison.Ordinal)));

    [Theory]
    [InlineData("\"source\":\"ourogate-proxy\"", "\"source\":\"other\"")]
    [InlineData("\"updatedAt\":\"2026-10-08T04:00:50.791Z\"", "\"updatedAt\":false")]
    [InlineData("2026-10-08T04:00:50.791Z", "not-a-date")]
    [InlineData("\"countries\":[\"US\"]", "\"countries\":[\"US\",\"US\"]")]
    [InlineData("\"countries\":[\"US\"]", "\"countries\":[\"US\",\"GB\"]")]
    [InlineData("\"countries\":[\"US\"]", "\"countries\":[\"us\"]")]
    [InlineData("\"countries\":[\"US\"]", "\"countries\":[7]")]
    [InlineData("\"countries\":[\"US\"]", "\"countries\":{}")]
    [InlineData("\"proxies\":{", "\"extra\":true,\"proxies\":{")]
    [InlineData("\"proxies\":{", "\"source\":\"ourogate-proxy\",\"proxies\":{")]
    [InlineData("\"socks5://8.8.8.8:1080\"", "false")]
    [InlineData("[\"socks5://8.8.8.8:1080\"]", "[]")]
    [InlineData("[\"socks5://8.8.8.8:1080\"]", "{}")]
    [InlineData("\"proxies\":{\"US\":", "\"proxies\":{\"GB\":")]
    [InlineData("\"proxies\":{", "\"proxies\":{\"US\":[],")]
    public void RejectsDuplicateUnknownOrInconsistentMetadata(string before, string after) =>
        Assert.Throws<InvalidDataException>(() => OuroGateFeedAdapter.Extract(Url, Body.Replace(before, after, StringComparison.Ordinal)));

    [Fact]
    public void ReplaysCompleteSnapshotAcrossImportWindows()
    {
        var rows = string.Join(',', Enumerable.Range(1000, 801).Select(port => JsonSerializer.Serialize($"socks5://8.8.8.8:{port}")));
        var converted = OuroGateFeedAdapter.Extract(Url, Body.Replace("\"socks5://8.8.8.8:1080\"", rows, StringComparison.Ordinal));
        var snapshot = ProxyCandidateSnapshotCodec.Encode(converted, ProxyProtocol.Http);
        var accepted = new HashSet<int>();
        var cursor = 0;
        while (true)
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 200, candidate =>
            {
                Assert.Equal(ProxyProtocol.Socks5, candidate.ToEndpoint().Protocol);
                Assert.True(accepted.Add(candidate.ToEndpoint().Port));
                return true;
            });
            cursor = window.NextIndex;
            if (window.Completed) break;
        }
        Assert.Equal(801, snapshot.Count);
        Assert.Equal(Enumerable.Range(1000, 801), accepted.Order());
    }

    [Fact]
    public void MixedProfilesPreserveAllPublicIpRowsAndReportUnsupportedProfiles()
    {
        var rows = Enumerable.Range(1000, 59).Select(port => $"socks5://8.8.8.8:{port}")
            .Concat(Enumerable.Range(2000, 5).Select(port => $"socks5://example-user:example-password@1.1.1.1:{port}"))
            .Concat(Enumerable.Range(3000, 6).Select(port => $"socks5://example-user:example-password@proxy.example.org:{port}"));
        var extraction = OuroGateFeedAdapter.ExtractWithReport(Url, WithRows(rows));
        Assert.Equal(59, extraction.AcceptedRows);
        Assert.Equal(11, extraction.UnsupportedProfiles);
        Assert.Equal(11, extraction.AuthenticatedProfiles);
        Assert.Equal(6, extraction.DnsProfiles);
        var parsed = SourceFeedParser.ParseRequired(extraction.Content, ProxyProtocol.HttpTls);
        Assert.Equal(59, parsed.Count);
        Assert.All(parsed, candidate => Assert.Equal(ProxyProtocol.Socks5, candidate.Protocol));
        Assert.DoesNotContain("example-user", extraction.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("example-password", extraction.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy.example.org", extraction.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("1.1.1.1", extraction.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedRowsSurviveEverySnapshotWindowWithoutLosingSupportedTail()
    {
        var rows = Enumerable.Range(1000, 501).Select(port => $"socks4://8.8.8.8:{port}")
            .Prepend("socks5://example-user:example-password@1.1.1.1:1080")
            .Append("socks5://proxy.example.org:1080");
        var extraction = OuroGateFeedAdapter.ExtractWithReport(Url, WithRows(rows));
        Assert.Equal(501, extraction.AcceptedRows);
        Assert.Equal(2, extraction.UnsupportedProfiles);
        Assert.Equal(1, extraction.AuthenticatedProfiles);
        Assert.Equal(1, extraction.DnsProfiles);
        var snapshot = ProxyCandidateSnapshotCodec.Encode(extraction.Content, ProxyProtocol.Http);
        var ports = new HashSet<int>();
        var cursor = 0;
        do
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 200, candidate =>
            {
                Assert.Equal(ProxyProtocol.Socks4, candidate.ToEndpoint().Protocol);
                Assert.True(ports.Add(candidate.ToEndpoint().Port));
                return true;
            });
            Assert.True(window.NextIndex > cursor);
            cursor = window.NextIndex;
        } while (cursor < snapshot.Count);
        Assert.Equal(Enumerable.Range(1000, 501), ports.Order());
    }

    [Theory]
    [InlineData("socks5://example-user:example-password@127.0.0.1:1080")]
    [InlineData("socks5://example-user:example-password@10.0.0.1:1080")]
    [InlineData("socks5://example-user:example-password@010.0.0.1:1080")]
    [InlineData("socks5://example-user:example-password@1.1.1.01:1080")]
    [InlineData("socks5://example-user:example-password@2130706433:1080")]
    [InlineData("socks5://example-user:example-password@0x7f000001:1080")]
    [InlineData("socks5://example-user:example-password@localhost:1080")]
    [InlineData("socks5://example-user:example-password@proxy.local:1080")]
    [InlineData("socks5://example-user:example-password@proxy.internal:1080")]
    [InlineData("socks5://example-user@8.8.8.8:1080")]
    [InlineData("socks5://:example-password@8.8.8.8:1080")]
    [InlineData("socks5://example-user:@8.8.8.8:1080")]
    [InlineData("socks5://example-user:%invalid@8.8.8.8:1080")]
    [InlineData("socks5://example-user:example-password@8.8.8.8:1080/path")]
    [InlineData("socks5://example-user:example-password@8.8.8.8:1080?secret=example")]
    [InlineData("socks5://example-user:example-password@8.8.8.8:1080#fragment")]
    [InlineData("https://example-user:example-password@8.8.8.8:1080")]
    public void MixedPayloadStillRejectsUnsafeOrMalformedRows(string endpoint)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            OuroGateFeedAdapter.ExtractWithReport(Url, WithRows(["socks5://8.8.4.4:1080", endpoint])));
        Assert.DoesNotContain("example-password", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectorImportsSupportedMixedRowsAndLogsOnlyUnsupportedCounts()
    {
        var logger = new CaptureLogger();
        var body = WithRows(["socks4://8.8.8.8:1080", "socks5://example-user:example-password@proxy.example.org:1080"]);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.ETag = new EntityTagHeaderValue("\"mixed-country-map\"");
        using var client = new HttpClient(new Handler(response));
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), logger);
        var result = await collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None);
        Assert.Equal("\"mixed-country-map\"", result.HttpETag);
        Assert.Equal(ProxyProtocol.Socks4, Assert.Single(SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http)).Protocol);
        var message = Assert.Single(logger.Messages);
        Assert.Equal("OuroGateProfilesUnsupported", message.Event.Name);
        Assert.DoesNotContain("example-user", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("example-password", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy.example.org", message.Text, StringComparison.Ordinal);
        Assert.Contains("1", message.Text, StringComparison.Ordinal);
    }

    private static string WithRows(IEnumerable<string> rows) =>
        Body.Replace("\"socks5://8.8.8.8:1080\"", string.Join(',', rows.Select(row => JsonSerializer.Serialize(row))), StringComparison.Ordinal);

    private sealed class CaptureLogger : ILogger<ProxyCollector>
    {
        internal List<(EventId Event, string Text)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((eventId, formatter(state, exception)));
    }

    [Fact]
    public async Task CollectorTransformsRefreshAndPreservesConditionalValidators()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) };
        response.Headers.ETag = new EntityTagHeaderValue("\"country-map\"");
        using var client = new HttpClient(new Handler(response));
        using var collector = CreateCollector();
        var result = await collector.FetchSourceStateAsync(client, Url, null, null, CancellationToken.None);
        Assert.False(result.NotModified);
        Assert.Equal("\"country-map\"", result.HttpETag);
        Assert.Equal(ProxyProtocol.Socks5, Assert.Single(SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http)).Protocol);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddMinutes(30), SourceFetchSchedule.NextSuccessAttempt(Url, now));
    }

    [Fact]
    public async Task CollectorPreservesNotModifiedWithoutParsingBody()
    {
        using var client = new HttpClient(new Handler(new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var collector = CreateCollector();
        var result = await collector.FetchSourceStateAsync(client, Url, "\"cached\"", null, CancellationToken.None);
        Assert.True(result.NotModified);
        Assert.Null(result.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    public async Task CollectorHonorsRetryAfterAndRejectsForeignRedirects(HttpStatusCode status)
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
