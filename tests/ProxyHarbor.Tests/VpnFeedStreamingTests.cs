using System.Text;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnFeedStreamingTests
{
    [Fact]
    public void CallerIndexedStreamDecodesSubscriptionAndDeliversEveryUriOccurrence()
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "vless://first@EXAMPLE.com:443\nvless://last@example.com:443\ntrojan://id@8.8.8.8:443"));
        var received = new List<VpnCandidate>();
        Assert.Equal(3, VpnFeedParser.ParseRecordsTo(body, VpnProtocol.Vless, 10, received.Add));
        Assert.Equal("vless://first@EXAMPLE.com:443", received[0].ConnectionUri);
        Assert.Equal("vless://last@example.com:443", received[1].ConnectionUri);
        Assert.Equal(VpnProtocol.Trojan, received[2].Protocol);
        Assert.Equal(received[0].Host, received[1].Host);
    }

    [Fact]
    public void CallerIndexedStreamRejectsDuplicateFloodBeforeOverBudgetCallback()
    {
        var calls = 0;
        Assert.Throws<InvalidDataException>(() => VpnFeedParser.ParseRecordsTo(
            "vless://first@8.8.8.8:443\nvless://last@8.8.8.8:443", VpnProtocol.Vless, 1, _ => calls++));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CallerIndexedStreamValidatesArgumentsAndEmptyBodyWithoutInvokingCallback()
    {
        Assert.Throws<ArgumentNullException>(() => VpnFeedParser.ParseRecordsTo(null!, VpnProtocol.Vless, 1, _ => { }));
        Assert.Throws<ArgumentNullException>(() => VpnFeedParser.ParseRecordsTo("", VpnProtocol.Vless, 1, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => VpnFeedParser.ParseRecordsTo("", VpnProtocol.Vless, 0, _ => { }));
        Assert.Equal(0, VpnFeedParser.ParseRecordsTo("", VpnProtocol.Vless, 1, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void CompleteStreamReachesBeyondCollectorPrefixLimit()
    {
        var body = string.Join('\n', Enumerable.Range(1, 15_282).Select(port => $"vless://id@8.8.8.8:{port}"));
        var calls = 0;
        VpnCandidate last = default;
        var summary = VpnFeedParser.ParseAllTo(body, VpnProtocol.Vless, 20_000, candidate =>
        {
            calls++;
            last = candidate;
        });
        Assert.Equal(new VpnParseSummary(15_282, 15_282), summary);
        Assert.Equal(15_282, calls);
        Assert.Equal(15_282, last.Port);
        Assert.Equal($"vless://id@8.8.8.8:15282", last.ConnectionUri);
    }

    [Fact]
    public void StreamPreservesLaterUriUpdatesForSameCanonicalEndpoint()
    {
        const string body = "vless://first@EXAMPLE.com:443\nvless://second@example.com:443#new";
        var records = new List<VpnCandidate>();
        var summary = VpnFeedParser.ParseAllTo(body, VpnProtocol.Vless, 10, records.Add);
        Assert.Equal(new VpnParseSummary(1, 2), summary);
        Assert.Equal(2, records.Count);
        Assert.All(records, candidate => Assert.Equal("example.com", candidate.Host));
        Assert.EndsWith("#new", records[1].ConnectionUri);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordBudgetRejectsExcessIncludingDuplicateFlood(bool duplicates)
    {
        var body = string.Join('\n', Enumerable.Range(1, 3)
            .Select(index => $"trojan://id@8.8.8.8:{(duplicates ? 443 : 443 + index)}"));
        var calls = 0;
        Assert.Throws<InvalidDataException>(() => VpnFeedParser.ParseAllTo(body, VpnProtocol.Trojan, 2, _ => calls++));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(VpnProtocol.OpenVpn, "remote 8.8.8.8 1194 udp\nremote 1.1.1.1 443 tcp")]
    [InlineData(VpnProtocol.WireGuard, "Endpoint = 8.8.8.8:51820\nEndpoint = 1.1.1.1:51820")]
    [InlineData(VpnProtocol.Vless, "vless://id@8.8.8.8:443\ntrojan://id@1.1.1.1:8443")]
    public void StreamRetainsExistingConfigurationAndSubscriptionSemantics(VpnProtocol fallback, string body)
    {
        var expected = VpnFeedParser.Parse(body, fallback, 100);
        var records = new List<VpnCandidate>();
        var summary = VpnFeedParser.ParseAllTo(body, fallback, 100, records.Add);
        Assert.Equal(expected, records);
        Assert.Equal(expected.Count, summary.UniqueCount);
        Assert.Equal(expected.Count, summary.RecordCount);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        records.Clear();
        Assert.Equal(summary, VpnFeedParser.ParseAllTo(encoded, fallback, 100, records.Add));
        Assert.Equal(expected, records);
    }

    [Fact]
    public void StreamRejectsUnsafeAndMalformedRecordsWithoutLosingHealthyNeighbors()
    {
        const string body = "vless://id@127.0.0.1:443\nwireguard://id@localhost:51820\n" +
            "vless://id@8.8.8.8:443#before\0after\nnot-a-proxy\nvless://id@1.1.1.1:443";
        var records = new List<VpnCandidate>();
        Assert.Equal(new VpnParseSummary(1, 1), VpnFeedParser.ParseAllTo(body, VpnProtocol.Vless, 10, records.Add));
        Assert.Equal("1.1.1.1", Assert.Single(records).Host);
    }

    [Fact]
    public void ModernVmessInMixedFeedRetainsItsExplicitProtocol()
    {
        const string link = "vmess://public-id@1.1.1.1:443?type=tcp";
        var candidate = Assert.Single(VpnFeedParser.Parse(link, VpnProtocol.Vless));
        Assert.Equal(VpnProtocol.Vmess, candidate.Protocol);
        var records = new List<VpnCandidate>();
        VpnFeedParser.ParseAllTo(link, VpnProtocol.Vless, 10, records.Add);
        Assert.Equal(candidate, Assert.Single(records));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks5")]
    [InlineData("ssr")]
    [InlineData("unsupported")]
    public void UnsupportedUriSchemeCannotMasqueradeAsFallbackVpnProtocol(string scheme)
    {
        var body = $"{scheme}://id@8.8.8.8:443\nvless://id@1.1.1.1:443";
        var candidate = Assert.Single(VpnFeedParser.Parse(body, VpnProtocol.Vless));
        Assert.Equal("1.1.1.1", candidate.Host);
        var records = new List<VpnCandidate>();
        Assert.Equal(new VpnParseSummary(1, 1), VpnFeedParser.ParseAllTo(body, VpnProtocol.Vless, 10, records.Add));
        Assert.Equal(candidate, Assert.Single(records));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("{\"add\":false,\"port\":443}")]
    [InlineData("{\"add\":{},\"port\":443}")]
    [InlineData("{\"add\":\"8.8.8.8\",\"port\":[]}")]
    [InlineData("{\"add\":\"8.8.8.8\",\"port\":true}")]
    public void MalformedVmessJsonCannotAbortHealthyNeighbors(string json)
    {
        var body = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\n" +
            "vless://id@1.1.1.1:443";
        Assert.Equal("1.1.1.1", Assert.Single(VpnFeedParser.Parse(body, VpnProtocol.Vless)).Host);
        var records = new List<VpnCandidate>();
        Assert.Equal(new VpnParseSummary(1, 1), VpnFeedParser.ParseAllTo(body, VpnProtocol.Vless, 10, records.Add));
        Assert.Equal("1.1.1.1", Assert.Single(records).Host);
    }

    [Theory]
    [InlineData(VpnProtocol.OpenVpn, " \tremote 8.8.8.8 1194 udp \r\n \nremote 1.1.1.1 443 tcp ")]
    [InlineData(VpnProtocol.WireGuard, " \tEndpoint = 8.8.8.8:51820 \r\n \n Endpoint = 1.1.1.1:51820 ")]
    public void SpecializedStreamsPreserveWhitespaceAndWindowsLineEndings(VpnProtocol fallback, string body)
    {
        var records = new List<VpnCandidate>();
        Assert.Equal(new VpnParseSummary(2, 2), VpnFeedParser.ParseAllTo(body, fallback, 10, records.Add));
        Assert.Equal(2, VpnFeedParser.Parse(body, fallback, 10).Count);
    }
}
