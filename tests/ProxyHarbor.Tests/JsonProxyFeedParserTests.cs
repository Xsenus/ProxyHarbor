using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class JsonProxyFeedParserTests
{
    [Fact]
    public void StructuredApiRecordsExpandSupportedProtocolsAndNormalizePort()
    {
        var parsed = SourceFeedParser.ParseRequired("""
            {"results":[{"ip_address":"8.8.8.8","port":8080,"protocols":["HTTP","https","HTTP"]},
            {"ip":"1.1.1.1","port":"1080","protocols":["socks5"]}]}
            """, ProxyProtocol.Socks4);
        Assert.Equal(
            [("8.8.8.8", 8080, ProxyProtocol.Http), ("8.8.8.8", 8080, ProxyProtocol.Https), ("1.1.1.1", 1080, ProxyProtocol.Socks5)], parsed);
    }

    [Fact]
    public void NestedPublicSearchEnvelopeReadsRecordsWithEmptyCredentialFields()
    {
        var parsed = SourceFeedParser.ParseRequired("""
            {"status":1,"data":{"data":[{"ip":"9.9.9.9","port":80,"protocol":"http","username":"","password":null}]}}
            """, ProxyProtocol.Socks5);
        Assert.Equal(("9.9.9.9", 80, ProxyProtocol.Http), Assert.Single(parsed));
    }

    [Fact]
    public void ExistingStringListWrapperPreservesExplicitSchemesAndDeduplication()
    {
        var parsed = SourceFeedParser.ParseBoundedRequired("""
            {"proxies":["socks5://8.8.8.8:1080","socks5://8.8.8.8:1080"]}
            """, ProxyProtocol.Http, 1);
        Assert.False(parsed.Truncated);
        Assert.Equal(("8.8.8.8", 1080, ProxyProtocol.Socks5), Assert.Single(parsed.Items));
    }

    [Fact]
    public void JsonLimitIsAcrossRecordsAndProtocols()
    {
        var parsed = SourceFeedParser.ParseBoundedRequired("""
            [{"ip":"8.8.8.8","port":80,"protocols":["http","https","socks5"]}]
            """, ProxyProtocol.Http, 2);
        Assert.Equal(2, parsed.Items.Count);
        Assert.True(parsed.Truncated);
    }

    [Theory]
    [InlineData("{\"message\":\"error at 8.8.8.8:80\"}")]
    [InlineData("{\"success\":false,\"data\":[\"8.8.8.8:80\"]}")]
    [InlineData("{\"status\":0,\"data\":[\"8.8.8.8:80\"]}")]
    [InlineData("[{\"ip\":\"8.8.8.8\",\"port\":80,\"protocol\":\"vmess\"}]")]
    [InlineData("[{\"ip\":\"8.8.8.8\",\"port\":80,\"protocol\":\"0\"}]")]
    [InlineData("[{\"ip\":\"8.8.8.8\",\"port\":80,\"username\":\"private-account\"}]")]
    [InlineData("[{\"ip\":\"8.8.8.8\",\"port\":80,\"password\":{\"value\":\"secret\"}}]")]
    [InlineData("[\"vmess://8.8.8.8:80\"]")]
    [InlineData("{\"data\":\"error at 8.8.8.8:80\"}")]
    [InlineData("[\"8.8.8.8:80 diagnostic suffix\"]")]
    [InlineData("[\"error at http://8.8.8.8:80\"]")]
    [InlineData("[{\"ip\":\"10.0.0.1\",\"port\":80}]")]
    [InlineData("[{\"ip\":\"010.0.0.1\",\"port\":80}]")]
    [InlineData("[{\"ip\":\"example.com\",\"port\":80}]")]
    [InlineData("[{\"ip\":\"8.8.8.8\",\"port\":\"80 x 9.9.9.9:80\"}]")]
    [InlineData("[{\"ip\":\"8.8.8.8\",\"port\":{\"error\":\"9.9.9.9:80\"}}]")]
    public void UnsupportedPrivateAndDiagnosticRecordsAreRejected(string body) =>
        Assert.Throws<InvalidDataException>(() => SourceFeedParser.ParseRequired(body, ProxyProtocol.Http));

    [Fact]
    public void MalformedJsonFailsWithoutFallingBackToEmbeddedAddresses()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SourceFeedParser.ParseRequired("{ broken 8.8.8.8:80 secret-value", ProxyProtocol.Http));
        Assert.DoesNotContain("secret-value", exception.Message);
        Assert.Contains("JSON", exception.Message);
    }

    [Fact]
    public void PlainIpv6FeedRemainsCompatible()
    {
        var parsed = SourceFeedParser.ParseRequired("[2606:4700:4700::1111]:443", ProxyProtocol.Https);
        Assert.Equal(("2606:4700:4700::1111", 443, ProxyProtocol.Https), Assert.Single(parsed));
    }

    [Fact]
    public void StructuredIpv6RecordUsesDefaultProtocolWhenAbsent()
    {
        var parsed = SourceFeedParser.ParseRequired("""
            [{"host":"2606:4700:4700::1111","port":443}]
            """, ProxyProtocol.Https);
        Assert.Equal(("2606:4700:4700::1111", 443, ProxyProtocol.Https), Assert.Single(parsed));
    }
}
