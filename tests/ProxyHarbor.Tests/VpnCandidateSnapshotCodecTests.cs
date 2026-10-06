using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnCandidateSnapshotCodecTests
{
    [Fact]
    public void RepeatedBoundedWindowsReachEveryRecordBeyondCollectorPrefix()
    {
        var content = string.Join('\n', Enumerable.Range(1, 15_282).Select(port => $"vless://id@8.8.8.8:{port}"));
        var snapshot = VpnCandidateSnapshotCodec.Encode(content, VpnProtocol.Vless);
        Assert.Equal(15_282, snapshot.UniqueCount);
        Assert.Equal(15_282, snapshot.RecordCount);
        Assert.Equal(ProxyCandidateSnapshotCodec.HashBody(content), snapshot.BodyHash);
        var received = new List<VpnCandidate>();
        var cursor = 0;
        while (cursor < snapshot.UniqueCount)
        {
            var window = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 200, candidate =>
            {
                received.Add(candidate);
                return true;
            });
            Assert.InRange(window.Count, 1, 200);
            Assert.Equal(window.Count, window.NextIndex - cursor);
            Assert.Equal(window.NextIndex == snapshot.UniqueCount, window.Completed);
            cursor = window.NextIndex;
        }
        Assert.Equal(VpnFeedParser.Parse(content, VpnProtocol.Vless, 20_000), received);
        Assert.Equal(new VpnSnapshotWindow(0, cursor, true),
            VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 1, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void SnapshotPreservesLastDuplicateUriAfterPageBoundary()
    {
        var content = "vless://first@EXAMPLE.com:443\n" +
            string.Join('\n', Enumerable.Range(1, 2_048).Select(port => $"trojan://id@8.8.8.8:{port}")) +
            "\nvless://last@example.com:443#更新";
        var snapshot = VpnCandidateSnapshotCodec.Encode(content, VpnProtocol.Vless);
        Assert.Equal(2_049, snapshot.UniqueCount);
        Assert.Equal(2_050, snapshot.RecordCount);
        var received = new List<VpnCandidate>();
        var first = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, candidate => { received.Add(candidate); return true; });
        Assert.False(first.Completed);
        Assert.Equal("vless://last@example.com:443#更新", received[0].ConnectionUri);
        var last = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, first.NextIndex, 3_000, candidate => { received.Add(candidate); return true; });
        Assert.True(last.Completed);
        Assert.Equal("example.com", received[0].Host);
        Assert.Equal(VpnFeedParser.Parse(content, VpnProtocol.Vless, 3_000), received);
    }

    [Theory]
    [InlineData(VpnProtocol.OpenVpn, "remote 8.8.8.8 1194 udp\nremote 1.1.1.1 443 tcp")]
    [InlineData(VpnProtocol.WireGuard, "Endpoint = [2606:4700:4700::1111]:51820")]
    [InlineData(VpnProtocol.Vless, "vmess://id@EXAMPLE.com:443\nhy2://id@[2606:4700:4700::1111]:443#label")]
    public void SnapshotRoundTripsPublicUrisDnsIpv6AndNullConfigurationLinks(VpnProtocol fallback, string content)
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode(content, fallback);
        var received = new List<VpnCandidate>();
        var window = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 100, candidate => { received.Add(candidate); return true; });
        Assert.True(window.Completed);
        Assert.Equal(VpnFeedParser.Parse(content, fallback), received);
    }

    [Fact]
    public void LongUrisUseByteBoundedPagesWithoutDroppingOrReorderingRecords()
    {
        var content = string.Join('\n', Enumerable.Range(1, 50)
            .Select(port => $"vless://id@8.8.8.8:{port}#" + new string('界', 10_000)));
        var snapshot = VpnCandidateSnapshotCodec.Encode(content, VpnProtocol.Vless);
        var received = new List<VpnCandidate>();
        Assert.True(VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 100, candidate => { received.Add(candidate); return true; }).Completed);
        Assert.Equal(VpnFeedParser.Parse(content, VpnProtocol.Vless), received);
    }

    [Fact]
    public void RejectedAdmissionLeavesCursorAtUncommittedRecord()
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443\nvless://id@1.1.1.1:443", VpnProtocol.Vless);
        Assert.Equal(new VpnSnapshotWindow(0, 0, false), VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 10, _ => false));
        var calls = 0;
        Assert.Equal(new VpnSnapshotWindow(1, 1, false), VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 10, _ => ++calls == 1));
        VpnCandidate last = default;
        Assert.True(VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 1, 10, candidate => { last = candidate; return true; }).Completed);
        Assert.Equal("1.1.1.1", last.Host);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, -1)]
    [InlineData(4, 1_000_001)]
    [InlineData(8, 0)]
    [InlineData(8, 2)]
    [InlineData(12, 0)]
    [InlineData(12, 2_049)]
    [InlineData(16, -1)]
    [InlineData(16, 262_145)]
    [InlineData(20, -1)]
    [InlineData(20, 1_000_000)]
    [InlineData(24, -1)]
    [InlineData(24, 1_000_000)]
    public void CorruptMetadataFailsBeforeAdmission(int offset, int value)
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.Payload.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() =>
            VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData("10.0.0.1", 0, 0, null)]
    [InlineData("LOCALHOST", 0, 0, null)]
    [InlineData("8.8.8.8", 255, 0, null)]
    [InlineData("8.8.8.8", 0, 2, null)]
    [InlineData("8.8.8.8", (byte)VpnProtocol.WireGuard, 0, null)]
    [InlineData("8.8.8.8", (byte)VpnProtocol.Vless, 1, null)]
    [InlineData("8.8.8.8", (byte)VpnProtocol.Vless, 0, null)]
    [InlineData("8.8.8.8", (byte)VpnProtocol.Vmess, 0, null)]
    [InlineData("8.8.8.8", (byte)VpnProtocol.Trojan, 0, null)]
    [InlineData("8.8.8.8", (byte)VpnProtocol.Shadowsocks, 0, null)]
    [InlineData("8.8.8.8", (byte)VpnProtocol.Hysteria2, 1, null)]
    [InlineData("8.8.8.8", (byte)VpnProtocol.Tuic, 1, null)]
    [InlineData("8.8.8.8", 0, 0, "vless://id@10.0.0.1:443")]
    [InlineData("8.8.8.8", 0, 0, "vless://id@1.1.1.1:443")]
    [InlineData("8.8.8.8", 0, 0, "vless://id@8.8.8.8:443#x\0y")]
    public void CorruptRecordCannotImportUnsafeOrMismatchedReadyUri(string host, byte protocol, byte transport, string? uri)
    {
        using var record = new MemoryStream();
        using var writer = new BinaryWriter(record);
        var hostBytes = Encoding.UTF8.GetBytes(host);
        writer.Write((ushort)hostBytes.Length);
        writer.Write(hostBytes);
        writer.Write((ushort)443);
        writer.Write(protocol);
        writer.Write(transport);
        writer.Write(uri is null ? -1 : Encoding.UTF8.GetByteCount(uri));
        if (uri is not null) writer.Write(Encoding.UTF8.GetBytes(uri));
        Assert.Throws<InvalidDataException>(() =>
            VpnCandidateSnapshotCodec.ReadWindow(WrapPage(record.ToArray(), 1), 0, 1, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void CompressedBombAndTrailingDataAreRejected()
    {
        var payload = WrapPage(new byte[300_000], 1);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20), VpnCandidateSnapshotCodec.MaxPageBytes);
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.ReadWindow(payload, 0, 1, _ => true));
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.ReadWindow([.. snapshot.Payload, 0], 0, 1, _ => true));
    }

    [Fact]
    public void EmptyOrOversizedBodiesCannotBecomeCompleteSnapshots()
    {
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.Encode("no public proxies", VpnProtocol.Vless));
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.Encode(new string(' ', VpnCandidateSnapshotCodec.MaxBodyBytes + 1), VpnProtocol.Vless));
    }

    [Fact]
    public void DuplicateFloodCannotConsumeUniqueWindowQuotaOrHideLastUri()
    {
        var content = string.Join('\n', Enumerable.Range(0, 5_000).Select(i => $"vless://version{i}@EXAMPLE.com:443")) +
            "\nvless://id@1.1.1.1:443";
        var snapshot = VpnCandidateSnapshotCodec.Encode(content, VpnProtocol.Vless);
        Assert.Equal(5_001, snapshot.RecordCount);
        Assert.Equal(2, snapshot.UniqueCount);
        var received = new List<VpnCandidate>();
        Assert.Equal(new VpnSnapshotWindow(2, 2, true),
            VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 2, item => { received.Add(item); return true; }));
        Assert.Equal("vless://version4999@EXAMPLE.com:443", received[0].ConnectionUri);
        Assert.Equal("1.1.1.1", received[1].Host);
    }

    [Fact]
    public void AlternatingDistantLatestPagesRetainFirstIdentityOrderAcrossSmallWindows()
    {
        var first = Enumerable.Range(1, 50).Select(port => $"vless://first@8.8.8.8:{port}");
        var latest = Enumerable.Range(1, 50).OrderBy(port => port % 7)
            .Select(port => $"vless://latest@8.8.8.8:{port}#" + new string('界', 10_000));
        var content = string.Join('\n', first.Concat(latest));
        var snapshot = VpnCandidateSnapshotCodec.Encode(content, VpnProtocol.Vless);
        var received = new List<VpnCandidate>();
        var cursor = 0;
        while (cursor < snapshot.UniqueCount)
            cursor = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 3, item => { received.Add(item); return true; }).NextIndex;
        Assert.Equal(VpnFeedParser.Parse(content, VpnProtocol.Vless), received);
        Assert.All(received, item => Assert.StartsWith("vless://latest@", item.ConnectionUri));
    }

    [Theory]
    [InlineData(false, 17)]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(true, 1)]
    public void CorruptCanonicalIndexCannotReferenceNonPageOrNonRecord(bool changeRecord, int value)
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        var indexOffset = BinaryPrimitives.ReadInt32LittleEndian(snapshot.Payload.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.Payload.AsSpan(indexOffset + (changeRecord ? 4 : 0)), value);
        Assert.Throws<InvalidDataException>(() =>
            VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, _ => throw new InvalidOperationException()));
    }

    private static byte[] WrapPage(byte[] page, int count)
    {
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(page.Length)];
        Assert.True(BrotliEncoder.TryCompress(page, compressed, out var written, quality: 1, window: 16));
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(VpnCandidateSnapshotCodec.Magic);
        writer.Write(count);
        writer.Write(count);
        writer.Write(28 + written);
        writer.Write(count);
        writer.Write(page.Length);
        writer.Write(written);
        writer.Write(compressed.AsSpan(0, written));
        for (var index = 0; index < count; index++)
        {
            writer.Write(16);
            writer.Write(index);
        }
        return output.ToArray();
    }
}
