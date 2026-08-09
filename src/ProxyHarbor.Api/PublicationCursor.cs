using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Api;

/// <summary>Позиция в полном публичном порядке latency → success desc → UUID.</summary>
internal readonly record struct PublicationPosition(int LatencyMs, int SuccessfulChecks, Guid Id);

/// <summary>
/// Кодирует fixed-size opaque cursor без server-side state. Cursor не является
/// credential, но fingerprint запрещает случайно продолжать страницу с другими фильтрами.
/// </summary>
internal static class PublicationCursor
{
    private const byte Version = 1;
    private const int PayloadBytes = 1 + sizeof(ulong) + sizeof(int) + sizeof(int) + 16;
    internal const int EncodedLength = 44;

    /// <summary>Создаёт стабильный fingerprint только из параметров, меняющих множество строк.</summary>
    internal static ulong FilterFingerprint(
        ProxyProtocol? protocol,
        int? maxLatencyMs,
        decimal? minSuccessRate)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"v1|p={protocol?.ToString() ?? "*"}|l={maxLatencyMs?.ToString(CultureInfo.InvariantCulture) ?? "*"}|s={minSuccessRate?.ToString("G29", CultureInfo.InvariantCulture) ?? "*"}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        try { return BinaryPrimitives.ReadUInt64BigEndian(hash); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    /// <summary>Возвращает канонический base64url cursor фиксированной длины.</summary>
    internal static string Encode(PublicationPosition position, ulong filterFingerprint)
    {
        if (position.LatencyMs < 0 || position.SuccessfulChecks < 0 || position.Id == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(position), "Позиция публикации содержит недопустимые значения.");

        Span<byte> payload = stackalloc byte[PayloadBytes];
        payload[0] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(payload[1..], filterFingerprint);
        BinaryPrimitives.WriteInt32BigEndian(payload[9..], position.LatencyMs);
        BinaryPrimitives.WriteInt32BigEndian(payload[13..], position.SuccessfulChecks);
        if (!position.Id.TryWriteBytes(payload[17..], bigEndian: true, out var written) || written != 16)
            throw new InvalidOperationException("UUID cursor не удалось сериализовать.");
        return Convert.ToBase64String(payload).Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Fail-closed разбирает только текущую версию и ожидаемый набор фильтров.</summary>
    internal static bool TryDecode(
        string? encoded,
        ulong expectedFilterFingerprint,
        out PublicationPosition position)
    {
        position = default;
        if (string.IsNullOrEmpty(encoded) || encoded.Length != EncodedLength) return false;
        try
        {
            var payload = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/'));
            if (payload.Length != PayloadBytes || payload[0] != Version ||
                BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(1)) != expectedFilterFingerprint)
                return false;

            var latency = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(9));
            var successfulChecks = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(13));
            var id = new Guid(payload.AsSpan(17, 16), bigEndian: true);
            var decoded = new PublicationPosition(latency, successfulChecks, id);
            if (latency < 0 || successfulChecks < 0 || id == Guid.Empty ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Encode(decoded, expectedFilterFingerprint)),
                    Encoding.ASCII.GetBytes(encoded)))
                return false;
            position = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
