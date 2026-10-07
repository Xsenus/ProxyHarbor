using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ProxiwarePageCaptureTests
{
    private const int MaximumBytes = 100_000;

    [Fact]
    public async Task RestartContinuesTailAndReconcilesNewHeadWithoutNumericPublisherIds()
    {
        var capture = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            Page(2, Record("8.8.8.8"))), MaximumBytes);
        var payload = FreeProxyDbPageCaptureCodec.Encode(capture, MaximumBytes);
        var restored = FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), MaximumBytes);
        var requests = new List<string>();
        var result = await FreeProxyDbPageCapture.AdvanceAsync(restored, ProxiwarePublicApi.Url,
            MaximumBytes, 4, (url, _) =>
            {
                requests.Add(url);
                return Task.FromResult(Response(requests.Count == 1
                    ? Page(2, Record("1.1.1.1"))
                    : Page(3, Record("9.9.9.9"), Record("8.8.8.8", speed: 20))));
            }, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal([ProxiwarePublicApi.Url.Replace("page=1&", "page=2&", StringComparison.Ordinal), ProxiwarePublicApi.Url], requests);
        Assert.Equal(restored.Pages[0].CapturedAt, result.ObservedAt);
        using var document = JsonDocument.Parse(result.Content!);
        var rows = document.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(20, rows.Single(row => row.GetProperty("addr").GetString() == "8.8.8.8").GetProperty("speed_ms").GetInt32());
        Assert.Equal(3, SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Count);
    }

    [Theory]
    [InlineData("socks4socks5", ProxyProtocol.Socks4, ProxyProtocol.Socks5)]
    [InlineData("httpsocks4", ProxyProtocol.Http, ProxyProtocol.Socks4)]
    [InlineData("httpsocks5", ProxyProtocol.Http, ProxyProtocol.Socks5)]
    [InlineData("httphttps", ProxyProtocol.Http, ProxyProtocol.Https)]
    public async Task ExactCombinedLabelsPreserveBothTransports(string label, ProxyProtocol first, ProxyProtocol second)
    {
        var result = await Complete(Page(1, Record("8.8.8.8", label)));
        var parsed = SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Socks5);
        Assert.Equal([first, second], parsed.Select(row => row.Protocol).ToArray());
    }

    [Fact]
    public async Task ThreeTransportLabelAndExplicitProtocolListRemainDistinct()
    {
        var row = Record("8.8.8.8", "httpsocks4socks5");
        var result = await Complete(Page(1, row));
        Assert.Equal([ProxyProtocol.Http, ProxyProtocol.Socks4, ProxyProtocol.Socks5],
            SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http).Select(item => item.Protocol).ToArray());
        var body = """{"total_proxies":1,"proxies":[{"addr":"8.8.8.8","port":1080,"protocol":"socks4socks5","protocols":["socks5"]}]}""";
        result = await Complete(body);
        Assert.Equal(ProxyProtocol.Socks5, Assert.Single(SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http)).Protocol);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"success\":false,\"total_proxies\":0,\"proxies\":[]}")]
    [InlineData("{\"status\":0,\"total_proxies\":0,\"proxies\":[]}")]
    [InlineData("{\"error\":\"quota\",\"total_proxies\":0,\"proxies\":[]}")]
    [InlineData("{\"total_proxies\":\"1\",\"proxies\":[]}")]
    [InlineData("{\"total_proxies\":-1,\"proxies\":[]}")]
    [InlineData("{\"total_proxies\":1000001,\"proxies\":[]}")]
    [InlineData("{\"total_proxies\":1,\"proxies\":[null]}")]
    [InlineData("{\"total_proxies\":1,\"proxies\":[{\"addr\":\"8.8.8.8\",\"port\":80}]}")]
    [InlineData("{\"total_proxies\":1,\"proxies\":[{\"addr\":\"8.8.8.8\",\"port\":null,\"protocol\":\"http\"}]}")]
    [InlineData("{\"status\":1,\"data\":{\"total_count\":0,\"data\":[]}}")]
    public async Task InvalidOrWrongProviderResponseNeverReachesCheckpoint(string body)
    {
        var commits = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbPageCapture.AdvanceAsync(new(),
            ProxiwarePublicApi.Url, MaximumBytes, 2, (_, _) => Task.FromResult(Response(body)),
            (_, _) => { commits++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, commits);
    }

    [Fact]
    public async Task RepeatedPageAndPrematureEmptyTailDoNotPublishPrefix()
    {
        var capture = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow,
            Page(3, Record("8.8.8.8"))), MaximumBytes);
        foreach (var response in new[] { Page(3, Record("8.8.8.8")), Page(3) })
        {
            var commits = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbPageCapture.AdvanceAsync(capture,
                ProxiwarePublicApi.Url, MaximumBytes, 2, (_, _) => Task.FromResult(Response(response)),
                (_, _) => { commits++; return Task.CompletedTask; }, CancellationToken.None));
            Assert.Equal(0, commits);
        }
    }

    [Fact]
    public async Task UnknownPrivateAndCredentialRecordsRemainRejectedAfterNormalization()
    {
        var body = """
            {"total_proxies":5,"proxies":[
            {"addr":"127.0.0.1","port":80,"protocol":"socks4socks5"},
            {"addr":"8.8.8.8","port":80,"protocol":"httphttps","password":"private"},
            {"addr":"9.9.9.9","port":0,"protocol":"httpsocks4"},
            {"addr":"8.8.4.4","port":80,"protocol":"unknown"},
            {"addr":"1.1.1.1","port":443,"protocol":"https"}]}
            """;
        var result = await Complete(body);
        Assert.Equal(("1.1.1.1", 443, ProxyProtocol.Https),
            Assert.Single(SourceFeedParser.ParseRequired(result.Content!, ProxyProtocol.Http)));
    }

    [Fact]
    public async Task PauseAndCommitFailurePreserveBoundedTraversal()
    {
        var response = Page(2, Record("8.8.8.8"));
        var paused = await FreeProxyDbPageCapture.AdvanceAsync(new(), ProxiwarePublicApi.Url,
            MaximumBytes, 1, (_, _) => Task.FromResult(Response(response)), (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Null(paused.Content);
        Assert.False(paused.Capture.Inspect(MaximumBytes, ProxiwarePublicApi.Url).Complete);
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => FreeProxyDbPageCapture.AdvanceAsync(new(), ProxiwarePublicApi.Url,
            MaximumBytes, 4, (_, _) => { calls++; return Task.FromResult(Response(response)); },
            (_, _) => throw new IOException("checkpoint failed"), CancellationToken.None));
        Assert.Equal(1, calls);
    }

    private static object Record(string address, string protocol = "http", int speed = 100) =>
        new { addr = address, port = 1080, protocol, speed_ms = speed };
    private static string Page(int total, params object[] rows) => JsonSerializer.Serialize(new { total_proxies = total, proxies = rows });
    private static SourceFetchResult Response(string body) => new(body, false, null, null);
    private static Task<FreeProxyDbCaptureAdvance> Complete(string body) =>
        FreeProxyDbPageCapture.AdvanceAsync(new(), ProxiwarePublicApi.Url, MaximumBytes, 2,
            (_, _) => Task.FromResult(Response(body)), (_, _) => Task.CompletedTask, CancellationToken.None);
}
