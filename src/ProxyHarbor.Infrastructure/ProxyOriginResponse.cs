using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Строго разбирает ограниченный HTTP-ответ контрольного origin-сервера.</summary>
internal static class ProxyOriginResponse
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxInformationalResponses = 8;
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
        var responseStart = 0;
        var headerEnd = -1;
        int? expectedMessageEnd = null;
        var chunked = false;
        var finalHeaderParsed = false;
        var informationalResponses = 0;

        while (true)
        {
            var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
            while (true)
            {
                if (finalHeaderParsed)
                {
                    if (expectedMessageEnd.HasValue && bytes.Length >= expectedMessageEnd.Value)
                        return Snapshot(bytes[responseStart..expectedMessageEnd.Value]);
                    if (chunked && TryGetCompleteChunkedLength(bytes[(headerEnd + 4)..], out var bodyBytes))
                        return Snapshot(bytes[responseStart..(headerEnd + 4 + bodyBytes)]);
                    break;
                }

                var relativeHeaderEnd = bytes[responseStart..].IndexOf("\r\n\r\n"u8);
                if (relativeHeaderEnd < 0)
                {
                    if (bytes.Length - responseStart >= MaxHeaderBytes)
                        throw new ProbeControlResponseException("HTTP-заголовок контрольного сервера слишком велик.");
                    break;
                }

                headerEnd = checked(responseStart + relativeHeaderEnd);
                if (relativeHeaderEnd > MaxHeaderBytes)
                    throw new ProbeControlResponseException("HTTP-заголовок контрольного сервера слишком велик.");
                var header = bytes[responseStart..headerEnd];
                var framing = ParseFraming(
                    header,
                    relativeHeaderEnd + 4,
                    maxBytes - responseStart);

                if (framing.StatusCode is >= 100 and < 200)
                {
                    if (framing.StatusCode == 101)
                        throw new ProbeControlResponseException("Контрольный endpoint неожиданно сменил HTTP-протокол.");
                    if (framing.HasPayloadFraming)
                        throw new ProbeControlResponseException("Информационный HTTP-ответ содержит framing тела.");
                    if (++informationalResponses > MaxInformationalResponses)
                        throw new ProbeControlResponseException("Контрольный endpoint вернул слишком много информационных ответов.");

                    // 1xx заканчивается сразу после headers. Уже прочитанные вслед за ним
                    // байты принадлежат следующему response и разбираются без нового read.
                    responseStart = headerEnd + 4;
                    headerEnd = -1;
                    if (responseStart == bytes.Length) break;
                    continue;
                }

                expectedMessageEnd = framing.ExpectedMessageBytes.HasValue
                    ? checked(responseStart + framing.ExpectedMessageBytes.Value)
                    : null;
                chunked = framing.Chunked;
                finalHeaderParsed = true;
            }

            if (output.Length == maxBytes)
            {
                // Для close-delimited ответа EOF отличает ровно maxBytes от фактического превышения.
                var sentinel = new byte[1];
                if (await stream.ReadAsync(sentinel, token) == 0 && finalHeaderParsed &&
                    !expectedMessageEnd.HasValue && !chunked)
                    return Snapshot(output.GetBuffer().AsSpan(responseStart, checked((int)output.Length) - responseStart));
                throw new ProbeControlResponseException($"HTTP-ответ контрольного сервера превышает {maxBytes} байт.");
            }
            var remaining = checked((int)(maxBytes - output.Length));
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token);
            if (read == 0)
            {
                if (!finalHeaderParsed)
                    throw new ProbeControlResponseException("Контрольный сервер не вернул финальный HTTP-ответ.");
                if (expectedMessageEnd.HasValue || chunked)
                    throw new ProbeControlResponseException("Контрольный сервер преждевременно завершил HTTP-ответ.");
                return Snapshot(output.GetBuffer().AsSpan(responseStart, checked((int)output.Length) - responseStart));
            }
            await output.WriteAsync(buffer.AsMemory(0, read), token);
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
        if (ParseStatusCode(headerLines[0]) != 200)
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

    private static (int StatusCode, int? ExpectedMessageBytes, bool Chunked, bool HasPayloadFraming) ParseFraming(
        ReadOnlySpan<byte> header,
        int bodyOffset,
        int maxBytes)
    {
        var lines = DecodeHttpHeader(header).Split("\r\n", StringSplitOptions.None);
        var statusCode = ParseStatusCode(lines[0]);
        int? contentLength = null;
        string? transferCoding = null;
        var hasPayloadFraming = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new ProbeControlResponseException("Некорректный HTTP-заголовок контрольного сервера.");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                hasPayloadFraming = true;
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0 ||
                    contentLength.HasValue && contentLength.Value != parsed)
                    throw new ProbeControlResponseException("Некорректный или конфликтующий Content-Length.");
                contentLength = parsed;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                hasPayloadFraming = true;
                if (transferCoding is not null)
                    throw new ProbeControlResponseException("Контрольный endpoint вернул несколько Transfer-Encoding.");
                transferCoding = value;
            }
        }

        if (transferCoding is not null &&
            !transferCoding.Equals("chunked", StringComparison.OrdinalIgnoreCase))
            throw new ProbeControlResponseException("Контрольный endpoint использует неподдерживаемый Transfer-Encoding.");
        var chunked = transferCoding is not null;
        if (chunked && contentLength.HasValue)
            throw new ProbeControlResponseException("HTTP-ответ одновременно содержит Content-Length и chunked encoding.");
        if (!contentLength.HasValue) return (statusCode, null, chunked, hasPayloadFraming);
        if (bodyOffset > maxBytes || contentLength.Value > maxBytes - bodyOffset)
            throw new ProbeControlResponseException($"HTTP-ответ контрольного сервера превышает {maxBytes} байт.");
        var total = bodyOffset + contentLength.Value;
        return (statusCode, total, false, hasPayloadFraming);
    }

    /// <summary>Принимает только точную HTTP/1.0 или HTTP/1.1 status-line с трёхзначным кодом.</summary>
    private static int ParseStatusCode(string statusLine)
    {
        var firstSpace = statusLine.IndexOf(' ');
        if (firstSpace < 0 || statusLine[..firstSpace] is not "HTTP/1.0" and not "HTTP/1.1")
            throw new ProbeControlResponseException("Некорректная status-line контрольного сервера.");
        var remainder = statusLine.AsSpan(firstSpace + 1);
        if (remainder.Length < 3 ||
            remainder[0] is < '0' or > '9' ||
            remainder[1] is < '0' or > '9' ||
            remainder[2] is < '0' or > '9' ||
            remainder.Length > 3 && remainder[3] != ' ')
            throw new ProbeControlResponseException("Некорректная status-line контрольного сервера.");
        return (remainder[0] - '0') * 100 + (remainder[1] - '0') * 10 + remainder[2] - '0';
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
