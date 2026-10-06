using System.Buffers.Binary;
using System.IO.Compression;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ProxyCandidateSnapshotCodecTests
{
    [Fact]
    public void WindowsCrossCompressedPageBoundariesAndReachEveryUniqueCandidate()
    {
        var body = string.Join('\n', Enumerable.Range(1, ProxyCandidateSnapshotCodec.PageSize * 2 + 7)
            .Select(port => $"socks5://8.8.8.8:{port}"));
        var snapshot = ProxyCandidateSnapshotCodec.Encode(body + "\n8.8.8.8:1", ProxyProtocol.Socks5);
        Assert.Equal(ProxyCandidateSnapshotCodec.PageSize * 2 + 7, snapshot.Count);
        var seen = new List<ProxyCandidateKey>();
        var cursor = 0;
        ProxySnapshotWindow window;
        do
        {
            window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 200, candidate =>
            {
                seen.Add(candidate);
                return true;
            });
            Assert.InRange(window.Count, 1, 200);
            cursor = window.NextIndex;
        } while (!window.Completed);
        Assert.Equal(snapshot.Count, cursor);
        Assert.Equal(snapshot.Count, seen.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, snapshot.Count), seen.Select(candidate => candidate.Port));
        Assert.All(seen, candidate => Assert.Equal(ProxyProtocol.Socks5, candidate.Protocol));
    }

    [Fact]
    public void RejectedAdmissionAndUncommittedReplayKeepSameCandidate()
    {
        var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80\n1.1.1.1:443", ProxyProtocol.Http);
        var calls = 0;
        var first = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 200, _ => ++calls == 1);
        Assert.Equal(new ProxySnapshotWindow(1, 1, false), first);
        ProxyCandidateKey resumed = default;
        var next = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, first.NextIndex, 200, candidate =>
        {
            resumed = candidate;
            return true;
        });
        Assert.Equal(("1.1.1.1", 443, ProxyProtocol.Http), resumed.ToEndpoint());
        Assert.Equal(new ProxySnapshotWindow(1, 2, true), next);
        Assert.Equal(new ProxySnapshotWindow(0, 0, false),
            ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 200, _ => false));
        Assert.Equal(first,
            ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, _ => true));
    }

    [Fact]
    public void NormalizedSnapshotPreservesJsonProtocolsAndIpv6WithoutCredentialMetadata()
    {
        const string json = """
            {"data":[{"ip":"2606:4700:4700::1111","port":1080,"protocols":["socks5","https"]},
            {"ip":"8.8.8.8","port":80,"username":"secret","password":"hidden"}],
            "message":"9.9.9.9:8080"}
            """;
        var snapshot = ProxyCandidateSnapshotCodec.Encode(json, ProxyProtocol.Http);
        Assert.Equal(2, snapshot.Count);
        var endpoints = new List<(string, int, ProxyProtocol)>();
        var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 10, candidate =>
        {
            endpoints.Add(candidate.ToEndpoint());
            return true;
        });
        Assert.True(window.Completed);
        Assert.Equal([
            ("2606:4700:4700::1111", 1080, ProxyProtocol.Socks5),
            ("2606:4700:4700::1111", 1080, ProxyProtocol.Https)], endpoints);
    }

    [Fact]
    public void CorruptionAndTrailingPayloadFailClosed()
    {
        var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80", ProxyProtocol.Http);
        foreach (var payload in new[] { Array.Empty<byte>(), snapshot.Payload[..^1], snapshot.Payload.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<InvalidDataException>(() =>
                ProxyCandidateSnapshotCodec.ReadWindow(payload, 0, 1, _ => true));
        snapshot.Payload[0] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, _ => true));
    }

    [Theory]
    [InlineData("10.0.0.1", 80, 0)]
    [InlineData("8.8.8.8", 0, 0)]
    [InlineData("8.8.8.8", 80, 255)]
    public void DecodedCacheRecordsStillEnforceNetworkAndProtocolSafety(string host, ushort port, byte protocol)
    {
        var candidate = ProxyCandidateKey.Parse(host, 80, ProxyProtocol.Http);
        var record = new byte[20];
        BinaryPrimitives.WriteUInt64BigEndian(record, candidate.AddressHigh);
        BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(8), candidate.AddressLow);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(16), port);
        record[18] = protocol;
        var payload = WrapRecord(record);
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(payload, 0, 1, _ => true));
    }

    [Fact]
    public void DecompressionCannotExpandBeyondOneFixedSizePage()
    {
        var payload = WrapRecord(new byte[100_000]);
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(payload, 0, 1, _ => true));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 0)]
    public void InvalidWindowBoundsAreRejected(int start, int limit)
    {
        var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80", ProxyProtocol.Http);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, start, limit, _ => true));
    }

    [Fact]
    public void CompletedCursorNeedsNoCandidateAdmission()
    {
        var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80", ProxyProtocol.Http);
        Assert.Equal(new ProxySnapshotWindow(0, 1, true),
            ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 1, 1, _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void CorruptedSnapshotCountsCannotProduceSuccessfulAdmission(int count)
    {
        var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80", ProxyProtocol.Http);
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.Payload.AsSpan(4), count);
        var calls = 0;
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, _ => { calls++; return true; }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(42_000)]
    [InlineData(20)]
    public void InvalidCompressedPageLengthsCannotReadBeyondPayload(int length)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(payload, ProxyCandidateSnapshotCodec.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), length);
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(payload, 0, 1, _ => true));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(11)]
    public void MissingPageHeaderFailsBeforeAdmission(int length)
    {
        var payload = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, ProxyCandidateSnapshotCodec.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 1);
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(payload, 0, 1, _ => true));
    }

    [Theory]
    [InlineData("8.8.8.8", 2)]
    [InlineData("::ffff:8.8.8.8", 1)]
    [InlineData("fc00::1", 1)]
    [InlineData("2606:4700:4700::1111", 0)]
    public void CacheCannotAliasAddressFamiliesOrImportPrivateIpv6(string host, byte family)
    {
        var address = System.Net.IPAddress.Parse(host).GetAddressBytes();
        var record = new byte[20];
        address.CopyTo(record, 16 - address.Length);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(16), 443);
        record[19] = family;
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(WrapRecord(record), 0, 1, _ => true));
    }

    [Fact]
    public void CacheRejectsAnIpv4RecordWithHighAddressBits()
    {
        var record = new byte[20];
        BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(8), 0x100000008UL);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(16), 80);
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(WrapRecord(record), 0, 1, _ => true));
    }

    [Fact]
    public void OversizedBodiesAndPayloadsAreRejectedBeforeParsing()
    {
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.Encode(new string(' ', 10_000_001), ProxyProtocol.Http));
        Assert.Throws<InvalidDataException>(() =>
            ProxyCandidateSnapshotCodec.ReadWindow(new byte[ProxyCandidateSnapshotCodec.MaxPayloadBytes + 1], 0, 1, _ => true));
    }

    private static byte[] WrapRecord(byte[] record)
    {
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(record.Length)];
        Assert.True(BrotliEncoder.TryCompress(record, compressed, out var written, 1, 16));
        var payload = new byte[12 + written];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 0x50485331);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), written);
        compressed.AsSpan(0, written).CopyTo(payload.AsSpan(12));
        return payload;
    }
}
