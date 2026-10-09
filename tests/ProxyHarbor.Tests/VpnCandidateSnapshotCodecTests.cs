using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnCandidateSnapshotCodecTests
{
    [Fact]
    public void OriginalRecordWindowsRetainSupersededCredentialsAndExactDuplicates()
    {
        var legacy = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:old@8.8.8.8:443")) + "#old";
        const string modern = "ss://aes-256-gcm:new@8.8.8.8:443#new";
        const string other = "ss://aes-256-gcm:other@1.1.1.1:443";
        var body = string.Join('\n', legacy, other, modern, modern);
        var expected = new List<VpnCandidate>();
        VpnFeedParser.ParseRecordsTo(body, VpnProtocol.Shadowsocks, 10, expected.Add);
        var snapshot = VpnCandidateSnapshotCodec.Encode(body, VpnProtocol.Shadowsocks);
        Assert.Equal(2, snapshot.UniqueCount);
        Assert.Equal(4, snapshot.RecordCount);
        var received = new List<VpnCandidate>();
        var cursor = 0;
        while (cursor < snapshot.RecordCount)
        {
            Assert.Equal(new VpnSnapshotWindow(0, cursor, false),
                VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, cursor, 2, _ => false));
            var window = VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, cursor, 2,
                candidate => { received.Add(candidate); return true; });
            Assert.InRange(window.Count, 1, 2);
            Assert.Equal(cursor + window.Count, window.NextIndex);
            cursor = window.NextIndex;
        }
        Assert.Equal(expected, received);
        Assert.Equal(new VpnSnapshotWindow(0, snapshot.RecordCount, true),
            VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, snapshot.RecordCount, 1, _ => true));
        var indexed = new List<VpnCandidate>();
        Assert.True(VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 10,
            candidate => { indexed.Add(candidate); return true; }).Completed);
        Assert.Equal(new[] { modern, other }, indexed.Select(candidate => candidate.ConnectionUri));
    }

    [Fact]
    public void OriginalRecordCursorResumesAfterCallbackRefusesCandidate()
    {
        var content = string.Join('\n', Enumerable.Range(1, 6).Select(id => $"vless://id{id}@8.8.8.8:443?security=tls&sni=node.example#{id}"));
        var snapshot = VpnCandidateSnapshotCodec.Encode(content, VpnProtocol.Vless);
        Assert.Equal(1, snapshot.UniqueCount);
        var received = new List<VpnCandidate>();
        var first = VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, 0, 6, candidate =>
        {
            if (received.Count == 2) return false;
            received.Add(candidate);
            return true;
        });
        Assert.Equal(new VpnSnapshotWindow(2, 2, false), first);
        var last = VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, first.NextIndex, 6,
            candidate => { received.Add(candidate); return true; });
        Assert.Equal(new VpnSnapshotWindow(4, 6, true), last);
        var expected = new List<VpnCandidate>();
        VpnFeedParser.ParseRecordsTo(content, VpnProtocol.Vless, 10, expected.Add);
        Assert.Equal(expected, received);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("界")]
    public void OriginalYamlVariantsDrainAcrossPagesAndTextLimitedWindows(string character)
    {
        var setting = string.Concat(Enumerable.Repeat(character, 14_000));
        var content = "setting: &long '" + setting + "'\nproxies: [" + string.Join(',', Enumerable.Range(1, 400).Select(id =>
            $"{{name: node{id}, type: vless, server: 8.8.8.8, port: 443, uuid: id{id}, servername: node.example, future-option: *long}}")) + "]";
        var expected = new List<VpnCandidate>();
        VpnFeedParser.ParseRecordsTo(content, VpnProtocol.Vless, 500, expected.Add);
        var snapshot = VpnCandidateSnapshotCodec.Encode(content, VpnProtocol.Vless);
        Assert.Equal(1, snapshot.UniqueCount);
        Assert.Equal(400, snapshot.RecordCount);
        var received = new List<VpnCandidate>();
        var cursor = 0;
        var windows = 0;
        while (cursor < snapshot.RecordCount)
        {
            long textBytes = 0;
            var window = VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, cursor, 400, candidate =>
            {
                textBytes += 2L * (candidate.Host.Length + (candidate.ConnectionUri?.Length ?? 0) + (candidate.ClashConfiguration?.Length ?? 0));
                received.Add(candidate);
                return true;
            });
            Assert.InRange(textBytes, 1, VpnCandidateSnapshotCodec.MaxWindowTextBytes);
            Assert.InRange(window.Count, 1, 399);
            Assert.Equal(cursor + window.Count, window.NextIndex);
            Assert.Equal(window.NextIndex == snapshot.RecordCount, window.Completed);
            cursor = window.NextIndex;
            windows++;
        }
        Assert.True(windows > 1);
        Assert.Equal(expected, received);
    }

    [Fact]
    public void OriginalRecordsReaderSupportsLegacyUriOnlyPages()
    {
        const string first = "vless://first@8.8.8.8:443";
        const string last = "vless://last@8.8.8.8:443";
        var bytes = Record(first, null, includeConfiguration: false)
            .Concat(Record(last, null, includeConfiguration: false)).ToArray();
        var payload = WrapPage(bytes, 2);
        var received = new List<VpnCandidate>();
        Assert.Equal(new VpnSnapshotWindow(2, 2, true), VpnCandidateSnapshotCodec.ReadRecordsWindow(payload, 0, 10,
            candidate => { received.Add(candidate); return true; }));
        Assert.Equal(new[] { first, last }, received.Select(candidate => candidate.ConnectionUri));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    public void OriginalRecordsReaderRejectsCorruptLayoutBeforeAdmission(int offset)
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.Payload.AsSpan(offset), -1);
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, 0, 10,
            _ => throw new InvalidOperationException("Corrupt record was admitted")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalRecordsReaderValidatesIndexLocationsWithoutUsingEndpointIndex(bool recordIndex)
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(snapshot.Payload.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.Payload.AsSpan(offset + (recordIndex ? 4 : 0)), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, 0, 10,
            _ => throw new InvalidOperationException("Corrupt index was admitted")));
    }

    [Fact]
    public void OriginalRecordsReaderRevalidatesSupersededUriProvenance()
    {
        var bytes = Record("vless://id@127.0.0.1:443", null)
            .Concat(Record("vless://id@8.8.8.8:443", null)).ToArray();
        var payload = WrapPage(bytes, 2, VpnCandidateSnapshotCodec.Magic);
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.ReadRecordsWindow(payload, 0, 1,
            _ => throw new InvalidOperationException("Private superseded URI was admitted")));
    }

    [Fact]
    public void OriginalRecordsReaderValidatesBoundsAndPreservesCallbackExceptions()
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        Assert.Throws<ArgumentNullException>(() => VpnCandidateSnapshotCodec.ReadRecordsWindow(null!, 0, 1, _ => true));
        Assert.Throws<ArgumentNullException>(() => VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, 0, 1, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, -1, 1, _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, 2, 1, _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => VpnCandidateSnapshotCodec.ReadRecordsWindow(snapshot.Payload, 0, 0, _ => true));
        var failure = new InvalidOperationException("Callback failed");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => VpnCandidateSnapshotCodec.ReadRecordsWindow(
            snapshot.Payload, 0, 1, _ => throw failure)));
    }

    [Theory]
    [InlineData(false, "x")]
    [InlineData(true, "x")]
    [InlineData(true, "界")]
    public void TextBoundedWindowsEventuallyDrainLongUrisAndAliasExpandedConfigurations(bool yaml, string character)
    {
        var setting = string.Concat(Enumerable.Repeat(character, 14_000));
        var content = yaml
            ? "setting: &long '" + setting + "'\nproxies: [" + string.Join(',', Enumerable.Range(1, 400).Select(port =>
                $"{{name: node{port}, type: vless, server: 8.8.8.8, port: {port}, uuid: published, future-option: *long}}")) + "]"
            : string.Join('\n', Enumerable.Range(1, 400).Select(port => $"vless://id@8.8.8.8:{port}#{setting}"));
        var snapshot = VpnCandidateSnapshotCodec.Encode(content, VpnProtocol.Vless);
        Assert.Equal(400, snapshot.UniqueCount);
        var ports = new HashSet<int>();
        var cursor = 0;
        var windows = 0;
        while (cursor < snapshot.UniqueCount)
        {
            Assert.Equal(new VpnSnapshotWindow(0, cursor, false), VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 400, _ => false));
            long retainedBytes = 0;
            var window = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 400, candidate =>
            {
                retainedBytes += 2L * (candidate.Host.Length + (candidate.ConnectionUri?.Length ?? 0) + (candidate.ClashConfiguration?.Length ?? 0));
                Assert.True(ports.Add(candidate.Port));
                Assert.Contains(setting, yaml ? candidate.ClashConfiguration : candidate.ConnectionUri);
                return true;
            });
            Assert.InRange(retainedBytes, 1, VpnCandidateSnapshotCodec.MaxWindowTextBytes);
            Assert.InRange(window.Count, 1, 399);
            Assert.Equal(cursor + window.Count, window.NextIndex);
            Assert.Equal(window.NextIndex == snapshot.UniqueCount, window.Completed);
            cursor = window.NextIndex;
            windows++;
        }
        Assert.True(windows > 1);
        Assert.Equal(Enumerable.Range(1, 400), ports.Order());
    }

    [Fact]
    public void LegacyShadowsocksRoundTripsWithModernDuplicateAcrossBoundedWindows()
    {
        var legacy = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:old@8.8.8.8:443")) + "#old,,";
        const string modern = "ss://aes-256-gcm:new@8.8.8.8:443#new";
        var other = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:other@1.1.1.1:8388"));
        var unsafeUri = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:private@127.0.0.1:443"));
        var body = JsonSerializer.Serialize(legacy) + ",\n" + other + "\n" + modern + "\n" + unsafeUri;
        var snapshot = VpnCandidateSnapshotCodec.Encode(body, VpnProtocol.Shadowsocks);
        Assert.Equal(2, snapshot.UniqueCount);
        Assert.Equal(3, snapshot.RecordCount);
        var received = new List<VpnCandidate>();
        var first = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1,
            candidate => { received.Add(candidate); return true; });
        Assert.False(first.Completed);
        Assert.Equal(modern, Assert.Single(received).ConnectionUri);
        var last = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, first.NextIndex, 1,
            candidate => { received.Add(candidate); return true; });
        Assert.True(last.Completed);
        Assert.Equal(other, received[1].ConnectionUri);
        Assert.Equal(VpnFeedParser.Parse(body, VpnProtocol.Shadowsocks), received);
    }

    [Theory]
    [InlineData("#label,,", false)]
    [InlineData("?alpn=h2,http/1.1,", false)]
    [InlineData("#label,", true)]
    [InlineData("#label\"", true)]
    public void SupersededUriPunctuationCannotInvalidatePageContainingCanonicalRecord(string suffix, bool quoted)
    {
        var first = "vless://first@8.8.8.8:443" + suffix;
        const string last = "vless://last@8.8.8.8:443#new";
        var body = (quoted ? JsonSerializer.Serialize(first) + "," : first) + "\n" + last;
        var records = new List<VpnCandidate>();
        Assert.Equal(2, VpnFeedParser.ParseRecordsTo(body, VpnProtocol.Vless, 10, records.Add));
        Assert.Equal(first, records[0].ConnectionUri);
        var snapshot = VpnCandidateSnapshotCodec.Encode(body, VpnProtocol.Vless);
        Assert.Equal(2, snapshot.RecordCount);
        var received = new List<VpnCandidate>();
        Assert.True(VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1,
            candidate => { received.Add(candidate); return true; }).Completed);
        Assert.Equal(last, Assert.Single(received).ConnectionUri);
    }

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

    [Theory]
    [InlineData(16_384, true)]
    [InlineData(16_385, false)]
    public void SnapshotEncodingHonorsDatabaseUriCharacterLimitBeforePersisting(int length, bool accepted)
    {
        const string prefix = "vless://id@8.8.8.8:443#";
        var uri = prefix + new string('x', length - prefix.Length);
        if (!accepted)
        {
            Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.Encode(uri, VpnProtocol.Vless));
            return;
        }
        var snapshot = VpnCandidateSnapshotCodec.Encode(uri, VpnProtocol.Vless);
        var received = new List<VpnCandidate>();
        Assert.True(VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1,
            candidate => { received.Add(candidate); return true; }).Completed);
        Assert.Equal(uri, Assert.Single(received).ConnectionUri);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClashSettingsAndDependenciesRoundTripAcrossWindows(bool base64)
    {
        const string yaml = "proxies: [{name: primary, type: anytls, server: example.com, port: 443, password: '0012', dialer-proxy: peer, future-option: {values: [a,b]}}, {name: peer, type: socks5, server: 8.8.8.8, port: 1080, username: user, password: secret}]";
        var body = base64 ? Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml)) : yaml;
        var snapshot = VpnCandidateSnapshotCodec.Encode(body, VpnProtocol.Vless);
        Assert.Equal(VpnCandidateSnapshotCodec.Magic, BinaryPrimitives.ReadInt32LittleEndian(snapshot.Payload));
        Assert.Equal(2, snapshot.UniqueCount);
        var received = new List<VpnCandidate>();
        var first = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, candidate => { received.Add(candidate); return true; });
        Assert.False(first.Completed);
        var last = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, first.NextIndex, 1, candidate => { received.Add(candidate); return true; });
        Assert.True(last.Completed);
        Assert.Equal(VpnFeedParser.Parse(body, VpnProtocol.Vless), received);
        Assert.All(received, candidate => { Assert.Null(candidate.ConnectionUri); Assert.True(ClashYamlFeedParser.IsValidStandalone(candidate)); });
        Assert.Contains("0012", received[0].ClashConfiguration);
        Assert.Contains("secret", received[0].ClashConfiguration);
    }

    [Fact]
    public void LegacyVersionTwoSnapshotRemainsReadable()
    {
        const string uri = "vless://id@8.8.8.8:443#legacy";
        var snapshot = WrapPage(Record(uri, null, includeConfiguration: false), 1);
        var received = new List<VpnCandidate>();
        Assert.True(VpnCandidateSnapshotCodec.ReadWindow(snapshot, 0, 1, candidate => { received.Add(candidate); return true; }).Completed);
        Assert.Equal(new VpnCandidate("8.8.8.8", 443, VpnProtocol.Vless, "tcp", uri), Assert.Single(received));
        var state = new VpnSourceImportState
        {
            SourceUrl = "https://example.com/feed",
            CandidateCount = 1,
            ProfileRecordCount = 1,
            NextIndex = 0,
            Payload = snapshot,
            PayloadHash = System.Security.Cryptography.SHA256.HashData(snapshot),
            SnapshotBodyHash = new byte[32],
            FreshBodyHash = new byte[32]
        };
        Assert.True(VpnSourceImportStore.ReadWindow(state, 1, _ => true).Completed);
    }

    [Theory]
    [InlineData("vless")]
    [InlineData("vmess")]
    [InlineData("trojan")]
    [InlineData("ss")]
    [InlineData("ssr")]
    [InlineData("hysteria")]
    [InlineData("hysteria2")]
    [InlineData("tuic")]
    [InlineData("wireguard")]
    [InlineData("anytls")]
    [InlineData("http")]
    [InlineData("socks4")]
    [InlineData("socks5")]
    public void EverySupportedClashProtocolPreservesTransportAndConfiguration(string type)
    {
        var yaml = $"proxies: [{{type: {type}, server: 8.8.8.8, port: 443, password: published, cipher: aes-128-gcm}}]";
        var snapshot = VpnCandidateSnapshotCodec.Encode(yaml, VpnProtocol.Vless);
        var received = new List<VpnCandidate>();
        Assert.True(VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, candidate => { received.Add(candidate); return true; }).Completed);
        Assert.Equal(VpnFeedParser.Parse(yaml, VpnProtocol.Vless), received);
    }

    [Fact]
    public void DuplicateClashConfigurationKeepsNewestSettingsAcrossByteBoundedPages()
    {
        var nodes = Enumerable.Range(1, 30).Select(port => $"{{name: node{port}, type: vless, server: 8.8.8.8, port: {port}, password: first, future-option: '{new string('x', 10_000)}'}}");
        var yaml = "proxies: [" + string.Join(',', nodes) + ",{name: latest, type: vless, server: 8.8.8.8, port: 1, password: latest}]";
        var snapshot = VpnCandidateSnapshotCodec.Encode(yaml, VpnProtocol.Vless);
        Assert.Equal(31, snapshot.RecordCount);
        Assert.Equal(30, snapshot.UniqueCount);
        var received = new List<VpnCandidate>();
        for (var cursor = 0; cursor < snapshot.UniqueCount;)
            cursor = VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, cursor, 3, candidate => { received.Add(candidate); return true; }).NextIndex;
        Assert.Equal(VpnFeedParser.Parse(yaml, VpnProtocol.Vless), received);
        Assert.Contains("latest", received[0].ClashConfiguration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("proxies: [{type: vless, server: 1.1.1.1, port: 443}]")]
    [InlineData("proxies: [{type: vless, server: 127.0.0.1, port: 443}]")]
    [InlineData("proxies: [{type: vless, server: 8.8.8.8, port: 444}]")]
    [InlineData("proxies: [{type: trojan, server: 8.8.8.8, port: 443}]")]
    [InlineData("proxies: [{type: vless, server: 8.8.8.8, port: 443, dialer-proxy: missing}]")]
    [InlineData("proxies: [{type: vless, server: 8.8.8.8, port: 443}, {type: http, server: 127.0.0.1, port: 80}]")]
    [InlineData("proxies: [{type: vless, server: 8.8.8.8, port: 443}]\nexternal-controller: 0.0.0.0:9090")]
    public void CorruptConfigurationCannotBypassEndpointOrDependencyValidation(string configuration)
    {
        var snapshot = WrapPage(Record(null, configuration), 1, VpnCandidateSnapshotCodec.Magic);
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.ReadWindow(snapshot, 0, 1, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void NewVersionRevalidatesUriAndConfigurationIndependently()
    {
        const string yaml = "proxies: [{type: vless, server: 8.8.8.8, port: 443}]";
        var valid = WrapPage(Record("vless://id@8.8.8.8:443", yaml), 1, VpnCandidateSnapshotCodec.Magic);
        Assert.True(VpnCandidateSnapshotCodec.ReadWindow(valid, 0, 1, _ => true).Completed);
        var invalid = WrapPage(Record("vless://id@127.0.0.1:443", yaml), 1, VpnCandidateSnapshotCodec.Magic);
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.ReadWindow(invalid, 0, 1, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void ChangingVersionThreeHeaderToSupportedLegacyVersionCannotAdmitNewRecordLayout()
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless);
        BinaryPrimitives.WriteInt32LittleEndian(snapshot.Payload, VpnCandidateSnapshotCodec.LegacyMagic);
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 1, _ => throw new InvalidOperationException()));
    }

    private static byte[] Record(string? uri, string? configuration, bool includeConfiguration = true)
    {
        using var record = new MemoryStream();
        using var writer = new BinaryWriter(record);
        var host = Encoding.UTF8.GetBytes("8.8.8.8");
        writer.Write((ushort)host.Length);
        writer.Write(host);
        writer.Write((ushort)443);
        writer.Write((byte)VpnProtocol.Vless);
        writer.Write((byte)0);
        writer.Write(uri is null ? -1 : Encoding.UTF8.GetByteCount(uri));
        if (uri is not null) writer.Write(Encoding.UTF8.GetBytes(uri));
        if (includeConfiguration)
        {
            writer.Write(configuration is null ? -1 : Encoding.UTF8.GetByteCount(configuration));
            if (configuration is not null) writer.Write(Encoding.UTF8.GetBytes(configuration));
        }
        return record.ToArray();
    }

    private static byte[] WrapPage(byte[] page, int count, int magic = VpnCandidateSnapshotCodec.LegacyMagic)
    {
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(page.Length)];
        Assert.True(BrotliEncoder.TryCompress(page, compressed, out var written, quality: 1, window: 16));
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(magic);
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
