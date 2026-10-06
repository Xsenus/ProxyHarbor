using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class SourceApiCaptureSafetyTests
{
    private const int MaximumBytes = 100_000;
    private static readonly SourceApiCaptureOwner Owner = new(Guid.NewGuid(), false, FreeProxyDbFeedFetcher.Url, 0);

    [Fact]
    public void VpnMappingKeepsConnectionStringsAndRejectsDiagnosticAndControlData()
    {
        const string valid = "vless://id@8.8.8.8:443#public";
        var input = JsonSerializer.Serialize(new
        {
            data = new object[]
        {
            new { connect_string = valid }, new { message = valid }, new { connect_string = 42 },
            new { connect_string = (string?)null }, new { connect_string = valid + "\n" + valid },
            new { connect_string = new string('x', 16_385) }
        }
        });
        var text = FreeProxyDbSourceApiFetcher.VpnText(input);
        Assert.Equal(valid + Environment.NewLine, text);
        Assert.Single(VpnFeedParser.Parse(text, VpnProtocol.Vless, 100));
        Assert.Empty(FreeProxyDbSourceApiFetcher.VpnText("{\"data\":[]}"));
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 4)]
    [InlineData(true, -1)]
    [InlineData(true, 9)]
    public void ProtocolOutsideOwnerContractCannotReachStorage(bool vpn, int protocol)
    {
        var owner = Owner with { Vpn = vpn, Protocol = protocol, Url = vpn ? FreeProxyDbPageCapture.VpnUrl : Owner.Url };
        Assert.Throws<InvalidDataException>(owner.EnsureSupported);
    }

    [Fact]
    public void SourceIdentityUrlAndOwnerTypeCannotBeConfused()
    {
        Owner.EnsureSupported();
        var vpn = Owner with { Vpn = true, Url = FreeProxyDbPageCapture.VpnUrl, Protocol = 7 };
        vpn.EnsureSupported();
        Assert.Equal(10_000_000, Owner.MaximumBytes);
        Assert.Equal(FreeProxyDbPageCapture.MaximumCaptureBytes, vpn.MaximumBytes);
        Assert.Throws<InvalidDataException>((Owner with { SourceId = Guid.Empty }).EnsureSupported);
        Assert.Throws<InvalidDataException>((Owner with { Url = Owner.Url + "&key=private" }).EnsureSupported);
        Assert.Throws<InvalidDataException>((vpn with { Url = Owner.Url }).EnsureSupported);
        Assert.Equal(Owner, SourceApiCaptureOwner.From(new ProxySource { Id = Owner.SourceId, Name = "fixture", Url = Owner.Url }));
        Assert.Equal(vpn, SourceApiCaptureOwner.From(new VpnSource { Id = vpn.SourceId, Name = "fixture", Provider = "fixture", License = "fixture", Url = vpn.Url, DefaultProtocol = (VpnProtocol)7 }));
    }

    [Fact]
    public async Task InvalidCheckpointCannotOverwriteCommittedPrefixOrDifferentOwner()
    {
        var factory = new NoDatabaseFactory();
        var store = new SourceApiCaptureStore(factory);
        var first = new FreeProxyDbPageCapture().Append(Page(1, false, 2, 1), MaximumBytes);
        var checkpoint = new SourceApiCaptureCheckpoint(Guid.NewGuid(), Guid.NewGuid(), Owner, first);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Owner,
            checkpoint with { Owner = Owner with { SourceId = Guid.NewGuid() } }, first, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Owner, checkpoint, first, CancellationToken.None));
        var forged = new FreeProxyDbPageCapture().Append(Page(1, false, 2, 2), MaximumBytes)
            .Append(Page(2, false, 2, 3), MaximumBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Owner, checkpoint, forged, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Owner, null, new(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SourceApiCaptureStore(factory, 0)
            .SaveAsync(Owner, null, first, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SourceApiCaptureStore(factory, SourceApiCaptureStore.MaximumStoredBytes + 1)
            .SaveAsync(Owner, null, first, CancellationToken.None));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task InvalidPersistentDeadlinesAreRejectedBeforeOpeningDatabase()
    {
        var factory = new NoDatabaseFactory();
        var gate = new SourceApiOriginGate(factory);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gate.ExtendDeadlineAsync(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gate.ExtendDeadlineAsync(DateTimeOffset.UnixEpoch.AddSeconds(-1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gate.ExtendDeadlineAsync(DateTimeOffset.MaxValue, CancellationToken.None));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void PageDatesCountsAndDuplicateRowsCannotPoisonPersistedCapture()
    {
        foreach (var at in new[] { DateTimeOffset.UnixEpoch.AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1)) })
            Assert.Throws<InvalidDataException>(() => new FreeProxyDbPageCapture().Append(Page(1, false, 2, 1) with { CapturedAt = at }, MaximumBytes));
        foreach (var invalid in new[]
        {
            "{\"status\":1,\"data\":{\"total_count\":1,\"data\":[{\"id\":0}]}}",
            "{\"status\":1,\"data\":{\"total_count\":1,\"data\":[{\"id\":\"1\"}]}}",
            "{\"status\":1,\"data\":{\"total_count\":1,\"data\":[{}]}}",
            "{\"status\":1,\"data\":{\"total_count\":1,\"data\":null}}",
            JsonSerializer.Serialize(new { status = 1, data = new { total_count = 101, data = Enumerable.Range(1, 101).Select(id => new { id }) } })
        }) Assert.Throws<InvalidDataException>(() => new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow, invalid), MaximumBytes));
        var complete = new FreeProxyDbPageCapture().Append(Page(1, false, 1, 1), MaximumBytes).Append(Page(1, true, 1, 1), MaximumBytes);
        Assert.Throws<InvalidDataException>(() => complete.Append(Page(2, true, 1, 2), MaximumBytes));
        var huge = JsonSerializer.Serialize(new { status = 1, data = new { total_count = 1, data = new[] { new { id = 1, metadata = new string('x', FreeProxyDbPageCapture.MaximumPageBytes) } } } });
        Assert.Throws<InvalidDataException>(() => new FreeProxyDbPageCapture().Append(new(1, false, DateTimeOffset.UtcNow, huge), FreeProxyDbPageCapture.MaximumCaptureBytes));
    }

    [Fact]
    public async Task EmptyFullListCompletesOnlyAfterReconciliationAndUnsolicited304IsRejected()
    {
        var requests = 0;
        var empty = await FreeProxyDbPageCapture.AdvanceAsync(new(), Owner.Url, MaximumBytes, 2,
            (_, _) => { requests++; return Task.FromResult(new SourceFetchResult(Page(1, false, 0).Content, false, null, null)); },
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.True(empty.Capture.Inspect(MaximumBytes).Complete);
        await Assert.ThrowsAsync<InvalidDataException>(() => FreeProxyDbPageCapture.AdvanceAsync(new(), Owner.Url, MaximumBytes, 1,
            (_, _) => Task.FromResult(new SourceFetchResult(null, true, null, null)), (_, _) => Task.CompletedTask, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => FreeProxyDbPageCapture.AdvanceAsync(new(), "https://example.com/api", MaximumBytes, 1,
            (_, _) => throw new InvalidOperationException(), (_, _) => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public void RecomputedHashCannotAuthorizeCorruptHeadersDecompressionLengthsOrUtf8()
    {
        var payload = FreeProxyDbPageCaptureCodec.Encode(new FreeProxyDbPageCapture().Append(Page(1, false, 2, 1), MaximumBytes), MaximumBytes);
        foreach (var offset in new[] { 0, 4, 21, 25 })
        {
            var corrupted = (byte[])payload.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(corrupted.AsSpan(offset), int.MaxValue);
            Reject(corrupted);
        }
        var phase = (byte[])payload.Clone(); phase[12] = 2; Reject(phase);
        var date = (byte[])payload.Clone(); BinaryPrimitives.WriteInt64LittleEndian(date.AsSpan(13), -1); Reject(date);
        var truncated = payload[..20]; Reject(truncated);
        byte[] badRaw = [0xff];
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(1)];
        Assert.True(BrotliEncoder.TryCompress(badRaw, compressed, out var written));
        var badUtf8 = payload[..29].Concat(compressed.Take(written)).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(badUtf8.AsSpan(21), 1);
        BinaryPrimitives.WriteInt32LittleEndian(badUtf8.AsSpan(25), written);
        Reject(badUtf8);
    }

    private static void Reject(byte[] payload) => Assert.Throws<InvalidDataException>(() => FreeProxyDbPageCaptureCodec.Decode(payload, SHA256.HashData(payload), MaximumBytes));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedCaptureReadProjectionPreservesGenerationAndEpochWithoutDatabaseLocksOrHttp(bool vpn)
    {
        // Provider-neutral read projection only. Relational admission, locks and
        // constraints are covered separately by the real PostgreSQL suite.
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>().UseInMemoryDatabase($"api-read-{Guid.NewGuid():N}").Options;
        var factory = new ProxySourceImportStoreIntegrationTests.SnapshotDbFactory(options);
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        var owner = Owner with { SourceId = Guid.NewGuid(), Vpn = vpn, Url = vpn ? FreeProxyDbPageCapture.VpnUrl : Owner.Url };
        var body = "{\"status\":1,\"data\":{\"total_count\":1,\"data\":[{\"id\":1,\"ip\":\"8.8.8.8\",\"port\":443,\"protocol\":\"vless\",\"connect_string\":\"vless://id@8.8.8.8:443\"}]}}";
        var capture = new FreeProxyDbPageCapture().Append(new(1, false, at, body), MaximumBytes)
            .Append(new(1, true, at, body), MaximumBytes);
        var payload = FreeProxyDbPageCaptureCodec.Encode(capture, MaximumBytes);
        var state = new SourceApiCaptureState
        {
            ProxySourceId = vpn ? null : owner.SourceId,
            VpnSourceId = vpn ? owner.SourceId : null,
            SourceUrl = owner.Url,
            SourceProtocol = 0,
            Payload = payload,
            PayloadHash = SHA256.HashData(payload),
            Complete = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await using (var db = factory.CreateDbContext())
        {
            if (vpn) db.VpnSources.Add(new VpnSource { Id = owner.SourceId, Name = "fixture", Provider = "fixture", License = "fixture", Url = owner.Url, DefaultProtocol = (VpnProtocol)0 });
            else db.Sources.Add(new ProxySource { Id = owner.SourceId, Name = "fixture", Url = owner.Url });
            db.SourceApiCaptureStates.Add(state);
            await db.SaveChangesAsync();
        }
        var checkpoint = Assert.IsType<SourceApiCaptureCheckpoint>(await new SourceApiCaptureStore(factory).LoadAsync(owner, CancellationToken.None));
        Assert.Equal(state.Id, checkpoint.Id);
        Assert.Equal(state.Version, checkpoint.Version);
        Assert.Equal(at, checkpoint.Capture.Inspect(owner.MaximumBytes).ObservedAt);
        var emptyOwner = owner with { SourceId = Guid.NewGuid() };
        Assert.Null(await new SourceApiCaptureStore(factory).LoadAsync(emptyOwner, CancellationToken.None));
        var result = await new FreeProxyDbSourceApiFetcher(factory).FetchAsync(owner,
            (_, _) => throw new InvalidOperationException("Completed read projection attempted HTTP."), CancellationToken.None);
        Assert.False(result.NetworkObserved);
        Assert.Equal(at, result.ObservedAt);
        Assert.Equal(at.AddHours(6), result.NextRefreshAt);
        Assert.Equal(state.Version, result.Checkpoint.Version);
        if (vpn) Assert.Single(VpnFeedParser.Parse(result.Fetch.Content!, VpnProtocol.Vless, 10));
        else Assert.Single(JsonDocument.Parse(result.Fetch.Content!).RootElement.GetProperty("data").EnumerateArray());
    }
    private static FreeProxyDbCapturedPage Page(int page, bool head, int total, params int[] ids) => new(page, head, DateTimeOffset.UtcNow,
        JsonSerializer.Serialize(new { status = 1, data = new { total_count = total, data = ids.Select(id => new { id }) } }));
    private sealed class NoDatabaseFactory : IDbContextFactory<ProxyHarborDbContext>
    {
        internal int Calls;
        public ProxyHarborDbContext CreateDbContext() { Calls++; throw new InvalidOperationException("Validation reached database."); }
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericTransportCannotRequestSearchApiDirectlyOrThroughRedirect(bool redirect)
    {
        using var handler = new ApiRedirectHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceHttpFetcher.FetchAsync(client,
            redirect ? "https://1.1.1.1/alias" : FreeProxyDbFeedFetcher.Url,
            null, null, 100_000, 2, 3, CancellationToken.None));
        Assert.Equal(redirect ? 1 : 0, handler.Requests);
        Assert.False(handler.ApiRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservedApiSlotMakesExactlyOneRequestOnFailureOrRedirect(bool redirect)
    {
        using var handler = new ReservedSlotHandler(redirect);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => SourceHttpFetcher.FetchAsync(client,
            "https://1.1.1.1/api", null, null, 100_000, 2, 3, CancellationToken.None,
            delayAsync: (_, _) => throw new InvalidOperationException("Unreserved retry."),
            respectRateLimit: true, sourceApiRequest: true));
        Assert.Equal(1, handler.Requests);
    }

    private sealed class ReservedSlotHandler(bool redirect) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var response = new HttpResponseMessage(redirect
                ? System.Net.HttpStatusCode.Redirect : System.Net.HttpStatusCode.ServiceUnavailable);
            if (redirect) response.Headers.Location = new Uri("https://1.1.1.1/other");
            return Task.FromResult(response);
        }
    }

    private sealed class ApiRedirectHandler : HttpMessageHandler
    {
        internal int Requests;
        internal bool ApiRequested;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (FreeProxyDbPageCapture.IsSearchUrl(request.RequestUri!.AbsoluteUri)) ApiRequested = true;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(FreeProxyDbFeedFetcher.Url);
            return Task.FromResult(response);
        }
    }
}
