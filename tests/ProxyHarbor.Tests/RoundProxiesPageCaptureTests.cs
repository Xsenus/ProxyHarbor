using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class RoundProxiesPageCaptureTests
{
    private const int MaximumBytes = 1_000_000;

    [Fact]
    public async Task MutableSortOverlapCannotPublishMissingRowsAndRestartReconcilesChangedHead()
    {
        var first = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            Page(1, 3, Record("a", "8.8.8.8"), Record("b", "1.1.1.1"))), MaximumBytes);
        var paused = await FreeProxyDbPageCapture.AdvanceAsync(first, RoundProxiesPublicApi.Url, MaximumBytes, 1,
            (_, _) => Task.FromResult(Response(Page(2, 3, Record("b", "1.1.1.1")))), (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Null(paused.Content);
        Assert.True(paused.Capture.Inspect(MaximumBytes).Reconciliation);
        var payload = FreeProxyDbPageCaptureCodec.Encode(paused.Capture, MaximumBytes);
        var restored = FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), MaximumBytes);
        var committed = 0;
        var completed = await FreeProxyDbPageCapture.AdvanceAsync(restored, RoundProxiesPublicApi.Url, MaximumBytes, 1,
            (url, _) =>
            {
                Assert.Equal(RoundProxiesPublicApi.Url, url);
                return Task.FromResult(Response(Page(1, 3, Record("c", "9.9.9.9"), Record("a", "8.8.8.8", "socks5"))));
            }, (_, _) => { committed++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, committed);
        Assert.Equal(first.Pages[0].CapturedAt, completed.ObservedAt);
        var profiles = SourceFeedParser.ParseRequired(completed.Content!, ProxyProtocol.Http);
        Assert.Equal(3, profiles.Count);
        Assert.Equal(ProxyProtocol.Socks5, profiles.Single(p => p.Host == "8.8.8.8").Protocol);
    }

    [Theory]
    [InlineData("{\"data\":[],\"total\":0}")]
    [InlineData("{\"data\":[],\"total\":0,\"page\":2,\"limit\":500}")]
    [InlineData("{\"data\":[],\"total\":0,\"page\":\"1\",\"limit\":500}")]
    [InlineData("{\"data\":[],\"total\":0,\"page\":1,\"limit\":\"500\"}")]
    [InlineData("{\"data\":[],\"total\":0,\"page\":1,\"limit\":100}")]
    [InlineData("{\"data\":[],\"total\":-1,\"page\":1,\"limit\":500}")]
    [InlineData("{\"data\":[],\"total\":0,\"page\":1,\"limit\":500,\"success\":false}")]
    [InlineData("{\"data\":[],\"total\":0,\"page\":1,\"limit\":500,\"error\":\"quota\"}")]
    [InlineData("{\"proxies\":[],\"total_proxies\":0}")]
    public async Task WrongPageLimitProviderAndErrorEnvelopeNeverReachCheckpoint(string body)
    {
        var committed = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbPageCapture.AdvanceAsync(new(), RoundProxiesPublicApi.Url,
            MaximumBytes, 1, (_, _) => Task.FromResult(Response(body)), (_, _) => { committed++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, committed);
    }

    [Fact]
    public async Task EmptyOrRepeatedTailNeverTurnsAValidPrefixIntoSuccess()
    {
        var first = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            Page(1, 2, Record("a", "8.8.8.8"))), MaximumBytes);
        foreach (var body in new[] { Page(2, 2), Page(2, 2, Record("a", "8.8.8.8")) })
            await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbPageCapture.AdvanceAsync(first, RoundProxiesPublicApi.Url,
                MaximumBytes, 1, (_, _) => Task.FromResult(Response(body)), (_, _) => throw new InvalidOperationException("Invalid page committed"), CancellationToken.None));
    }

    [Fact]
    public async Task MultipleProtocolsAndEndpointSafetySurviveAssembly()
    {
        var rows = new object[] {
            new { _id = "a", ip = "8.8.8.8", port = 1080, protocols = new[] { "socks4", "socks5" } },
            new { _id = "b", ip = "127.0.0.1", port = 80, protocols = new[] { "http" } },
            new { _id = "c", ip = "9.9.9.9", port = 80, protocols = new[] { "http" }, password = "private" },
            new { _id = "d", ip = "1.1.1.1", port = 0, protocols = new[] { "http" } }
        };
        var body = Page(1, rows.Length, rows);
        var completed = await FreeProxyDbPageCapture.AdvanceAsync(new(), RoundProxiesPublicApi.Url, MaximumBytes, 2,
            (_, _) => Task.FromResult(Response(body)), (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal([ProxyProtocol.Socks4, ProxyProtocol.Socks5],
            SourceFeedParser.ParseRequired(completed.Content!, ProxyProtocol.Http).Select(p => p.Protocol).ToArray());
    }

    [Fact]
    public void FreeProxyDbExtraRootMetadataDoesNotChangeItsProviderOrCodec()
    {
        var body = """{"status":1,"total":1,"data":{"total_count":1,"data":[{"id":1}]}}""";
        var capture = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow, body), MaximumBytes);
        Assert.Single(capture.Inspect(MaximumBytes, FreeProxyDbFeedFetcher.Url).Rows);
        Assert.Throws<InvalidDataException>(() => capture.Inspect(MaximumBytes, RoundProxiesPublicApi.Url));
    }

    private static object Record(string id, string host, string protocol = "http") => new { _id = id, ip = host, port = 1080, protocols = new[] { protocol } };
    private static string Page(int page, int total, params object[] data) => JsonSerializer.Serialize(new { data, total, page, limit = 500 });
    private static SourceFetchResult Response(string body) => new(body, false, null, null);
}
