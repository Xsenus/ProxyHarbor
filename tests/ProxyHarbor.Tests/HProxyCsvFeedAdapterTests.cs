using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class HProxyCsvFeedAdapterTests
{
    private const string Header = "ip,port,protocols,anonymity,country,city,latency_ms,uptime_pct,alive,uptime_24h,uptime_7d,reliability\r\n";
    private const string Row = "8.8.8.8,8080,http|https|socks4|socks5,elite,US,Mountain View,1,99,true,99,99,99\r\n";

    [Fact]
    public void PreservesAllProtocolsQuotedMetadataIpv6AndEverySnapshotWindow()
    {
        var csv = "\uFEFF" + Header + Row + Row +
            "2606:4700:4700::1111,1080,socks5,anonymous,DE,\"A, B\r\n\"\"City\"\"\",1,99,false,99,99,99\r\n";
        var body = HProxyCsvFeedAdapter.Extract(HProxyCsvFeedAdapter.Url, csv);
        var parsed = SourceFeedParser.ParseRequired(body, ProxyProtocol.Http);
        Assert.Equal(5, parsed.Count);
        Assert.Contains(("8.8.8.8", 8080, ProxyProtocol.Https), parsed);
        Assert.Contains(("2606:4700:4700::1111", 1080, ProxyProtocol.Socks5), parsed);
        var snapshot = ProxyCandidateSnapshotCodec.Encode(body, ProxyProtocol.Http);
        var decoded = new List<ProxyCandidateKey>();
        var cursor = 0;
        while (true)
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 1, key => { decoded.Add(key); return true; });
            Assert.True(window.NextIndex > cursor);
            cursor = window.NextIndex;
            if (window.Completed) break;
        }
        Assert.Equal(parsed, decoded.Select(key => key.ToEndpoint()).ToArray());
    }

    [Fact]
    public void CountrySelectorIsAppliedLocallyAfterValidatingTheCompleteCsv()
    {
        var url = HProxyCsvFeedAdapter.Url + "?country=US";
        Assert.True(HProxyCsvFeedAdapter.Supports(url));
        Assert.Equal(HProxyCsvFeedAdapter.Url, HProxyCsvFeedAdapter.FetchUrl(url));
        var body = HProxyCsvFeedAdapter.Extract(url, Header + Row + Row.Replace("8.8.8.8", "1.1.1.1").Replace(",US,", ",DE,"));
        Assert.Equal(4, SourceFeedParser.ParseRequired(body, ProxyProtocol.Http).Count);
        Assert.Throws<InvalidDataException>(() => HProxyCsvFeedAdapter.Extract(url, Header + Row + Row.Replace(",US,", ",DE,").Replace("http|https|socks4|socks5", "unknown")));
        Assert.Throws<InvalidDataException>(() => HProxyCsvFeedAdapter.Extract(HProxyCsvFeedAdapter.Url + "?country=FR", Header + Row));
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/all.csv")]
    [InlineData("https://raw.githubusercontent.com.evil.test/hproxy-com/free-proxy-list/main/all.csv")]
    [InlineData("https://user@raw.githubusercontent.com/hproxy-com/free-proxy-list/main/all.csv")]
    [InlineData("https://raw.githubusercontent.com/hproxy-com/free-proxy-list/dev/all.csv")]
    [InlineData("https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/all.csv?country=us")]
    [InlineData("https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/all.csv?country=US&extra=1")]
    [InlineData("https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/all.csv#fragment")]
    public void DoesNotClaimUnsupportedUrls(string url) => Assert.False(HProxyCsvFeedAdapter.Supports(url));

    [Fact]
    public void RejectsExcessiveRowsEvenWhenAllEndpointsAreDuplicates()
    {
        Assert.Throws<InvalidDataException>(() => HProxyCsvFeedAdapter.Extract(HProxyCsvFeedAdapter.Url,
            Header + string.Concat(Enumerable.Repeat(Row, 100_001))));
    }

    [Fact]
    public void RejectsMalformedUnsupportedAndPrivateRowsWithoutPartialResults()
    {
        foreach (var invalid in new[]
        {
            Header, Header + Row.Replace("8.8.8.8", "127.0.0.1"), Header + Row.Replace("8080", "65536"),
            Header + Row.Replace("http|https|socks4|socks5", "http|http"), Header + Row.Replace("http|https|socks4|socks5", "CONNECT80"),
            Header.Replace("protocols", "password") + Row, Header + Row.Replace("Mountain View", "\"unclosed"),
            Header + Row.Replace("Mountain View", "un\"quoted"), Header + Row.Replace("Mountain View", "\"closed\"extra"),
            Header + Row + "8.8.4.4,80,http\r\n", Header + Row.Replace("Mountain View", new string('x', 16_385))
        }) Assert.Throws<InvalidDataException>(() => HProxyCsvFeedAdapter.Extract(HProxyCsvFeedAdapter.Url, invalid));
        Assert.Throws<InvalidDataException>(() => HProxyCsvFeedAdapter.Extract(HProxyCsvFeedAdapter.Url, new string(' ', 10_000_001)));
    }
}
