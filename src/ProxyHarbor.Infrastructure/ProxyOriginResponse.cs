using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Строго разбирает ограниченный HTTP-ответ контрольного origin-сервера.</summary>
internal static class ProxyOriginResponse
{
    internal static string ParseExitIp(string response)
    {
        var separator = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0) throw new ProbeControlResponseException("Некорректный HTTP-ответ контрольного сервера.");
        var headerLines = response[..separator].Split("\r\n", StringSplitOptions.None);
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

        var body = response[(separator + 4)..];
        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Split(',').Any(value => value.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase)))
            body = DecodeChunked(body);
        else if (headers.TryGetValue("Content-Length", out var contentLengthText))
        {
            if (!int.TryParse(contentLengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength) ||
                contentLength < 0 || Encoding.UTF8.GetByteCount(body) < contentLength)
                throw new ProbeControlResponseException("Тело HTTP-ответа контрольного сервера оборвано.");
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var exitIp = json.RootElement.TryGetProperty("ip", out var ipElement) ? ipElement.GetString() : null;
            if (!IPAddress.TryParse(exitIp, out var exitAddress) || !NetworkSafety.IsPublicAddress(exitAddress))
                throw new ProbeControlResponseException("Контрольный сервер не вернул внешний IP.");
            return exitIp;
        }
        catch (JsonException exception)
        {
            throw new ProbeControlResponseException("Контрольный сервер вернул некорректный JSON.", exception);
        }
    }

    private static string DecodeChunked(string encoded)
    {
        var output = new StringBuilder(encoded.Length);
        var position = 0;
        while (true)
        {
            var lineEnd = encoded.IndexOf("\r\n", position, StringComparison.Ordinal);
            if (lineEnd < 0) throw new ProbeControlResponseException("Некорректное chunked-тело контрольного сервера.");
            var sizeText = encoded[position..lineEnd].Split(';', 2)[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new ProbeControlResponseException("Некорректный размер HTTP chunk.");
            position = lineEnd + 2;
            if (size == 0) return output.ToString();
            if (position + size + 2 > encoded.Length ||
                !encoded.AsSpan(position + size, 2).SequenceEqual("\r\n"))
                throw new ProbeControlResponseException("Chunked-тело контрольного сервера оборвано.");
            output.Append(encoded, position, size);
            position += size + 2;
        }
    }
}

/// <summary>Ответ доверенного control endpoint не позволяет оценивать качество проверяемого прокси.</summary>
internal sealed class ProbeControlResponseException(string message, Exception? innerException = null)
    : IOException(message, innerException);
