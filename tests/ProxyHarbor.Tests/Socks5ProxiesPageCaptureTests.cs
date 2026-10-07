using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class Socks5ProxiesPageCaptureTests
{
    [Fact]
    public async Task OverlappingOffsetPagesSurviveRestartAndReconcileProtocolsBeforePublication()
    {
        const int maximumBytes = 1_000_000;
        var first = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            Socks5ProxiesPublicApi.CompactPage(Page(0, 3, Row("8.8.8.8"), Row("1.1.1.1")), 1)), maximumBytes);
        var paused = await FreeProxyDbPageCapture.AdvanceAsync(first, Socks5ProxiesPublicApi.Url, maximumBytes, 1,
            (url, _) =>
            {
                Assert.Contains("offset=100&", url);
                return Task.FromResult(Response(Page(100, 3, Row("1.1.1.1"))));
            }, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Null(paused.Content);
        Assert.True(paused.Capture.Inspect(maximumBytes).Reconciliation);
        var payload = FreeProxyDbPageCaptureCodec.Encode(paused.Capture, maximumBytes);
        var restored = FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), maximumBytes);
        var completed = await FreeProxyDbPageCapture.AdvanceAsync(restored, Socks5ProxiesPublicApi.Url, maximumBytes, 1,
            (url, _) =>
            {
                Assert.Equal(Socks5ProxiesPublicApi.Url, url);
                return Task.FromResult(Response(Page(0, 3, Row("9.9.9.9"), Row("8.8.8.8", "socks5"))));
            }, (_, _) => Task.CompletedTask, CancellationToken.None);
        var candidates = SourceFeedParser.ParseRequired(completed.Content!, ProxyProtocol.Http);
        Assert.Equal(3, candidates.Count);
        Assert.Equal(ProxyProtocol.Socks5, candidates.Single(p => p.Host == "8.8.8.8").Protocol);
        Assert.Equal(first.Pages[0].CapturedAt, completed.ObservedAt);
        Assert.All(completed.Capture.Pages, page => Assert.Contains("originalBodySha256", page.Content));
        Assert.Throws<InvalidDataException>(() => completed.Capture.Inspect(maximumBytes, RoundProxiesPublicApi.Url));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyOrRepeatedTailCannotPublishACompleteLookingPrefix(bool empty)
    {
        var first = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            Socks5ProxiesPublicApi.CompactPage(Page(0, 2, Row("8.8.8.8")), 1)), 1_000_000);
        await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbPageCapture.AdvanceAsync(first, Socks5ProxiesPublicApi.Url,
            1_000_000, 1, (_, _) => Task.FromResult(Response(empty ? Page(100, 2) : Page(100, 2, Row("8.8.8.8")))),
            (_, _) => throw new InvalidOperationException("Invalid page committed"), CancellationToken.None));
    }

    private static object Row(string ip, string protocol = "http") => new { ip, port = 1080, protocols = new[] { protocol } };
    private static string Page(int offset, int total, params object[] data) => JsonSerializer.Serialize(new { data, meta = new { total, limit = 100, offset } });
    private static SourceFetchResult Response(string content) => new(content, false, null, null);
}
