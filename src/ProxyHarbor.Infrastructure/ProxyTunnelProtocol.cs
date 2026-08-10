using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProxyHarbor.Infrastructure;

/// <summary>Строго формирует и проверяет handshakes поддерживаемых proxy-протоколов.</summary>
internal static class ProxyTunnelProtocol
{
    internal static async Task EstablishHttpConnectAsync(
        Stream stream, string targetHost, int targetPort, CancellationToken token)
    {
        ValidateTarget(targetHost, targetPort);
        var authorityHost = IPAddress.TryParse(targetHost, out var address) &&
            address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : targetHost;
        var authority = $"{authorityHost}:{targetPort}";
        var request = $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Connection: keep-alive\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
        var response = await ReadHeadersAsync(stream, token);
        var lineEnd = response.IndexOf("\r\n", StringComparison.Ordinal);
        var statusLine = lineEnd >= 0 ? response[..lineEnd] : response;
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 ||
            parts[0] is not "HTTP/1.0" and not "HTTP/1.1" ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode) ||
            statusCode is < 200 or >= 300)
            throw new IOException("HTTP CONNECT отклонён прокси.");
    }

    internal static async Task EstablishSocks4aAsync(
        Stream stream, string targetHost, int targetPort, CancellationToken token)
    {
        ValidateTarget(targetHost, targetPort);
        var host = EncodeHost(targetHost);
        var request = new byte[10 + host.Length];
        request[0] = 4;
        request[1] = 1;
        request[2] = (byte)(targetPort >> 8);
        request[3] = (byte)targetPort;
        request[7] = 1; // SOCKS4a: 0.0.0.1, затем пустой user id и DNS-имя.
        host.CopyTo(request, 9);
        await stream.WriteAsync(request, token);
        var response = new byte[8];
        await ReadExactlyAsync(stream, response, token);
        if (response[0] != 0 || response[1] != 90)
            throw new IOException($"SOCKS4 отклонил соединение ({response[1]}).");
    }

    internal static async Task EstablishSocks5Async(
        Stream stream, string targetHost, int targetPort, CancellationToken token)
    {
        ValidateTarget(targetHost, targetPort);
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        var greeting = new byte[2];
        await ReadExactlyAsync(stream, greeting, token);
        if (greeting[0] != 5 || greeting[1] != 0)
            throw new IOException("SOCKS5 требует неподдерживаемую авторизацию.");

        var host = EncodeHost(targetHost);
        if (host.Length > byte.MaxValue) throw new IOException("DNS-имя назначения слишком длинное для SOCKS5.");
        var request = new byte[7 + host.Length];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = 3;
        request[4] = (byte)host.Length;
        host.CopyTo(request, 5);
        request[^2] = (byte)(targetPort >> 8);
        request[^1] = (byte)targetPort;
        await stream.WriteAsync(request, token);

        var header = new byte[4];
        await ReadExactlyAsync(stream, header, token);
        if (header[0] != 5 || header[2] != 0)
            throw new IOException("Некорректный SOCKS5-ответ.");
        if (header[1] != 0) throw new IOException($"SOCKS5 отклонил соединение ({header[1]}).");
        var addressLength = header[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadByteAsync(stream, token),
            _ => throw new IOException("Некорректный тип адреса в SOCKS5-ответе.")
        };
        if (addressLength == 0)
            throw new IOException("SOCKS5 вернул пустое DNS-имя bind endpoint.");
        await ReadExactlyAsync(stream, new byte[addressLength + 2], token);
    }

    private static byte[] EncodeHost(string host)
    {
        return Encoding.ASCII.GetBytes(host);
    }

    private static void ValidateTarget(string host, int port)
    {
        if (port is < 1 or > 65_535 || string.IsNullOrWhiteSpace(host) ||
            host.Any(character => char.IsControl(character) || character > '\x7f') ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new IOException("Некорректное назначение proxy-туннеля.");
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        const int maxHeaderBytes = 16 * 1024;
        using var output = new MemoryStream(512);
        var readBuffer = new byte[1024];
        while (output.Length < maxHeaderBytes)
        {
            var remaining = checked(maxHeaderBytes - (int)output.Length);
            var read = await stream.ReadAsync(readBuffer.AsMemory(0, Math.Min(readBuffer.Length, remaining)), token);
            if (read == 0) throw new IOException("Прокси преждевременно закрыл соединение.");
            await output.WriteAsync(readBuffer.AsMemory(0, read), token);

            var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
            var separator = bytes.IndexOf("\r\n\r\n"u8);
            if (separator < 0) continue;
            if (separator + 4 != bytes.Length)
                throw new IOException("Прокси прислал неожиданные байты после CONNECT-заголовка.");
            ValidateHttpHeaderBytes(bytes);
            return Encoding.ASCII.GetString(bytes);
        }
        throw new IOException("Заголовок ответа прокси слишком велик.");
    }

    /// <summary>Отклоняет non-ASCII, DEL, NUL и bare CR/LF до разбора status line.</summary>
    private static void ValidateHttpHeaderBytes(ReadOnlySpan<byte> value)
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
                throw new IOException("CONNECT-заголовок прокси содержит недопустимые байты.");
        }
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[1];
        await ReadExactlyAsync(stream, buffer, token);
        return buffer[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), token);
            if (count == 0) throw new IOException("Прокси преждевременно закрыл соединение.");
            read += count;
        }
    }
}
