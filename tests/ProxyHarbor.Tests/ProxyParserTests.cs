using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует правила нормализации данных из недоверенных источников.</summary>
public sealed class ProxyParserTests
{
    [Fact]
    public void ParseRecognizesSchemesAndFallbackProtocol()
    {
        var result = ProxyParser.Parse("http://8.8.8.8:8080\n1.1.1.1:1080\nsocks5://example.com:9000", ProxyProtocol.Socks4);

        Assert.Contains(result, x => x == ("8.8.8.8", 8080, ProxyProtocol.Http));
        Assert.Contains(result, x => x == ("1.1.1.1", 1080, ProxyProtocol.Socks4));
        Assert.DoesNotContain(result, x => x.Host == "example.com");
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:20::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("3ffe::1")]
    [InlineData("3fff::1")]
    [InlineData("::2")]
    public void NetworkSafetyRejectsNonPublicAddresses(string value) =>
        Assert.False(NetworkSafety.IsPublicAddress(System.Net.IPAddress.Parse(value)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("192.0.1.1")]
    [InlineData("192.2.1.1")]
    [InlineData("2001:200::1")]
    [InlineData("2606:4700:4700::1111")]
    public void NetworkSafetyAcceptsPublicAddresses(string value) =>
        Assert.True(NetworkSafety.IsPublicAddress(System.Net.IPAddress.Parse(value)));

    [Fact]
    public void ParseRemovesDuplicatesAndUnsafeAddresses()
    {
        var result = ProxyParser.Parse("8.8.8.8:80\n8.8.8.8:80\n127.0.0.1:90\n192.168.1.2:80\n999.1.1.1:80\n8.8.8.8:99999", ProxyProtocol.Http);

        Assert.Single(result);
        Assert.Equal(("8.8.8.8", 80, ProxyProtocol.Http), result.Single());
    }

    [Fact]
    public void ParseHandlesNoiseWithoutThrowing()
    {
        var result = ProxyParser.Parse("<html>nothing useful</html>\n:// bad : abc", ProxyProtocol.Http);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseCanonicalizesEquivalentIpv6Addresses()
    {
        var result = ProxyParser.Parse("[2606:4700:4700:0:0:0:0:1111]:443\n[2606:4700:4700::1111]:443", ProxyProtocol.Https);

        var proxy = Assert.Single(result);
        Assert.Equal("2606:4700:4700::1111", proxy.Host);
        var entity = new ProxyEndpoint { Host = proxy.Host, Port = proxy.Port, Protocol = proxy.Protocol };
        Assert.Equal("https://[2606:4700:4700::1111]:443", entity.Key);
    }

    [Fact]
    public void ParseStopsAtConfiguredUniqueResultLimit()
    {
        var result = ProxyParser.Parse(
            "8.8.8.8:80\n8.8.8.8:80\n1.1.1.1:81\n9.9.9.9:82\n208.67.222.222:83",
            ProxyProtocol.Http,
            maxResults: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal(
            [("8.8.8.8", 80, ProxyProtocol.Http), ("1.1.1.1", 81, ProxyProtocol.Http), ("9.9.9.9", 82, ProxyProtocol.Http)],
            result);
    }

    [Fact]
    public void ParseContinuesAfterEndpointLikeHeaderNoiseAndUnsafeAddress()
    {
        const string content = """
            # Generated https://github.com/example/project -> 2026-08-09 08:40:27.418
            0.0.0.0:80
            1.0.171.213:8080
            8.8.8.8:443
            """;

        var result = ProxyParser.Parse(content, ProxyProtocol.Https);

        Assert.Equal(
            [("1.0.171.213", 8080, ProxyProtocol.Https), ("8.8.8.8", 443, ProxyProtocol.Https)],
            result);
    }

    [Fact]
    public void ParseRejectsNonPositiveResultLimit() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProxyParser.Parse("8.8.8.8:80", ProxyProtocol.Http, maxResults: 0));
}
