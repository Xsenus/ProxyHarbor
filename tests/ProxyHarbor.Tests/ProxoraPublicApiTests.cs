using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ProxoraPublicApiTests
{
    [Theory]
    [InlineData("{\"items\":[],\"total\":0,\"page\":2,\"per_page\":500}")]
    [InlineData("{\"items\":[],\"total\":0,\"page\":1,\"per_page\":25}")]
    [InlineData("{\"items\":[],\"total\":-1,\"page\":1,\"per_page\":500}")]
    [InlineData("{\"items\":[],\"total\":\"0\",\"page\":1,\"per_page\":500}")]
    [InlineData("{\"items\":[],\"total\":0,\"page\":1,\"per_page\":500,\"success\":false}")]
    [InlineData("{\"items\":[],\"total\":0,\"page\":1,\"per_page\":500,\"error\":\"quota\"}")]
    [InlineData("{\"items\":[],\"total\":0,\"page\":1,\"per_page\":500,\"status\":0}")]
    public void InvalidPagesCannotBecomeDurableCheckpoints(string body) =>
        Assert.Throws<InvalidDataException>(() => ProxoraPublicApi.CompactPage(body, 1));

    [Fact]
    public void CompactionRetainsTypedRowsAndRejectsUnsafeCandidatesWithoutPersistingCredentials()
    {
        var body = Page(1, 5, new { host = "8.8.8.8", port = 1080, protocol = "socks4", history = new string('x', 10_000) },
            Row("8.8.8.8", "socks5"), Row("127.0.0.1"),
            new { host = "1.1.1.1", port = 80, protocol = "http", password = "secret-not-persisted" },
            new { host = "9.9.9.9", port = 0, protocol = "http" });
        var compact = ProxoraPublicApi.CompactPage(body, 1);
        Assert.DoesNotContain("secret-not-persisted", compact);
        Assert.DoesNotContain("history", compact);
        using var doc = JsonDocument.Parse(compact);
        var rows = ProxoraPublicApi.InspectPage(doc.RootElement, 1).Rows;
        Assert.Equal(5, rows.GetArrayLength());
        Assert.NotEqual(ProxoraPublicApi.RecordKey(rows[0]), ProxoraPublicApi.RecordKey(rows[1]));
        var candidates = SourceFeedParser.ParseRequired(JsonSerializer.Serialize(new { data = rows }), ProxyProtocol.Http);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(new[] { ProxyProtocol.Socks4, ProxyProtocol.Socks5 }, candidates.OrderBy(p => p.Protocol).Select(p => p.Protocol));
        Assert.Equal(TimeSpan.FromMinutes(1), new SourceApiOriginGate(null!, ProxoraPublicApi.Url).RequestInterval);
    }

    [Fact]
    public async Task TypedPagesSurviveRestartAndHeadReconciliationBeforePublication()
    {
        const int maximumBytes = 1_000_000;
        var capture = new FreeProxyDbPageCapture();
        var commits = 0;
        var responses = new Queue<string>([Page(1, 3, Row("8.8.8.8"), Row("1.1.1.1", "socks4")),
            Page(2, 3, Row("1.1.1.1", "socks4")), Page(1, 3, Row("8.8.8.8"), Row("9.9.9.9", "socks5"))]);
        string? content = null;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var result = await FreeProxyDbPageCapture.AdvanceAsync(capture, ProxoraPublicApi.Url, maximumBytes, 1,
                (url, _) =>
                {
                    Assert.Contains($"page={(cycle == 1 ? 2 : 1)}&", url);
                    return Task.FromResult(new SourceFetchResult(responses.Dequeue(), false, null, null));
                }, (_, _) => { commits++; return Task.CompletedTask; }, CancellationToken.None);
            var payload = FreeProxyDbPageCaptureCodec.Encode(result.Capture, maximumBytes);
            capture = FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), maximumBytes);
            if (cycle < 2) Assert.Null(result.Content);
            content = result.Content;
        }
        Assert.Equal(3, commits);
        Assert.Equal(3, SourceFeedParser.ParseRequired(content!, ProxyProtocol.Http).Count);
        Assert.All(capture.Pages, page => Assert.Contains("originalBodySha256", page.Content));
        Assert.Throws<InvalidDataException>(() => capture.Inspect(maximumBytes, Socks5ProxiesPublicApi.Url));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrRepeatedTailNeverPublishesAPrefix(bool empty)
    {
        var capture = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            ProxoraPublicApi.CompactPage(Page(1, 2, Row("8.8.8.8")), 1)), 1_000_000);
        await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbPageCapture.AdvanceAsync(capture, ProxoraPublicApi.Url,
            1_000_000, 1, (_, _) => Task.FromResult(new SourceFetchResult(empty ? Page(2, 2) : Page(2, 2, Row("8.8.8.8")), false, null, null)),
            (_, _) => throw new InvalidOperationException("Invalid page committed"), CancellationToken.None));
    }

    private static object Row(string host, string protocol = "http") => new { host, port = 1080, protocol };
    private static string Page(int page, int total, params object[] items) => JsonSerializer.Serialize(new { items, total, page, per_page = 500 });
}
