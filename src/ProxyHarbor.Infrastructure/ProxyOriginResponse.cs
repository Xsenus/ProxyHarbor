using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Строго разбирает ограниченный HTTP-ответ контрольного origin-сервера.</summary>
internal static class ProxyOriginResponse
{
    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Читает ровно один bounded HTTP/1.1 response и не ждёт EOF после полного framed body.
    /// </summary>
    internal static async Task<ReadOnlyMemory<byte>> ReadAsync(
        Stream stream,
        int maxBytes,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        using var output = new MemoryStream(Math.Min(maxBytes, 8 * 1024));
        var buffer = new byte[Math.Min(4 * 1024, maxBytes)];
        var headerEnd = -1;
        int? expectedMessageBytes = null;
        var chunked = false;

        while (true)
        {
            var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
            if (headerEnd >= 0)
            {
                if (expectedMessageBytes.HasValue && bytes.Length >= expectedMessageBytes.Value)
                    return Snapshot(bytes[..expectedMessageBytes.Value]);
                if (chunked && TryGetCompleteChunkedLength(bytes[(headerEnd + 4)..], out var bodyBytes))
                    return Snapshot(bytes[..(headerEnd + 4 + bodyBytes)]);
            }

            if (output.Length == maxBytes)
            {
                // Для close-delimited ответа EOF отличает ровно maxBytes от фактического превышения.
                var sentinel = new byte[1];
                if (await stream.ReadAsync(sentinel, token) == 0)
                    return Snapshot(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
                throw new ProbeControlResponseException($"HTTP-ответ контрольного сервера превышает {maxBytes} байт.");
            }
            var remaining = checked((int)(maxBytes - output.Length));
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token);
            if (read == 0)
            {
                if (expectedMessageBytes.HasValue || chunked)
                    throw new ProbeControlResponseException("Контрольный сервер преждевременно завершил HTTP-ответ.");
                return Snapshot(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
            }
            await output.WriteAsync(buffer.AsMemory(0, read), token);

            if (headerEnd >= 0) continue;
            bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
            headerEnd = bytes.IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0)
            {
                if (bytes.Length >= Math.Min(maxBytes, MaxHeaderBytes))
                    throw new ProbeControlResponseException("HTTP-заголовок контрольного сервера слишком велик.");
                continue;
            }
            if (headerEnd > MaxHeaderBytes)
                throw new ProbeControlResponseException("HTTP-заголовок контрольного сервера слишком велик.");

            (expectedMessageBytes, chunked) = ParseFraming(bytes[..headerEnd], headerEnd + 4, maxBytes);
        }
    }

    /// <summary>Удобный overload для детерминированных текстовых unit-тестов.</summary>
    internal static string ParseExitIp(string response) =>
        ParseExitIp(StrictUtf8.GetBytes(response));

