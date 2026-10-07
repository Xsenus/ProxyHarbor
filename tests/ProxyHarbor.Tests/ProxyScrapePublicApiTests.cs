using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ProxyScrapePublicApiTests
{
    private static readonly string[] ExpectedOffsets = ["skip=0&", "skip=2000&", "skip=4000&", "skip=0&"];

    [Fact]
    public void BuiltInMixedFeedUsesTheCanonicalWholeListAdapter() =>
        Assert.Equal(ProxyScrapePublicApi.Url, BuiltInSourceCatalog.Sources.Single(source => source.Name == "ProxyScrape V4 Mixed").Url);
    [Theory]
    [InlineData("skip", "2000")]
    [InlineData("skip", "\"0\"")]
    [InlineData("limit", "2001")]
    [InlineData("limit", "null")]
    [InlineData("shown_records", "1")]
    [InlineData("shown_records", "\"0\"")]
    [InlineData("total_records", "-1")]
    [InlineData("total_records", "20000001")]
    [InlineData("total_records", "\"0\"")]
    [InlineData("nextpage", "true")]
    [InlineData("nextpage", "0")]
    [InlineData("success", "false")]
    [InlineData("status", "0")]
    [InlineData("error", "\"quota\"")]
    public void MalformedMetadataCannotBecomeACheckpoint(string field, string value)
    {
        var fields = new Dictionary<string, JsonElement>();
        using var page = JsonDocument.Parse(Page(0, 0, false));
        foreach (var property in page.RootElement.EnumerateObject()) fields.Add(property.Name, property.Value.Clone());
        using var replacement = JsonDocument.Parse(value);
        fields[field] = replacement.RootElement.Clone();
        Assert.Throws<InvalidDataException>(() => ProxyScrapePublicApi.CompactPage(JsonSerializer.Serialize(fields), 1));
    }

    [Fact]
    public void CompactionPreservesProtocolsAndRejectsPrivateOrCredentialedRows()
    {
        var body = Page(0, 5, false, Row("8.8.8.8", "socks4"), Row("8.8.8.8", "socks5"), Row("127.0.0.1"),
            new { ip = "1.1.1.1", port = 80, protocol = "http", password = "do-not-persist", history = new string('x', 10000) },
            new { ip = "9.9.9.9", port = 0, protocol = "http" });
        var compact = ProxyScrapePublicApi.CompactPage(body, 1);
        Assert.DoesNotContain("do-not-persist", compact);
        Assert.DoesNotContain("history", compact);
        var candidates = SourceFeedParser.ParseRequired(compact, ProxyProtocol.Http);
        Assert.Equal(new[] { ProxyProtocol.Socks4, ProxyProtocol.Socks5 }, candidates.OrderBy(p => p.Protocol).Select(p => p.Protocol));
        Assert.Equal(TimeSpan.FromSeconds(1), new SourceApiOriginGate(null!, ProxyScrapePublicApi.Url).RequestInterval);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(5), SourceFetchSchedule.NextSuccessAttempt(ProxyScrapePublicApi.Url, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task MutableTotalsAndFalseNextPageStillRequireEmptyTailAndHeadReconciliationAcrossRestarts()
    {
        // Reproduce the real provider's nontransactional total/row delta at the tail.
        var responses = new Queue<string>([Page(0, 3, true, Row("8.8.8.8"), Row("1.1.1.1", "socks4")),
            Page(2000, 2000, false, Row("9.9.9.9", "socks5")), Page(4000, 3, false),
            Page(0, 4, true, Row("8.8.8.8"), Row("208.67.222.222"))]);
        const int maximumBytes = 1_000_000;
        var capture = new FreeProxyDbPageCapture();
        var requested = new List<string>();
        var commits = 0;
        string? content = null;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            var advanced = await FreeProxyDbPageCapture.AdvanceAsync(capture, ProxyScrapePublicApi.Url, maximumBytes, 1,
                (url, _) =>
                {
                    Assert.Equal(cycle, commits);
                    requested.Add(url);
                    return Task.FromResult(new SourceFetchResult(responses.Dequeue(), false, null, null));
                }, (_, _) => { commits++; return Task.CompletedTask; }, CancellationToken.None);
            var payload = FreeProxyDbPageCaptureCodec.Encode(advanced.Capture, maximumBytes);
            capture = FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), maximumBytes);
            if (cycle < 3) Assert.Null(advanced.Content);
            content = advanced.Content;
        }
        Assert.Equal(ExpectedOffsets,
            requested.Select(url => url[(url.IndexOf("skip=", StringComparison.Ordinal))..url.IndexOf("limit=", StringComparison.Ordinal)]));
        Assert.Equal(4, SourceFeedParser.ParseRequired(content!, ProxyProtocol.Http).Count);
        Assert.True(capture.Inspect(maximumBytes, ProxyScrapePublicApi.Url).Complete);
        Assert.Throws<InvalidDataException>(() => capture.Inspect(maximumBytes, ProxoraPublicApi.Url));
        Assert.All(capture.Pages, page => Assert.Contains("originalBodySha256", page.Content));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrematureEmptyOrRepeatedPageCannotPublishAPrefix(bool empty)
    {
        var capture = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            ProxyScrapePublicApi.CompactPage(Page(0, 4001, true, Row("8.8.8.8")), 1)), 1_000_000);
        await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbPageCapture.AdvanceAsync(capture, ProxyScrapePublicApi.Url,
            1_000_000, 1, (_, _) => Task.FromResult(new SourceFetchResult(
                empty ? Page(2000, 4001, false) : Page(2000, 4001, true, Row("8.8.8.8")), false, null, null)),
            (_, _) => throw new InvalidOperationException("Invalid page committed"), CancellationToken.None));
    }

    [Fact]
    public async Task FailedCheckpointCannotAdvanceToAnotherRequest()
    {
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => FreeProxyDbPageCapture.AdvanceAsync(new(), ProxyScrapePublicApi.Url,
            1_000_000, 4, (_, _) => { calls++; return Task.FromResult(new SourceFetchResult(Page(0, 1, false, Row("8.8.8.8")), false, null, null)); },
            (_, _) => throw new IOException("Checkpoint write failed"), CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CompletelyReplacedHeadTraversesUntilItsEmptyTail()
    {
        var pages = new Queue<string>([Page(0, 1, false, Row("8.8.8.8")), Page(2000, 1, false),
            Page(0, 1, false, Row("1.1.1.1")), Page(2000, 1, false)]);
        var result = await FreeProxyDbPageCapture.AdvanceAsync(new(), ProxyScrapePublicApi.Url, 1_000_000, 4,
            (_, _) => Task.FromResult(new SourceFetchResult(pages.Dequeue(), false, null, null)),
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.True(result.Capture.Inspect(1_000_000).Complete);
        Assert.Equal(2, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
    }

    private static object Row(string host, string protocol = "http") => new { ip = host, port = 1080, protocol };
    internal static string Page(int skip, int total, bool nextpage, params object[] proxies) =>
        JsonSerializer.Serialize(new { proxies, shown_records = proxies.Length, total_records = total, skip, limit = 2000, nextpage });
}
