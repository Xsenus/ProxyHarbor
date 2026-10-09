using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Индекс первых endpoint с последним URI из полного feed и bounded decompression страниц.</summary>
internal static class VpnCandidateSnapshotCodec
{
    internal const int LegacyMagic = 0x50485632;
    internal const int Magic = 0x50485633;
    internal const int MaxRecords = 1_000_000;
    internal const int MaxBodyBytes = 32 * 1024 * 1024;
    internal const int MaxPayloadBytes = 48 * 1024 * 1024;
    internal const int MaxPageBytes = 256 * 1024;
    internal const int MaxPageRecords = 2_048;
    internal const int MaxWindowTextBytes = 8 * 1024 * 1024;
    private const int MaxUriBytes = 65_536;
    private const int MaxUriCharacters = 16_384;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static VpnCandidateSnapshot Encode(string content, VpnProtocol fallback)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (StrictUtf8.GetByteCount(content) > MaxBodyBytes)
            throw new InvalidDataException("Body VPN-источника превышает безопасный размер снимка.");
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, StrictUtf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        var identities = new Dictionary<(string Host, int Port, VpnProtocol Protocol, string Transport), int>();
        var locations = new List<(int PageOffset, int RecordIndex)>();
        var page = new byte[MaxPageBytes];
        var used = 0;
        var pageRecords = 0;
        var recordCount = VpnFeedParser.ParseRecordsTo(content, fallback, MaxRecords, candidate =>
        {
            var hostBytes = StrictUtf8.GetByteCount(candidate.Host);
            var uriBytes = candidate.ConnectionUri is null ? -1 : StrictUtf8.GetByteCount(candidate.ConnectionUri);
            var configurationBytes = candidate.ClashConfiguration is null ? -1 : StrictUtf8.GetByteCount(candidate.ClashConfiguration);
            if (hostBytes is < 1 or > 253 || uriBytes > MaxUriBytes || candidate.ConnectionUri?.Length > MaxUriCharacters ||
                configurationBytes > ClashYamlFeedParser.MaximumConfigurationBytes ||
                candidate.ClashConfiguration?.Length > ClashYamlFeedParser.MaximumConfigurationCharacters)
                throw InvalidSnapshot();
            var recordBytes = 14 + hostBytes + Math.Max(0, uriBytes) + Math.Max(0, configurationBytes);
            if (pageRecords == MaxPageRecords || used + recordBytes > MaxPageBytes) FlushPage();
            var identity = (candidate.Host, candidate.Port, candidate.Protocol, candidate.Transport);
            var location = ((int)output.Position, pageRecords);
            if (identities.TryGetValue(identity, out var ordinal)) locations[ordinal] = location;
            else
            {
                identities.Add(identity, locations.Count);
                locations.Add(location);
            }
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(used), (ushort)hostBytes);
            used += 2;
            used += StrictUtf8.GetBytes(candidate.Host.AsSpan(), page.AsSpan(used, hostBytes));
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(used), (ushort)candidate.Port);
            page[used + 2] = (byte)candidate.Protocol;
            page[used + 3] = candidate.Transport == "udp" ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(used + 4), uriBytes);
            used += 8;
            if (uriBytes >= 0)
                used += StrictUtf8.GetBytes(candidate.ConnectionUri.AsSpan(), page.AsSpan(used, uriBytes));
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(used), configurationBytes);
            used += 4;
            if (configurationBytes >= 0)
                used += StrictUtf8.GetBytes(candidate.ClashConfiguration.AsSpan(), page.AsSpan(used, configurationBytes));
            pageRecords++;
        });
        if (recordCount == 0) throw new InvalidDataException("VPN-источник не содержит распознаваемых публичных записей.");
        if (pageRecords > 0) FlushPage();
        var indexOffset = (int)output.Position;
        if (output.Length + locations.Count * 8L > MaxPayloadBytes)
            throw new InvalidDataException("VPN-снимок превышает безопасный размер хранилища.");
        foreach (var (pageOffset, recordIndex) in locations)
        {
            writer.Write(pageOffset);
            writer.Write(recordIndex);
        }
        output.Position = sizeof(int);
        writer.Write(recordCount);
        writer.Write(locations.Count);
        writer.Write(indexOffset);
        return new VpnCandidateSnapshot(output.ToArray(), locations.Count, recordCount,
            ProxyCandidateSnapshotCodec.HashBody(content));

        void FlushPage()
        {
            var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(used)];
            if (!BrotliEncoder.TryCompress(page.AsSpan(0, used), compressed, out var written, quality: 1, window: 16))
                throw InvalidSnapshot();
            writer.Write(pageRecords);
            writer.Write(used);
            writer.Write(written);
            writer.Write(compressed.AsSpan(0, written));
            if (output.Length > MaxPayloadBytes)
                throw new InvalidDataException("VPN-снимок превышает безопасный размер хранилища.");
            used = 0;
            pageRecords = 0;
        }
    }

    internal static VpnSnapshotWindow ReadWindow(
        byte[] payload, int startIndex, int maxRecords, Func<VpnCandidate, bool> accept)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecords, 1);
        var layout = ReadLayout(payload);
        var hasConfigurations = layout.HasConfigurations;
        var uniqueCount = layout.UniqueCount;
        var indexOffset = layout.IndexOffset;
        var pages = layout.Pages;
        ArgumentOutOfRangeException.ThrowIfLessThan(startIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, uniqueCount);
        // A hostile duplicate order may bounce between pages. Limit retained decoded
        // URI strings to four byte-bounded pages rather than the entire feed.
        var cache = new Dictionary<int, (VpnCandidate[] Records, LinkedListNode<int> Node)>();
        var recent = new LinkedList<int>();
        var next = startIndex;
        long returnedTextBytes = 0;
        while (next < uniqueCount && next - startIndex < maxRecords)
        {
            var entry = indexOffset + next * 8;
            var pageOffset = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(entry));
            var recordIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(entry + 4));
            if (!pages.TryGetValue(pageOffset, out var metadata) || recordIndex < 0 || recordIndex >= metadata.Count)
                throw InvalidSnapshot();
            if (!cache.TryGetValue(pageOffset, out var decoded))
            {
                var records = ReadPage(payload, pageOffset, metadata, hasConfigurations);
                if (cache.Count == 4)
                {
                    cache.Remove(recent.First!.Value);
                    recent.RemoveFirst();
                }
                decoded = (records, recent.AddLast(pageOffset));
                cache.Add(pageOffset, decoded);
            }
            else
            {
                recent.Remove(decoded.Node);
                recent.AddLast(decoded.Node);
            }
            var candidate = decoded.Records[recordIndex];
            // Compressed aliases can expand a small feed into many long settings.
            // Bound retained UTF-16 text as well as record count; the next window
            // resumes at this exact identity rather than dropping the remainder.
            var textBytes = 2L * (candidate.Host.Length + (candidate.ConnectionUri?.Length ?? 0) +
                (candidate.ClashConfiguration?.Length ?? 0));
            if (returnedTextBytes + textBytes > MaxWindowTextBytes) break;
            if (!accept(candidate)) break;
            returnedTextBytes += textBytes;
            next++;
        }
        return new VpnSnapshotWindow(next - startIndex, next, next == uniqueCount);
    }

    /// <summary>Reads original feed records, including superseded settings for the same endpoint.</summary>
    internal static VpnSnapshotWindow ReadRecordsWindow(
        byte[] payload, int startIndex, int maxRecords, Func<VpnCandidate, bool> accept)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecords, 1);
        var layout = ReadLayout(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(startIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, layout.RecordCount);
        for (var index = 0; index < layout.UniqueCount; index++)
        {
            var entry = layout.IndexOffset + index * 8;
            var pageOffset = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(entry));
            var recordIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(entry + 4));
            if (!layout.Pages.TryGetValue(pageOffset, out var metadata) || recordIndex < 0 || recordIndex >= metadata.Count)
                throw InvalidSnapshot();
        }
        var next = startIndex;
        long returnedTextBytes = 0;
        foreach (var (pageOffset, metadata) in layout.Pages.OrderBy(entry => entry.Key))
        {
            if (next >= metadata.RecordStart + metadata.Count) continue;
            if (next - startIndex >= maxRecords) break;
            var records = ReadPage(payload, pageOffset, metadata, layout.HasConfigurations);
            for (var index = next - metadata.RecordStart; index < records.Length && next - startIndex < maxRecords; index++)
            {
                var candidate = records[index];
                var textBytes = 2L * (candidate.Host.Length + (candidate.ConnectionUri?.Length ?? 0) +
                    (candidate.ClashConfiguration?.Length ?? 0));
                if (returnedTextBytes + textBytes > MaxWindowTextBytes || !accept(candidate))
                    return new VpnSnapshotWindow(next - startIndex, next, next == layout.RecordCount);
                returnedTextBytes += textBytes;
                next++;
            }
        }
        return new VpnSnapshotWindow(next - startIndex, next, next == layout.RecordCount);
    }

    private static SnapshotLayout ReadLayout(byte[] payload)
    {
        if (payload.Length is < 16 or > MaxPayloadBytes)
            throw InvalidSnapshot();
        var magic = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (magic is not (Magic or LegacyMagic)) throw InvalidSnapshot();
        var hasConfigurations = magic == Magic;
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
        var uniqueCount = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8));
        if (count is < 1 or > MaxRecords || uniqueCount < 1 || uniqueCount > count) throw InvalidSnapshot();
        var indexOffset = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(12));
        if (indexOffset < 16 || indexOffset > payload.Length ||
            payload.Length - indexOffset != uniqueCount * 8) throw InvalidSnapshot();
        var position = 16;
        var pageStart = 0;
        var pages = new Dictionary<int, SnapshotPage>();
        while (pageStart < count)
        {
            if (position > indexOffset - 12) throw InvalidSnapshot();
            var pageOffset = position;
            var pageCount = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(position));
            var plainBytes = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(position + 4));
            var compressedBytes = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(position + 8));
            position += 12;
            if (pageCount is < 1 or > MaxPageRecords || pageCount > count - pageStart ||
                plainBytes < pageCount * (hasConfigurations ? 15 : 11) || plainBytes > MaxPageBytes || compressedBytes < 1 ||
                compressedBytes > BrotliEncoder.GetMaxCompressedLength(MaxPageBytes) || compressedBytes > indexOffset - position)
                throw InvalidSnapshot();
            pages.Add(pageOffset, new SnapshotPage(pageCount, plainBytes, compressedBytes, pageStart));
            position += compressedBytes;
            pageStart += pageCount;
        }
        if (position != indexOffset) throw InvalidSnapshot();
        return new SnapshotLayout(hasConfigurations, count, uniqueCount, indexOffset, pages);
    }

    private static VpnCandidate[] ReadPage(byte[] payload, int pageOffset, SnapshotPage metadata, bool hasConfigurations)
    {
        var page = new byte[metadata.PlainBytes];
        if (!BrotliDecoder.TryDecompress(payload.AsSpan(pageOffset + 12, metadata.CompressedBytes), page, out var written) || written != page.Length)
            throw InvalidSnapshot();
        var records = new VpnCandidate[metadata.Count];
        var position = 0;
        for (var index = 0; index < records.Length; index++) records[index] = ReadRecord(page, ref position, hasConfigurations);
        if (position != page.Length) throw InvalidSnapshot();
        return records;
    }

    private sealed record SnapshotLayout(bool HasConfigurations, int RecordCount, int UniqueCount, int IndexOffset,
        IReadOnlyDictionary<int, SnapshotPage> Pages);
    private readonly record struct SnapshotPage(int Count, int PlainBytes, int CompressedBytes, int RecordStart);

    private static VpnCandidate ReadRecord(ReadOnlySpan<byte> page, ref int position, bool hasConfigurations)
    {
        if (position > page.Length - 2) throw InvalidSnapshot();
        var hostBytes = BinaryPrimitives.ReadUInt16LittleEndian(page[position..]);
        position += 2;
        if (hostBytes is < 1 or > 253 || hostBytes > page.Length - position - 8) throw InvalidSnapshot();
        var host = ReadText(page.Slice(position, hostBytes));
        position += hostBytes;
        var port = BinaryPrimitives.ReadUInt16LittleEndian(page[position..]);
        var protocol = (VpnProtocol)page[position + 2];
        var transportByte = page[position + 3];
        var uriBytes = BinaryPrimitives.ReadInt32LittleEndian(page[(position + 4)..]);
        position += 8;
        if (port == 0 || !Enum.IsDefined(protocol) || transportByte > 1 || uriBytes < -1 ||
            uriBytes > MaxUriBytes || uriBytes > page.Length - position) throw InvalidSnapshot();
        var requiresUdp = protocol is VpnProtocol.WireGuard or VpnProtocol.Hysteria or VpnProtocol.Hysteria2 or VpnProtocol.Tuic;
        if (protocol != VpnProtocol.OpenVpn && (transportByte == 1) != requiresUdp) throw InvalidSnapshot();
        var uri = uriBytes < 0 ? null : ReadText(page.Slice(position, uriBytes));
        position += Math.Max(0, uriBytes);
        string? configuration = null;
        if (hasConfigurations)
        {
            if (position > page.Length - 4) throw InvalidSnapshot();
            var configurationBytes = BinaryPrimitives.ReadInt32LittleEndian(page[position..]);
            position += 4;
            if (configurationBytes < -1 || configurationBytes > ClashYamlFeedParser.MaximumConfigurationBytes ||
                configurationBytes > page.Length - position) throw InvalidSnapshot();
            if (configurationBytes >= 0) configuration = ReadText(page.Slice(position, configurationBytes));
            position += Math.Max(0, configurationBytes);
        }
        if (uri is null && configuration is null && protocol is not (VpnProtocol.OpenVpn or VpnProtocol.WireGuard)) throw InvalidSnapshot();
        var candidate = new VpnCandidate(host, port, protocol, transportByte == 1 ? "udp" : "tcp", uri)
        { ClashConfiguration = configuration };
        if (!VpnFeedParser.IsSafe(candidate) || host.Any(char.IsUpper) || uri?.Length > MaxUriCharacters)
            throw InvalidSnapshot();
        // Revalidate URI provenance independently of the serialized endpoint: a corrupt
        // cache cannot pair a public host with a private or different ready-to-import URI.
        if (uri is not null && VpnFeedParser.Parse(uri, protocol, 1) is { } parsed &&
            (parsed.Count != 1 || parsed[0] != candidate with { ClashConfiguration = null })) throw InvalidSnapshot();
        if (configuration is not null && !ClashYamlFeedParser.IsValidStandalone(candidate)) throw InvalidSnapshot();
        return candidate;
    }

    private static string ReadText(ReadOnlySpan<byte> value)
    {
        try { return StrictUtf8.GetString(value); }
        catch (DecoderFallbackException) { throw InvalidSnapshot(); }
    }

    private static InvalidDataException InvalidSnapshot() =>
        new("Сохранённый VPN-снимок повреждён или имеет неподдерживаемый формат.");
}

internal sealed record VpnCandidateSnapshot(byte[] Payload, int UniqueCount, int RecordCount, byte[] BodyHash);
internal readonly record struct VpnSnapshotWindow(int Count, int NextIndex, bool Completed);
