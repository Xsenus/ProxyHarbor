using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class Au1rxxSingBoxFeedAdapterTests
{
    private const string Prefix = "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/protocol/";
    private static string Url(string partition) => Prefix + partition + "/singbox-0001.json";
    private static string Body(string rows) => "{\"outbounds\":[" + rows + "],\"route\":{}}";
    private const string HttpRow = "{\"type\":\"http\",\"tag\":\"proxy\",\"server\":\"8.8.8.8\",\"server_port\":8080}";

    [Theory]
    [InlineData("http", "http", "", 1)]
    [InlineData("socks4", "socks", ",\"version\":\"4\"", 2)]
    [InlineData("socks5", "socks", ",\"version\":\"5\"", 3)]
    [InlineData("https", "http", ",\"tls\":{\"enabled\":true,\"insecure\":false}", 4)]
    [InlineData("https", "http", ",\"tls\":{\"enabled\":true,\"insecure\":true}", 5)]
    public void PreservesExplicitWireProtocol(string partition, string type, string extra, int protocol)
    {
        var row = HttpRow.Replace("\"http\"", JsonSerializer.Serialize(type), StringComparison.Ordinal).Replace("8080}", "8080" + extra + "}", StringComparison.Ordinal);
        var extraction = Au1rxxSingBoxFeedAdapter.Extract(Url(partition), Body(row));
        var parsed = Assert.Single(SourceFeedParser.ParseRequired(extraction.Content, ProxyProtocol.Http));
        Assert.Equal(protocol, (int)parsed.Protocol);
        Assert.Empty(extraction.HeldProfiles);
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/protocol/http/singbox-0001.json")]
    [InlineData(Prefix + "http/singbox-0000.json")]
    [InlineData(Prefix + "http/singbox-0001.json?token=test")]
    [InlineData(Prefix + "http/singbox-0001.json#fragment")]
    [InlineData(Prefix + "ftp/singbox-0001.json")]
    [InlineData(Prefix + "http/other.json")]
    [InlineData("https://raw.githubusercontent.com.evil.test/Au1rxx/free-vpn-subscriptions/main/output/protocol/http/singbox-0001.json")]
    public void RejectsUnreviewedUrls(string url) => Assert.False(Au1rxxSingBoxFeedAdapter.Supports(url));

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"outbounds\":[],\"route\":{}}")]
    [InlineData("{\"outbounds\":[],\"outbounds\":[],\"route\":{}}")]
    public void RejectsMalformedOrEmptyEnvelope(string body) => Assert.Throws<InvalidDataException>(() => Au1rxxSingBoxFeedAdapter.Extract(Url("http"), body));

    [Theory]
    [InlineData(",\"username\":\"example-user\",\"password\":\"example-password\"", false)]
    [InlineData("", true)]
    public void PreservesHeldOriginalProfileWithoutCredentialStripping(string fields, bool dns)
    {
        var heldRow = HttpRow.Replace("\"proxy\"", "\"held\"", StringComparison.Ordinal).Replace("8080}", "8080" + fields + "}", StringComparison.Ordinal);
        if (dns) heldRow = heldRow.Replace("8.8.8.8", "proxy.example.org", StringComparison.Ordinal);
        var result = Au1rxxSingBoxFeedAdapter.Extract(Url("http"), Body(HttpRow + "," + heldRow));
        var held = Assert.Single(result.HeldProfiles);
        Assert.Equal(dns, held.Dns);
        Assert.Equal(!dns, held.Authenticated);
        Assert.Equal(heldRow, held.OriginalProfile.GetRawText());
        Assert.Single(SourceFeedParser.ParseRequired(result.Content, ProxyProtocol.Http));
        Assert.DoesNotContain("example-", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("server_port", "0")]
    [InlineData("server_port", "65536")]
    [InlineData("server_port", "false")]
    [InlineData("server_port", "\"8080\"")]
    [InlineData("username", "false")]
    [InlineData("path", "\"/custom\"")]
    [InlineData("headers", "{}")]
    [InlineData("detour", "\"another\"")]
    [InlineData("version", "2")]
    public void RejectsAmbiguousOrUnsupportedSettings(string name, string value)
    {
        var row = name == "server_port" ? HttpRow.Replace("8080", value, StringComparison.Ordinal) : HttpRow[..^1] + "," + JsonSerializer.Serialize(name) + ":" + value + "}";
        Assert.Throws<InvalidDataException>(() => Au1rxxSingBoxFeedAdapter.Extract(Url("http"), Body(row)));
    }

    [Fact]
    public void DoesNotTreatMetadataOrDuplicatePropertiesAsEndpoints()
    {
        var result = Au1rxxSingBoxFeedAdapter.Extract(Url("http"), Body(HttpRow + ", {\"type\":\"direct\",\"tag\":\"direct\"}"));
        Assert.Equal(1, result.MetadataRows);
        Assert.Single(SourceFeedParser.ParseRequired(result.Content, ProxyProtocol.Http));
        var duplicate = HttpRow[..^1] + ",\"server\":\"1.1.1.1\"}";
        Assert.Throws<InvalidDataException>(() => Au1rxxSingBoxFeedAdapter.Extract(Url("http"), Body(duplicate)));
    }

    [Fact]
    public void HoldsCustomTlsServerNameWithoutDowngradingTls()
    {
        var common = HttpRow[..^1] + ",\"tls\":{\"enabled\":true,\"insecure\":false}}";
        var custom = common.Replace("\"proxy\"", "\"custom\"", StringComparison.Ordinal).Replace("\"insecure\":false", "\"insecure\":false,\"server_name\":\"tls.example.org\"", StringComparison.Ordinal);
        var result = Au1rxxSingBoxFeedAdapter.Extract(Url("https"), Body(common + "," + custom));
        Assert.True(Assert.Single(result.HeldProfiles).CustomTls);
        Assert.Equal(ProxyProtocol.HttpTls, Assert.Single(SourceFeedParser.ParseRequired(result.Content, ProxyProtocol.Http)).Protocol);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task CollectorPreservesConditionalResponseAndUsesAdapter(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status) { Content = new StringContent(Body(HttpRow)) };
        response.Headers.ETag = new EntityTagHeaderValue("\"singbox-test\"");
        using var client = new HttpClient(new Handler(response));
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
        var result = await collector.FetchSourceStateAsync(client, Url("http"), "\"singbox-test\"", null, CancellationToken.None);
        Assert.Equal(status == HttpStatusCode.NotModified, result.NotModified);
        if (result.NotModified) Assert.Null(result.Content);
        else Assert.Equal(ProxyProtocol.Https, Assert.Single(SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http)).Protocol);
        Assert.Equal("\"singbox-test\"", result.HttpETag);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddMinutes(30), SourceFetchSchedule.NextSuccessAttempt(Url("http"), now));
        Assert.Equal(1, ProxySourceImportStore.ParserVersionForUrl(Url("http")));
    }

    [Fact]
    public void AllUnsupportedProfilesRemainInspectableButCannotBecomeAnEmptySuccess()
    {
        var row = HttpRow.Replace("8.8.8.8", "proxy.example.org", StringComparison.Ordinal);
        var result = Au1rxxSingBoxFeedAdapter.Extract(Url("http"), Body(row));
        Assert.True(Assert.Single(result.HeldProfiles).Dns);
        Assert.Throws<InvalidDataException>(() => SourceFeedParser.ParseRequired(result.Content, ProxyProtocol.Http));
    }

    [Fact]
    public void FullSnapshotRetainsEveryWindowBeyondCollectorBatchSize()
    {
        var rows = Enumerable.Range(1000, 801).Select(port => HttpRow.Replace("\"proxy\"", "\"proxy-" + port + "\"", StringComparison.Ordinal).Replace("8080", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var result = Au1rxxSingBoxFeedAdapter.Extract(Url("http"), Body(string.Join(',', rows)));
        var snapshot = ProxyCandidateSnapshotCodec.Encode(result.Content, ProxyProtocol.Http);
        var seen = new HashSet<ProxyCandidateKey>();
        var cursor = 0;
        while (cursor < snapshot.Count)
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 200, key => seen.Add(key));
            Assert.True(window.NextIndex > cursor);
            cursor = window.NextIndex;
        }
        Assert.Equal(801, snapshot.Count);
        Assert.Equal(801, seen.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    public async Task CollectorHonorsQuotaAndRejectsCrossOriginRedirect(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);
        if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://1.1.1.1/export");
        else response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        using var client = new HttpClient(new Handler(response));
        using var collector = new ProxyCollector(null!, null!, Options.Create(new CollectorOptions { SourceRetryCount = 2 }), NullLogger<ProxyCollector>.Instance);
        if (status == HttpStatusCode.Found)
            await Assert.ThrowsAsync<HttpRequestException>(() => collector.FetchSourceStateAsync(client, Url("http"), null, null, CancellationToken.None));
        else
            await Assert.ThrowsAsync<SourceRateLimitException>(() => collector.FetchSourceStateAsync(client, Url("http"), null, null, CancellationToken.None));
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
