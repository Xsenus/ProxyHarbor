using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class FreeProxyDbPageCaptureTests
{
    private const int MaximumBytes = 100_000;

    [Fact]
    public async Task RestartAfter429ContinuesNextPageAndReconcilesNewHeadIds()
    {
        byte[]? persisted = null;
        var firstRequests = new List<int>();
        await Assert.ThrowsAsync<SourceRateLimitException>(() => Advance(new(), 4, (page, _) =>
        {
            firstRequests.Add(page);
            if (page == 2) throw new SourceRateLimitException(DateTimeOffset.UtcNow.AddHours(1), HttpStatusCode.TooManyRequests);
            return Task.FromResult(Response(Page(3, Row(3), Row(2))));
        }, (capture, _) => { persisted = FreeProxyDbPageCaptureCodec.Encode(capture, MaximumBytes); return Task.CompletedTask; }));
        Assert.Equal([1, 2], firstRequests);
        Assert.NotNull(persisted);
        var restored = FreeProxyDbPageCaptureCodec.Decode(persisted, SHA256.HashData(persisted), MaximumBytes);
        Assert.Equal(2, restored.Inspect(MaximumBytes).NextPage);
        var requests = new List<int>();
        var result = await Advance(restored, 4, (page, _) =>
        {
            requests.Add(page);
            return Task.FromResult(Response(requests.Count == 1
                ? Page(4, Row(2), Row(1)) : Page(4, Row(4), Row(3, "fresh"))));
        });
        Assert.Equal([2, 1], requests);
        using var body = JsonDocument.Parse(result.Content!);
        var rows = body.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.Equal("fresh", rows.Single(row => row.GetProperty("id").GetInt32() == 3).GetProperty("connect_string").GetString());
        Assert.Equal(restored.Pages[0].CapturedAt, result.ObservedAt);
    }

    [Fact]
    public async Task BoundedPauseDoesNotPublishBodyOrPretendSuccessfulFetch()
    {
        var result = await Advance(new(), 1, (_, _) => Task.FromResult(Response(Page(2, Row(2)))));
        Assert.Null(result.Content);
        Assert.Null(result.ObservedAt);
        Assert.False(result.Capture.Inspect(MaximumBytes).Complete);
        Assert.Single(result.Capture.Pages);
    }

    [Fact]
    public async Task FailedCheckpointCommitStopsBeforeAnotherRequest()
    {
        var requests = 0;
        await Assert.ThrowsAsync<IOException>(() => Advance(new(), 4, (_, _) =>
        {
            requests++;
            return Task.FromResult(Response(Page(2, Row(1))));
        }, (_, _) => throw new IOException("commit failed")));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"status\":\"1\"}")]
    [InlineData("{\"status\":1,\"data\":{\"total_count\":\"1\",\"data\":[]}}")]
    [InlineData("{\"status\":1,\"data\":{\"total_count\":1000001,\"data\":[]}}")]
    [InlineData("{\"status\":1,\"data\":{\"total_count\":1,\"data\":[null]}}")]
    public async Task InvalidPageIsNeverCheckpointed(string body)
    {
        var commits = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Advance(new(), 4, (_, _) => Task.FromResult(Response(body)),
            (_, _) => { commits++; return Task.CompletedTask; }));
        Assert.Equal(0, commits);
    }

    [Fact]
    public async Task RepeatedPageAndPrematureEmptyTailAreRejectedBeforeCommit()
    {
        foreach (var next in new[] { Page(3, Row(1)), Page(3) })
        {
            var capture = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow, Page(3, Row(1))), MaximumBytes);
            var commits = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => Advance(capture, 4, (_, _) => Task.FromResult(Response(next)),
                (_, _) => { commits++; return Task.CompletedTask; }));
            Assert.Equal(0, commits);
        }
    }

    [Fact]
    public async Task HeadReconciliationCanContinueAcrossSeveralPagesAndRestart()
    {
        var capture = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow.AddHours(-1), Page(2, Row(2), Row(1))), MaximumBytes);
        var paused = await Advance(capture, 1, (_, _) => Task.FromResult(Response(Page(4, Row(4), Row(3)))));
        Assert.Null(paused.Content);
        var payload = FreeProxyDbPageCaptureCodec.Encode(paused.Capture, MaximumBytes);
        var restored = FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), MaximumBytes);
        var complete = await Advance(restored, 1, (page, _) =>
        {
            Assert.Equal(2, page);
            return Task.FromResult(Response(Page(4, Row(2), Row(1))));
        });
        Assert.NotNull(complete.Content);
        Assert.Equal(capture.Pages[0].CapturedAt, complete.ObservedAt);
        var requests = 0;
        var replay = await Advance(complete.Capture, 1, (_, _) => { requests++; throw new InvalidOperationException(); });
        Assert.Equal(0, requests);
        Assert.Equal(complete.Content, replay.Content);
        Assert.Equal(complete.ObservedAt, replay.ObservedAt);
    }

    [Fact]
    public void CorruptionBoundsAndTrailingDataAreRejected()
    {
        var capture = new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow, Page(2, Row(1))), MaximumBytes);
        var payload = FreeProxyDbPageCaptureCodec.Encode(capture, MaximumBytes);
        Assert.Throws<InvalidDataException>(() => FreeProxyDbPageCaptureCodec.Decode(payload, new byte[32], MaximumBytes));
        Assert.Throws<InvalidDataException>(() => FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), 1));
        byte[] trailing = [.. payload, 0];
        Assert.Throws<InvalidDataException>(() => FreeProxyDbPageCaptureCodec.Decode(trailing, SHA256.HashData(trailing), MaximumBytes));
        var truncated = payload[..^1];
        Assert.Throws<InvalidDataException>(() => FreeProxyDbPageCaptureCodec.Decode(truncated, SHA256.HashData(truncated), MaximumBytes));
    }

    [Fact]
    public void ConfigurationAndPageSequenceCannotBeConfused()
    {
        Assert.True(FreeProxyDbPageCapture.Supports(FreeProxyDbPageCapture.VpnUrl));
        Assert.False(FreeProxyDbPageCapture.Supports(FreeProxyDbPageCapture.VpnUrl + "&key=private"));
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<InvalidDataException>(() => new FreeProxyDbPageCapture().Append(new(2, false, now, Page(1, Row(1))), MaximumBytes));
        Assert.Throws<InvalidDataException>(() => new FreeProxyDbPageCapture().Append(new(1, true, now, Page(1, Row(1))), MaximumBytes));
        Assert.Throws<InvalidDataException>(() => new FreeProxyDbPageCapture().Append(new(1, false, now, Page(1, Row(1), Row(1))), MaximumBytes));
    }

    private static Task<FreeProxyDbCaptureAdvance> Advance(FreeProxyDbPageCapture capture, int budget,
        Func<int, CancellationToken, Task<SourceFetchResult>> fetch,
        Func<FreeProxyDbPageCapture, CancellationToken, Task>? commit = null) =>
        FreeProxyDbPageCapture.AdvanceAsync(capture, FreeProxyDbFeedFetcher.Url, MaximumBytes, budget,
            (url, token) => fetch(int.Parse(new Uri(url).Query.Split('&').Single(part => part.StartsWith("page_index=", StringComparison.Ordinal))[11..], System.Globalization.CultureInfo.InvariantCulture), token),
            commit ?? ((_, _) => Task.CompletedTask), CancellationToken.None);

    private static SourceFetchResult Response(string content) => new(content, false, null, null);
    private static string Page(int total, params object[] rows) => JsonSerializer.Serialize(new { status = 1, data = new { total_count = total, data = rows } });
    private static object Row(int id, string link = "original") => new { id, protocol = "vless", connect_string = link };
}
