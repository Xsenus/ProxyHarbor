using System.Runtime.CompilerServices;
using System.Text;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует правила нормализации данных из недоверенных источников.</summary>
public sealed class ProxyParserTests
{
    [Fact]
    public void InternalCandidateKeyContainsNoManagedReferences()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ProxyCandidateKey>());
        Assert.InRange(Unsafe.SizeOf<ProxyCandidateKey>(), 1, 32);
    }

    [Fact]
    public void CompactKeyCanonicalizesIpv6AndRoundTripsEndpoint()
    {
        var expanded = ProxyCandidateKey.Parse("2606:4700:4700:0:0:0:0:1111", 443, ProxyProtocol.Https);
        var compressed = ProxyCandidateKey.Parse("2606:4700:4700::1111", 443, ProxyProtocol.Https);

        Assert.Equal(expanded, compressed);
        Assert.Equal(("2606:4700:4700::1111", 443, ProxyProtocol.Https), expanded.ToEndpoint());
    }

    [Fact]
    public void CompactKeyCanonicalizesMappedIpv4AndRejectsUnknownProtocol()
    {
        var ipv4 = ProxyCandidateKey.Parse("8.8.8.8", 80, ProxyProtocol.Http);
        var mapped = ProxyCandidateKey.Parse("::ffff:8.8.8.8", 80, ProxyProtocol.Http);

        Assert.Equal(ipv4, mapped);
        Assert.Equal(("8.8.8.8", 80, ProxyProtocol.Http), mapped.ToEndpoint());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProxyCandidateKey.Parse("8.8.8.8", 80, (ProxyProtocol)999));
    }

    [Fact]
    public void ParseToStreamsUniqueCandidatesAndPreservesTruncationSemantics()
    {
        var accepted = new List<ProxyCandidateKey>();

        var summary = ProxyParser.ParseTo(
            "8.8.8.8:80\n8.8.8.8:80\n1.1.1.1:81\n9.9.9.9:82",
            ProxyProtocol.Http,
            maxResults: 2,
            accepted.Add);

        Assert.Equal(new ProxyParseSummary(2, Truncated: true), summary);
        Assert.Equal(
            [("8.8.8.8", 80, ProxyProtocol.Http), ("1.1.1.1", 81, ProxyProtocol.Http)],
            accepted.Select(candidate => candidate.ToEndpoint()));
    }

    [Fact]
    public void StreamingPathAvoidsPerEndpointMaterializationAllocations()
    {
        const int endpointCount = 20_000;
        var content = new StringBuilder(endpointCount * 20);
        for (var index = 0; index < endpointCount; index++)
            content.Append("11.").Append(index >> 16).Append('.').Append(index >> 8 & 255).Append('.')
                .Append(index & 255).Append(':').Append(1_000 + index % 60_000).Append('\n');
        var feed = content.ToString();

        // Прогрев отделяет JIT/regex initialization от сравниваемых allocations.
        _ = ProxyParser.ParseTo("8.8.8.8:80", ProxyProtocol.Http, 1, static _ => { });
        _ = ProxyParser.ParseWithLimitStatus("8.8.8.8:80", ProxyProtocol.Http, 1);

        var beforeStreaming = GC.GetAllocatedBytesForCurrentThread();
        var streamed = ProxyParser.ParseTo(feed, ProxyProtocol.Http, endpointCount, static _ => { });
        var streamingBytes = GC.GetAllocatedBytesForCurrentThread() - beforeStreaming;

        var beforeMaterialized = GC.GetAllocatedBytesForCurrentThread();
        var materialized = ProxyParser.ParseWithLimitStatus(feed, ProxyProtocol.Http, endpointCount);
        var materializedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeMaterialized;

        Assert.Equal(endpointCount, streamed.Count);
        Assert.Equal(endpointCount, materialized.Items.Count);
        Assert.True(
            materializedBytes - streamingBytes >= endpointCount * 32L,
            $"Ожидалась экономия минимум 32 bytes/endpoint, получено {materializedBytes - streamingBytes:N0} bytes.");
        GC.KeepAlive(materialized);
    }

    [Fact]
    public void ParseRecognizesSchemesAndFallbackProtocol()
    {
        var result = ProxyParser.Parse("http://8.8.8.8:8080\n1.1.1.1:1080\nsocks5://example.com:9000", ProxyProtocol.Socks4);

        Assert.Contains(result, x => x == ("8.8.8.8", 8080, ProxyProtocol.Http));
        Assert.Contains(result, x => x == ("1.1.1.1", 1080, ProxyProtocol.Socks4));
        Assert.DoesNotContain(result, x => x.Host == "example.com");
    }

    [Fact]
    public void ParseMixedFeedPreservesEveryPerRecordProtocolAndFallback()
    {
        const string content = """
            http://8.8.8.8:8080
            https://8.8.8.8:8080
            socks4://8.8.8.8:8080
            socks5://8.8.8.8:8080
            1.1.1.1:1080
            """;

        var result = ProxyParser.Parse(content, ProxyProtocol.Socks5);

        Assert.Equal(5, result.Count);
        Assert.All(Enum.GetValues<ProxyProtocol>(), protocol =>
            Assert.Contains(result, proxy => proxy.Host == "8.8.8.8" && proxy.Protocol == protocol));
        Assert.Contains(result, proxy =>
            proxy == ("1.1.1.1", 1080, ProxyProtocol.Socks5));
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
    public async Task SourceUrlRejectsFragmentEvenForPublicHttpsAddress() =>
        Assert.False(await NetworkSafety.IsSafePublicHttpsUrlAsync(
            "https://8.8.8.8/feed.txt#ignored-fragment",
            CancellationToken.None));

    [Fact]
    public void ParseRemovesDuplicatesAndUnsafeAddresses()
    {
        var result = ProxyParser.Parse("8.8.8.8:80\n8.8.8.8:80\n127.0.0.1:90\n192.168.1.2:80\n999.1.1.1:80\n8.8.8.8:99999", ProxyProtocol.Http);

        Assert.Single(result);
        Assert.Equal(("8.8.8.8", 80, ProxyProtocol.Http), result.Single());
    }

    [Theory]
    [InlineData("010.0.0.1:80")]
    [InlineData("001.1.1.1:80")]
    [InlineData("x8.8.8.8:80")]
    [InlineData("edge-8.8.8.8:80")]
    [InlineData("9.8.8.8.8:80")]
    [InlineData("::ffff:8.8.8.8:80")]
    [InlineData("8.8.8.8:123456")]
    [InlineData("8.8.8.8:80ms")]
    [InlineData("8.8.8.8:80.5")]
    public void ParseRejectsAmbiguousOrEmbeddedEndpointTokens(string content)
    {
        var result = ProxyParser.Parse(content, ProxyProtocol.Http);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseAcceptsCanonicalEndpointsWithCommonFeedDelimiters()
    {
        const string content = "http://8.8.8.8:80/path\n\"1.1.1.1:81\",country\nproxy=9.9.9.9:82|US";

        var result = ProxyParser.Parse(content, ProxyProtocol.Socks5);

        Assert.Equal(
            [
                ("8.8.8.8", 80, ProxyProtocol.Http),
                ("1.1.1.1", 81, ProxyProtocol.Socks5),
                ("9.9.9.9", 82, ProxyProtocol.Socks5)
            ],
            result);
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
