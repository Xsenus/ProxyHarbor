using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class RoundProxiesPageCaptureTests
{
    private const int MaximumBytes = 1_000_000;

    [Fact]
    public async Task EarlierHeadOverlapCompletesWhenNewOnlyTailFillsMissingRowsAfterRestart()
    {
        var prefix = FreeProxyDbPageCapture.Restore(RoundReconciliationFixture.Pages()[..3], MaximumBytes);
        Assert.False(prefix.Inspect(MaximumBytes).Complete);
        var payload = FreeProxyDbPageCaptureCodec.Encode(prefix, MaximumBytes);
        prefix = FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), MaximumBytes);
        var requests = 0;
        var result = await FreeProxyDbPageCapture.AdvanceAsync(prefix, RoundProxiesPublicApi.Url, MaximumBytes, 1,
            (url, _) =>
            {
                requests++;
                Assert.Contains("page=2&", url, StringComparison.Ordinal);
                return Task.FromResult(Response(RoundReconciliationFixture.Pages()[3].Content));
            }, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, requests);
        Assert.True(result.Capture.Inspect(MaximumBytes).Complete);
        Assert.Equal(3, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
    }

    [Fact]
    public async Task LegacyEmptyTailCheckpointPreservesAllPagesAndAssemblesWithoutMoreRequests()
    {
        var pages = RoundReconciliationFixture.Pages();
        var capture = FreeProxyDbPageCapture.Restore(pages, MaximumBytes);
        var payload = FreeProxyDbPageCaptureCodec.Encode(capture, MaximumBytes);
        capture = FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), MaximumBytes);
        Assert.Equal(pages, capture.Pages);
        Assert.True(capture.Inspect(MaximumBytes).Complete);
        var result = await FreeProxyDbPageCapture.AdvanceAsync(capture, RoundProxiesPublicApi.Url, MaximumBytes, 1,
            (_, _) => throw new InvalidOperationException("Recovered capture requested another page"),
            (_, _) => throw new InvalidOperationException("Recovered capture rewrote its checkpoint"), CancellationToken.None);
        Assert.Equal(3, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
        Assert.Equal(pages[0].CapturedAt, result.ObservedAt);
        Assert.Throws<InvalidDataException>(() => capture.Append(new(5, true, DateTimeOffset.UtcNow, Page(5, 3)), MaximumBytes));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeadStillNeedsBothOverlapAndTheDeclaredRecordCount(bool hasOverlap)
    {
        var capture = FreeProxyDbPageCapture.Restore(RoundReconciliationFixture.Pages()[..2], MaximumBytes);
        var head = hasOverlap ? Page(1, 4, Record("a", "8.8.8.8")) : Page(1, 3, Record("c", "9.9.9.9"));
        capture = capture.Append(new(1, true, DateTimeOffset.UtcNow, head), MaximumBytes);
        var tail = hasOverlap ? Record("c", "9.9.9.9") : Record("d", "4.2.2.2");
        var result = await FreeProxyDbPageCapture.AdvanceAsync(capture, RoundProxiesPublicApi.Url, MaximumBytes, 1,
            (_, _) => Task.FromResult(Response(Page(2, 4, tail))), (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Null(result.Content);
        Assert.False(result.Capture.Inspect(MaximumBytes).Complete);
        Assert.Equal(hasOverlap ? 3 : 4, result.Capture.Inspect(MaximumBytes).Rows.Count);
    }

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

internal static class RoundReconciliationFixture
{
    internal static FreeProxyDbCapturedPage[] Pages()
    {
        var observed = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        object Record(string id, string ip) => new { _id = id, ip, port = 1080, protocols = new[] { "http" } };
        string Body(int page, int total, params object[] data) => JsonSerializer.Serialize(new { data, total, page, limit = 500 });
        var a = Record("a", "8.8.8.8"); var b = Record("b", "1.1.1.1"); var c = Record("c", "9.9.9.9");
        return [new(1, false, observed, Body(1, 3, a, b)), new(2, false, observed.AddSeconds(1), Body(2, 2)),
            new(1, true, observed.AddSeconds(2), Body(1, 3, a, b)), new(2, true, observed.AddSeconds(3), Body(2, 3, c)),
            new(3, true, observed.AddSeconds(4), Body(3, 3)), new(4, true, observed.AddSeconds(5), Body(4, 3))];
    }
}
