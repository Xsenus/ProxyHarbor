using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class JsonProxyFeedParserTests
{
    [Theory]
    [InlineData(ProxyProtocol.Http)]
    [InlineData(ProxyProtocol.Https)]
    [InlineData(ProxyProtocol.Socks5)]
    [InlineData(ProxyProtocol.HttpTls)]
    [InlineData(ProxyProtocol.HttpTlsUnverified)]
    public void CombinedHttpTypeUsesPlainHttpRegardlessOfFallback(ProxyProtocol fallback)
    {
        var parsed = SourceFeedParser.ParseRequired("""
            [{"ip":"8.8.8.8","port":"8080","type":"HTTP/HTTPS"},
             {"ip":"8.8.8.8","port":8080,"protocol":"http/https"},
             {"ip":"1.1.1.1","port":1080,"protocols":["Http/Https","socks5"]}]
            """, fallback);
        Assert.Equal(
            [("8.8.8.8", 8080, ProxyProtocol.Http), ("1.1.1.1", 1080, ProxyProtocol.Http),
                ("1.1.1.1", 1080, ProxyProtocol.Socks5)], parsed);
    }

    [Theory]
    [InlineData("{\"ip\":\"127.0.0.1\",\"port\":80,\"type\":\"HTTP/HTTPS\"}")]
    [InlineData("{\"ip\":\"10.1.2.3\",\"port\":80,\"type\":\"HTTP/HTTPS\"}")]
    [InlineData("{\"ip\":\"proxy.example\",\"port\":80,\"type\":\"HTTP/HTTPS\"}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":0,\"type\":\"HTTP/HTTPS\"}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":80,\"type\":\"HTTP/HTTPS\",\"password\":\"secret\"}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":80,\"type\":\"HTTP/HTTPS/SOCKS5\"}")]
    public void CombinedTypeRetainsRecordSafetyChecks(string invalidRecord)
    {
        var parsed = SourceFeedParser.ParseRequired(
            $"[{invalidRecord},{{\"ip\":\"1.1.1.1\",\"port\":8080,\"type\":\"HTTP/HTTPS\"}}]",
            ProxyProtocol.HttpTlsUnverified);
        Assert.Equal(("1.1.1.1", 8080, ProxyProtocol.Http), Assert.Single(parsed));
    }

    [Theory]
    [InlineData("ip")]
    [InlineData("ip_address")]
    [InlineData("host")]
    public void CombinedAddressAndPortPreservesExplicitProtocols(string field)
    {
        var body = $$"""
            [{"{{field}}":"8.8.8.8:1080","protocols":["HTTP","socks4","SOCKS5","HTTP"]},
             {"{{field}}":"[2606:4700:4700::1111]:443","protocol":"https"}]
            """;
        var parsed = SourceFeedParser.ParseRequired(body, ProxyProtocol.Http);
        Assert.Equal(
            [("8.8.8.8", 1080, ProxyProtocol.Http), ("8.8.8.8", 1080, ProxyProtocol.Socks4),
                ("8.8.8.8", 1080, ProxyProtocol.Socks5), ("2606:4700:4700::1111", 443, ProxyProtocol.Https)], parsed);
    }

    [Theory]
    [InlineData("{\"ip\":\"127.0.0.1:80\"}")]
    [InlineData("{\"ip\":\"10.1.2.3:80\"}")]
    [InlineData("{\"ip\":\"[::1]:80\"}")]
    [InlineData("{\"ip\":\"proxy.example:80\"}")]
    [InlineData("{\"ip\":\"http://8.8.8.8:80\"}")]
    [InlineData("{\"ip\":\"socks5://8.8.8.8:80\",\"protocol\":\"http\"}")]
    [InlineData("{\"ip\":\"user:password@8.8.8.8:80\"}")]
    [InlineData("{\"ip\":\"error at 8.8.8.8:80\"}")]
    [InlineData("{\"ip\":\"8.8.8.8:80/path\"}")]
    [InlineData("{\"ip\":\"8.8.8.8:0\"}")]
    [InlineData("{\"ip\":\"8.8.8.8:65536\"}")]
    [InlineData("{\"ip\":\"008.8.8.8:80\"}")]
    [InlineData("{\"ip\":\"8.8.8.8:80\",\"port\":443}")]
    [InlineData("{\"ip\":\"8.8.8.8:80\",\"port\":null}")]
    [InlineData("{\"ip\":\"8.8.8.8:80\",\"username\":\"user\"}")]
    [InlineData("{\"ip\":\"8.8.8.8:80\",\"password\":\"password\"}")]
    [InlineData("{\"ip\":\"8.8.8.8:80\",\"user\":false}")]
    [InlineData("{\"ip\":\"8.8.8.8:80\",\"pass\":123}")]
    [InlineData("{\"ip\":\"8.8.8.8:80\",\"protocol\":\"unsupported\"}")]
    public void UnsafeCombinedRecordDoesNotDiscardHealthyNeighbor(string record)
    {
        var parsed = SourceFeedParser.ParseRequired(
            $"[{record},{{\"ip\":\"1.1.1.1:443\",\"protocol\":\"https\"}}]", ProxyProtocol.Http);
        Assert.Equal(("1.1.1.1", 443, ProxyProtocol.Https), Assert.Single(parsed));
    }

    [Fact]
    public void CombinedRecordUsesFallbackAndRespectsGlobalBound()
    {
        var parsed = SourceFeedParser.ParseBoundedRequired("""
            [{"ip":"8.8.8.8:1080"},{"ip":"8.8.8.8:1080"},{"ip":"1.1.1.1:1080"}]
            """, ProxyProtocol.Socks5, 1);
        Assert.Equal(("8.8.8.8", 1080, ProxyProtocol.Socks5), Assert.Single(parsed.Items));
        Assert.True(parsed.Truncated);
    }

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

    [Theory]
    [InlineData("{\"ip\":null,\"port\":80}")]
    [InlineData("{\"ip\":123,\"port\":80}")]
    [InlineData("{\"ip\":\"8.8.8.8\"}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":null}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":true}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":[]}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":80.5}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":0}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":65536}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":\"+80\"}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":\" 80\"}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":80,\"user\":false}")]
    [InlineData("{\"ip\":\"8.8.8.8\",\"port\":80,\"pass\":123}")]
    public void InvalidRecordDoesNotDiscardHealthyNeighbor(string invalidRecord)
    {
        var parsed = SourceFeedParser.ParseRequired(
            $"[{invalidRecord},{{\"ip\":\"1.1.1.1\",\"port\":443}}]", ProxyProtocol.Https);
        Assert.Equal(("1.1.1.1", 443, ProxyProtocol.Https), Assert.Single(parsed));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"http\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[null,123,false,\"unsupported\"]")]
    public void ExplicitInvalidProtocolListDoesNotUseFallback(string protocols)
    {
        var body = $"[{{\"ip\":\"8.8.8.8\",\"port\":80,\"protocols\":{protocols}}}]";
        Assert.Throws<InvalidDataException>(() => SourceFeedParser.ParseRequired(body, ProxyProtocol.Http));
    }

    [Fact]
    public void ProtocolListSkipsInvalidEntriesAndKeepsSupportedProtocol()
    {
        var parsed = SourceFeedParser.ParseRequired("""
            [{"ip":"8.8.8.8","port":1080,"protocols":[null,123,false,"vmess","socks5"]}]
            """, ProxyProtocol.Http);
        Assert.Equal(("8.8.8.8", 1080, ProxyProtocol.Socks5), Assert.Single(parsed));
    }

    [Fact]
    public void TypeAliasAndEmptyCredentialAliasesRemainPublic()
    {
        var parsed = SourceFeedParser.ParseRequired("""
            [{"ip":"8.8.8.8","port":1080,"type":"socks4","user":null,"pass":""}]
            """, ProxyProtocol.Http);
        Assert.Equal(("8.8.8.8", 1080, ProxyProtocol.Socks4), Assert.Single(parsed));
    }

    [Theory]
    [InlineData("[8.8.8.8]:80\n9.9.9.9:80")]
    [InlineData("[not-a-host]:80\n9.9.9.9:80")]
    public void InvalidBracketedEnvelopeCannotFallBackToEmbeddedAddresses(string body) =>
        Assert.Throws<InvalidDataException>(() => SourceFeedParser.ParseRequired(body, ProxyProtocol.Http));

    [Theory]
    [InlineData("")]
    [InlineData("\uFEFF \t\r\n")]
    public void EmptyFeedCannotReportSuccessOrInvokeAdmission(string body)
    {
        var calls = 0;
        Assert.Throws<InvalidDataException>(() => SourceFeedParser.ParseBoundedToRequired(body, ProxyProtocol.Http, 10, _ => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void NonRecordsAndDiagnosticMetadataDoNotHideHealthyEnvelopeRecords()
    {
        var parsed = SourceFeedParser.ParseRequired("""
            {"results":[null,17,false,{"message":"8.8.8.8:80","info":["8.8.8.8:80"]},
            {"ip":"1.1.1.1","port":443}],"next":"https://8.8.8.8:80/list"}
            """, ProxyProtocol.Https);
        Assert.Equal(("1.1.1.1", 443, ProxyProtocol.Https), Assert.Single(parsed));
    }

    [Fact]
    public void ConsumerFailureStopsJsonStreamWithoutReportingSuccessfulParse()
    {
        const string body = """[{"ip":"8.8.8.8","port":80,"protocols":["http","https"]}]""";
        var calls = 0;
        Assert.Throws<IOException>(() => SourceFeedParser.ParseBoundedToRequired(body, ProxyProtocol.Http, 10, _ =>
        {
            calls++;
            throw new IOException("consumer failed");
        }));
        Assert.Equal(1, calls);
    }
}
