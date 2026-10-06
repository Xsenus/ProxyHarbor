using System.Buffers.Binary;
using System.IO.Compression;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Неизменяемый снимок разобранных кандидатов с отдельно сжатыми bounded-страницами.</summary>
internal static class ProxyCandidateSnapshotCodec
{
    internal const int PageSize = 2_048;
    internal const int MaxCandidates = 1_000_000;
    internal const int MaxPayloadBytes = 24_000_000;
    private const int RecordBytes = 20;
    internal const int Magic = 0x50485331;

    internal static ProxyCandidateSnapshot Encode(string content, ProxyProtocol fallback)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (System.Text.Encoding.UTF8.GetByteCount(content) > 10_000_000)
            throw new InvalidDataException("Body источника превышает безопасный размер снимка.");
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(0); // Count заполняется только после успешного полного разбора.
        var page = new byte[PageSize * RecordBytes];
        var used = 0;
        var parsed = SourceFeedParser.ParseBoundedToRequired(content, fallback, MaxCandidates, candidate =>
        {
            WriteRecord(page.AsSpan(used * RecordBytes, RecordBytes), candidate);
            used++;
            if (used != PageSize) return;
            WritePage(writer, page);
            used = 0;
        });
        if (parsed.Truncated)
            throw new InvalidDataException("Снимок источника превышает безопасное число кандидатов.");
        if (used > 0) WritePage(writer, page.AsSpan(0, used * RecordBytes));
        output.Position = sizeof(int);
        writer.Write(parsed.Count);
        return new ProxyCandidateSnapshot(output.ToArray(), parsed.Count, HashBody(content));
    }

    internal static byte[] HashBody(string content) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));

    internal static ProxySnapshotWindow ReadWindow(
        byte[] payload, int startIndex, int maxResults, Func<ProxyCandidateKey, bool> accept)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        if (payload.Length is < 8 or > MaxPayloadBytes || BinaryPrimitives.ReadInt32LittleEndian(payload) != Magic)
            throw InvalidSnapshot();
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
        if (count is < 1 or > MaxCandidates) throw InvalidSnapshot();
        ArgumentOutOfRangeException.ThrowIfLessThan(startIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, count);
        var cursor = 8;
        var pageIndex = 0;
        var next = startIndex;
        while (pageIndex * PageSize < count)
        {
            if (cursor > payload.Length - 4) throw InvalidSnapshot();
            var compressedBytes = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(cursor));
            cursor += 4;
            if (compressedBytes < 1 || compressedBytes > PageSize * RecordBytes + 1_024 ||
                compressedBytes > payload.Length - cursor) throw InvalidSnapshot();
            var pageStart = pageIndex * PageSize;
            var pageCount = Math.Min(PageSize, count - pageStart);
            if (next < pageStart + pageCount)
            {
                // Destination фиксирован размером страницы: decompression bomb не может
                // выделить память по размеру произвольного compressed input.
                var page = new byte[pageCount * RecordBytes];
                if (!BrotliDecoder.TryDecompress(payload.AsSpan(cursor, compressedBytes), page, out var written) ||
                    written != page.Length) throw InvalidSnapshot();
                while (next < pageStart + pageCount)
                {
                    if (next - startIndex == maxResults)
                        return new ProxySnapshotWindow(next - startIndex, next, false);
                    var candidate = ReadRecord(page.AsSpan((next - pageStart) * RecordBytes, RecordBytes));
                    if (!accept(candidate)) return new ProxySnapshotWindow(next - startIndex, next, false);
                    next++;
                }
            }
            cursor += compressedBytes;
            pageIndex++;
        }
        if (cursor != payload.Length) throw InvalidSnapshot();
        return new ProxySnapshotWindow(next - startIndex, next, true);
    }

    private static void WritePage(BinaryWriter writer, ReadOnlySpan<byte> page)
    {
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(page.Length)];
        if (!BrotliEncoder.TryCompress(page, compressed, out var written, quality: 1, window: 16))
            throw InvalidSnapshot();
        writer.Write(written);
        writer.Write(compressed.AsSpan(0, written));
        if (writer.BaseStream.Length > MaxPayloadBytes)
            throw new InvalidDataException("Снимок источника превышает безопасный размер хранилища.");
    }

    private static void WriteRecord(Span<byte> record, ProxyCandidateKey candidate)
    {
        BinaryPrimitives.WriteUInt64BigEndian(record, candidate.AddressHigh);
        BinaryPrimitives.WriteUInt64BigEndian(record[8..], candidate.AddressLow);
        BinaryPrimitives.WriteUInt16BigEndian(record[16..], candidate.PortValue);
        record[18] = candidate.ProtocolValue;
        record[19] = candidate.IsIpv6 ? (byte)1 : (byte)0;
    }

    private static ProxyCandidateKey ReadRecord(ReadOnlySpan<byte> record)
    {
        var candidate = new ProxyCandidateKey(
            BinaryPrimitives.ReadUInt64BigEndian(record),
            BinaryPrimitives.ReadUInt64BigEndian(record[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(record[16..]), record[18], record[19] == 1);
        if (record[19] > 1 || candidate.Port == 0 || !Enum.IsDefined(candidate.Protocol) ||
            (!candidate.IsIpv6 && (candidate.AddressHigh != 0 || candidate.AddressLow > uint.MaxValue)))
            throw InvalidSnapshot();
        Span<byte> addressBytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(addressBytes, candidate.AddressHigh);
        BinaryPrimitives.WriteUInt64BigEndian(addressBytes[8..], candidate.AddressLow);
        var address = new System.Net.IPAddress(candidate.IsIpv6 ? addressBytes : addressBytes[12..]);
        if (address.IsIPv4MappedToIPv6 || !NetworkSafety.IsPublicAddress(address))
            throw InvalidSnapshot();
        return candidate;
    }

    private static InvalidDataException InvalidSnapshot() =>
        new("Сохранённый снимок proxy-источника повреждён или имеет неподдерживаемый формат.");
}

internal sealed record ProxyCandidateSnapshot(byte[] Payload, int Count, byte[] BodyHash);
internal readonly record struct ProxySnapshotWindow(int Count, int NextIndex, bool Completed);
