using System.Text.Json;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Preserves legacy CONNECT identities while carrying explicit TLS-to-proxy profiles.</summary>
public sealed class TlsProxyProtocolTests
{
    [Fact]
    public void ExistingProtocolNumbersRemainStable()
    {
        Assert.Equal(0, (int)ProxyProtocol.Http);
        Assert.Equal(1, (int)ProxyProtocol.Https);
        Assert.Equal(2, (int)ProxyProtocol.Socks4);
        Assert.Equal(3, (int)ProxyProtocol.Socks5);
        Assert.Equal(4, (int)ProxyProtocol.HttpTls);
        Assert.Equal(5, (int)ProxyProtocol.HttpTlsUnverified);
    }

    [Fact]
    public void LegacyHeartbeatDoesNotImplyTlsCapability()
    {
        var heartbeat = JsonSerializer.Deserialize<CheckerHeartbeatRequest>("""{"Version":"legacy"}""");
        Assert.NotNull(heartbeat);
        Assert.False(heartbeat.SupportsTlsProxyTransport);
    }

    [Theory]
    [InlineData(ProxyProtocol.HttpTls)]
    [InlineData(ProxyProtocol.HttpTlsUnverified)]
    [InlineData(ProxyProtocol.Https)]
    public void HttpsRowsHonorExplicitSourceTransportWithoutReinterpretingLegacyFeeds(ProxyProtocol fallback)
    {
        Assert.Equal(fallback, Assert.Single(ProxyParser.Parse("https://8.8.8.8:443", fallback)).Protocol);
        var candidates = new List<ProxyCandidateKey>();
        JsonProxyFeedParser.TryParseTo("""[{"ip":"8.8.8.8","port":443,"protocol":"https"}]""", fallback, 10, candidates.Add);
        Assert.Equal(fallback, Assert.Single(candidates).Protocol);
    }

    [Theory]
    [InlineData("http+tls://8.8.8.8:443", ProxyProtocol.HttpTls)]
    [InlineData("http+tls-unverified://8.8.8.8:443", ProxyProtocol.HttpTlsUnverified)]
    public void ExplicitTlsProfilesSurviveSnapshotEncodingAndReplay(string body, ProxyProtocol expected)
    {
        var snapshot = ProxyCandidateSnapshotCodec.Encode(body, ProxyProtocol.Http);
        var keys = new List<ProxyCandidateKey>();
        var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 10, key => { keys.Add(key); return true; });
        Assert.True(window.Completed);
        Assert.Equal(expected, Assert.Single(keys).Protocol);
        Assert.True(ProxyParser.TryParseEndpoint(body, ProxyProtocol.Http, out var parsed));
        Assert.Equal(expected, parsed.Protocol);
    }

    [Theory]
    [InlineData(ProxyProtocol.Http, "http", false)]
    [InlineData(ProxyProtocol.Https, "http", false)]
    [InlineData(ProxyProtocol.HttpTls, "https", false)]
    [InlineData(ProxyProtocol.HttpTlsUnverified, "https", true)]
    public void PublicTransportUrlDistinguishesTlsFromLegacyCategory(
        ProxyProtocol protocol, string scheme, bool requiresUnverified)
    {
        var dto = ProxyDto.From(new ProxyEndpoint { Host = "2606:4700:4700::1111", Port = 443, Protocol = protocol });
        Assert.Equal(scheme + "://[2606:4700:4700::1111]:443", dto.Url);
        Assert.Equal(protocol, dto.Protocol);
        Assert.Equal(requiresUnverified, dto.RequiresUnverifiedProxyTls);
    }
}