    /// <summary>Разбирает headers и chunk framing в bytes до строгой UTF-8 проверки JSON body.</summary>
    internal static string ParseExitIp(ReadOnlyMemory<byte> response)
    {
        var separator = response.Span.IndexOf("\r\n\r\n"u8);
        if (separator < 0) throw new ProbeControlResponseException("Некорректный HTTP-ответ контрольного сервера.");
        var headerLines = DecodeHttpHeader(response.Span[..separator]).Split("\r\n", StringSplitOptions.None);
        var statusParts = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2 || !statusParts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(statusParts[1], out var statusCode) || statusCode != 200)
            throw new ProbeControlResponseException("Контрольный сервер вернул неуспешный HTTP-код.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headerLines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new ProbeControlResponseException("Некорректный HTTP-заголовок контрольного сервера.");
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        ReadOnlyMemory<byte> body = response[(separator + 4)..];
        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Split(',').Any(value => value.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase)))
            body = DecodeChunked(body.Span);
        else if (headers.TryGetValue("Content-Length", out var contentLengthText))
        {
            if (!int.TryParse(contentLengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength) ||
                contentLength < 0 || body.Length < contentLength)
                throw new ProbeControlResponseException("Тело HTTP-ответа контрольного сервера оборвано.");
            body = body[..contentLength];
        }

        try
        {
            // JSON string tokens декодируются лениво; заранее проверяем весь body.
            _ = StrictUtf8.GetCharCount(body.Span);
            using var json = JsonDocument.Parse(body);
            var exitIp = json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("ip", out var ipElement) &&
                ipElement.ValueKind == JsonValueKind.String
                    ? ipElement.GetString()
                    : null;
            if (!IPAddress.TryParse(exitIp, out var exitAddress) || !NetworkSafety.IsPublicAddress(exitAddress))
                throw new ProbeControlResponseException("Контрольный сервер не вернул внешний IP.");
            return exitAddress.ToString();
        }
        catch (JsonException exception)
        {
            throw new ProbeControlResponseException("Контрольный сервер вернул некорректный JSON.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProbeControlResponseException(
                "Контрольный сервер вернул HTTP-ответ с некорректным UTF-8.",
                exception);
        }
    }

    private static ReadOnlyMemory<byte> Snapshot(ReadOnlySpan<byte> value) => value.ToArray();

    /// <summary>HTTP/1.x header обязан оставаться ASCII и не может содержать bare controls.</summary>
    private static string DecodeHttpHeader(ReadOnlySpan<byte> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var validCrLf = character switch
            {
                (byte)'\r' => index + 1 < value.Length && value[index + 1] == (byte)'\n',
                (byte)'\n' => index > 0 && value[index - 1] == (byte)'\r',
                _ => true
            };
            if (!validCrLf || character > 0x7E ||
                character < 0x20 && character is not (byte)'\t' and not (byte)'\r' and not (byte)'\n')
                throw new ProbeControlResponseException("HTTP-заголовок контрольного сервера содержит недопустимые байты.");
        }
        return Encoding.ASCII.GetString(value);
    }

    private static (int? ExpectedMessageBytes, bool Chunked) ParseFraming(
        ReadOnlySpan<byte> header,
        int bodyOffset,
        int maxBytes)
    {
        var lines = DecodeHttpHeader(header).Split("\r\n", StringSplitOptions.None);
        int? contentLength = null;
        var chunked = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new ProbeControlResponseException("Некорректный HTTP-заголовок контрольного сервера.");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0 ||
                    contentLength.HasValue && contentLength.Value != parsed)
                    throw new ProbeControlResponseException("Некорректный или конфликтующий Content-Length.");
                contentLength = parsed;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                value.Split(',').Any(item => item.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase)))
            {
                chunked = true;
            }
        }

        if (chunked && contentLength.HasValue)
            throw new ProbeControlResponseException("HTTP-ответ одновременно содержит Content-Length и chunked encoding.");
        if (!contentLength.HasValue) return (null, chunked);
        if (bodyOffset > maxBytes || contentLength.Value > maxBytes - bodyOffset)
            throw new ProbeControlResponseException($"HTTP-ответ контрольного сервера превышает {maxBytes} байт.");
        var total = bodyOffset + contentLength.Value;
        return (total, false);
    }

    private static bool TryGetCompleteChunkedLength(ReadOnlySpan<byte> body, out int completeLength)
    {
        completeLength = 0;
        var position = 0;
        while (true)
        {
            var relativeLineEnd = body[position..].IndexOf("\r\n"u8);
            if (relativeLineEnd < 0) return false;
            var lineEnd = position + relativeLineEnd;
            var sizeText = body[position..lineEnd];
            var extension = sizeText.IndexOf((byte)';');
            if (extension >= 0) sizeText = sizeText[..extension];
            sizeText = TrimAsciiWhitespace(sizeText);
            if (!TryParseHex(sizeText, out var size))
                throw new ProbeControlResponseException("Некорректный размер HTTP chunk.");
            position = lineEnd + 2;
            if (size == 0)
            {
                if (body.Length < position + 2) return false;
                if (body[position..].StartsWith("\r\n"u8))
                {
                    completeLength = position + 2;
                    return true;
                }
                var trailerEnd = body[position..].IndexOf("\r\n\r\n"u8);
                if (trailerEnd < 0) return false;
                completeLength = checked(position + trailerEnd + 4);
                return true;
            }
            if (size > body.Length - position - 2) return false;
            position = checked(position + size);
            if (!body[position..].StartsWith("\r\n"u8))
                throw new ProbeControlResponseException("HTTP chunk не завершён CRLF.");
            position += 2;
        }
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && value[0] is (byte)' ' or (byte)'\t') value = value[1..];
        while (!value.IsEmpty && value[^1] is (byte)' ' or (byte)'\t') value = value[..^1];
        return value;
    }

    private static bool TryParseHex(ReadOnlySpan<byte> value, out int result)
    {
        result = 0;
        if (value.IsEmpty) return false;
        foreach (var character in value)
        {
            var digit = character switch
            {
                >= (byte)'0' and <= (byte)'9' => character - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => character - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => character - (byte)'A' + 10,
                _ => -1
            };
            if (digit < 0 || result > (int.MaxValue - digit) / 16) return false;
            result = result * 16 + digit;
        }
        return true;
    }

    private static byte[] DecodeChunked(ReadOnlySpan<byte> encoded)
    {
        using var output = new MemoryStream(encoded.Length);
        var position = 0;
        while (true)
        {
            var relativeLineEnd = encoded[position..].IndexOf("\r\n"u8);
            if (relativeLineEnd < 0)
                throw new ProbeControlResponseException("Некорректное chunked-тело контрольного сервера.");
            var lineEnd = checked(position + relativeLineEnd);
            var sizeText = encoded[position..lineEnd];
            var extension = sizeText.IndexOf((byte)';');
            if (extension >= 0) sizeText = sizeText[..extension];
            if (!TryParseHex(TrimAsciiWhitespace(sizeText), out var size))
                throw new ProbeControlResponseException("Некорректный размер HTTP chunk.");
            position = lineEnd + 2;
            if (size == 0)
            {
                if (encoded[position..].StartsWith("\r\n"u8)) return output.ToArray();
                var trailerEnd = encoded[position..].IndexOf("\r\n\r\n"u8);
                if (trailerEnd < 0)
                    throw new ProbeControlResponseException("Chunked-тело контрольного сервера не завершено.");
                _ = DecodeHttpHeader(encoded.Slice(position, trailerEnd + 2));
                return output.ToArray();
            }
            if (size > encoded.Length - position - 2)
                throw new ProbeControlResponseException("Chunked-тело контрольного сервера оборвано.");
            output.Write(encoded.Slice(position, size));
            position = checked(position + size);
            if (!encoded[position..].StartsWith("\r\n"u8))
                throw new ProbeControlResponseException("Chunked-тело контрольного сервера оборвано.");
            position += 2;
        }
    }
}

/// <summary>Ответ доверенного control endpoint не позволяет оценивать качество проверяемого прокси.</summary>
internal sealed class ProbeControlResponseException(string message, Exception? innerException = null)
    : IOException(message, innerException);
