using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ProxyHarbor.Infrastructure;

/// <summary>Independently compressed API pages with bounded decoded lengths and a format version.</summary>
internal static class FreeProxyDbPageCaptureCodec
{
    private const int Magic = 0x50484131;
    internal const int MaximumPayloadBytes = FreeProxyDbPageCapture.MaximumCaptureBytes + 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Encode(FreeProxyDbPageCapture capture, int maximumBytes)
    {
        _ = capture.Inspect(maximumBytes);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, StrictUtf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(capture.Pages.Count);
        foreach (var page in capture.Pages)
        {
            var raw = StrictUtf8.GetBytes(page.Content);
            var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
            if (!BrotliEncoder.TryCompress(raw, compressed, out var written, quality: 4, window: 22))
                throw InvalidPayload();
            writer.Write(page.PageIndex);
            writer.Write(page.Reconciliation);
            writer.Write(page.CapturedAt.UtcTicks);
            writer.Write(raw.Length);
            writer.Write(written);
            writer.Write(compressed.AsSpan(0, written));
            if (output.Length > MaximumPayloadBytes) throw InvalidPayload();
        }
        return output.ToArray();
    }

    internal static FreeProxyDbPageCapture Decode(byte[] payload, byte[] hash, int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, FreeProxyDbPageCapture.MaximumCaptureBytes);
        if (payload.Length is < 8 or > MaximumPayloadBytes || hash.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash))
            throw InvalidPayload();
        using var input = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(input, StrictUtf8);
        if (reader.ReadInt32() != Magic) throw InvalidPayload();
        var count = reader.ReadInt32();
        if (count is < 0 or > FreeProxyDbPageCapture.MaximumPages * 2) throw InvalidPayload();
        var pages = new FreeProxyDbCapturedPage[count];
        long decodedBytes = 0;
        for (var index = 0; index < count; index++)
        {
            if (input.Length - input.Position < 21) throw InvalidPayload();
            var pageIndex = reader.ReadInt32();
            var phase = reader.ReadByte();
            var ticks = reader.ReadInt64();
            var rawLength = reader.ReadInt32();
            var compressedLength = reader.ReadInt32();
            decodedBytes += rawLength;
            if (phase > 1 || ticks < DateTimeOffset.UnixEpoch.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks ||
                rawLength is < 1 or > FreeProxyDbPageCapture.MaximumPageBytes || decodedBytes > maximumBytes ||
                compressedLength < 1 || compressedLength > BrotliEncoder.GetMaxCompressedLength(rawLength) ||
                compressedLength > input.Length - input.Position)
                throw InvalidPayload();
            var compressed = reader.ReadBytes(compressedLength);
            var raw = new byte[rawLength];
            if (!BrotliDecoder.TryDecompress(compressed, raw, out var written) || written != rawLength)
                throw InvalidPayload();
            string content;
            try { content = StrictUtf8.GetString(raw); }
            catch (DecoderFallbackException exception) { throw new InvalidDataException("API page UTF-8 corrupted.", exception); }
            pages[index] = new(pageIndex, phase == 1, new DateTimeOffset(ticks, TimeSpan.Zero), content);
        }
        if (input.Position != input.Length) throw InvalidPayload();
        return FreeProxyDbPageCapture.Restore(pages, maximumBytes);
    }

    private static InvalidDataException InvalidPayload() => new("API page checkpoint corrupted or oversized.");
}
