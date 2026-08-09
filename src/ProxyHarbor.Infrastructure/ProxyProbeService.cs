using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Проверяет прокси на уровне протокола и измеряет полную задержку до HTTPS-ответа.</summary>
public sealed class ProxyProbeService(IOptions<CollectorOptions> options, OriginIpProvider originIpProvider)
{
    /// <summary>Выполняет HTTP CONNECT, SOCKS4a или SOCKS5 handshake и реальный HTTPS-запрос.</summary>
    public async Task<ProxyCheckResult> CheckAsync(ProxyEndpoint proxy, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProbeTimeoutSeconds));

            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(proxy.Host, proxy.Port, timeout.Token);
            var stream = tcp.GetStream();

            switch (proxy.Protocol)
            {
                case ProxyProtocol.Http:
                case ProxyProtocol.Https:
                    await EstablishHttpTunnelAsync(stream, timeout.Token);
                    break;
                case ProxyProtocol.Socks4:
                    await EstablishSocks4TunnelAsync(stream, timeout.Token);
                    break;
                case ProxyProtocol.Socks5:
                    await EstablishSocks5TunnelAsync(stream, timeout.Token);
                    break;
            }

            using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.Value.ProbeHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, timeout.Token);
            var request = $"GET {options.Value.ProbePath} HTTP/1.1\r\nHost: {options.Value.ProbeHost}\r\nUser-Agent: ProxyHarbor/1.0\r\nConnection: close\r\n\r\n";
            await tls.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            await tls.FlushAsync(timeout.Token);

            var response = await ReadLimitedAsync(tls, 64 * 1024, timeout.Token);
            if (!response.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
                !response.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Контрольный сервер вернул неуспешный HTTP-код.");

            var separator = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator < 0) throw new IOException("Некорректный HTTP-ответ контрольного сервера.");
            using var json = JsonDocument.Parse(response[(separator + 4)..]);
            var exitIp = json.RootElement.TryGetProperty("ip", out var ipElement) ? ipElement.GetString() : null;
            if (!IPAddress.TryParse(exitIp, out var exitAddress) || !NetworkSafety.IsPublicAddress(exitAddress))
                throw new IOException("Контрольный сервер не вернул внешний IP.");

            timer.Stop();
            var originIp = await originIpProvider.GetAsync(timeout.Token);
            return new ProxyCheckResult(proxy.Id, true, checked((int)timer.ElapsedMilliseconds), exitIp,
                originIp is not null && !string.Equals(originIp, exitIp, StringComparison.OrdinalIgnoreCase), null);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or OperationCanceledException)
        {
            return new ProxyCheckResult(proxy.Id, false, null, null, false,
                ex is OperationCanceledException ? "timeout" : ex.Message[..Math.Min(500, ex.Message.Length)]);
        }
    }

    private async Task EstablishHttpTunnelAsync(Stream stream, CancellationToken token)
    {
        var request = $"CONNECT {options.Value.ProbeHost}:{options.Value.ProbePort} HTTP/1.1\r\nHost: {options.Value.ProbeHost}:{options.Value.ProbePort}\r\nProxy-Connection: keep-alive\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
        var response = await ReadHeadersAsync(stream, token);
        if (!response.Contains(" 200 ", StringComparison.Ordinal))
            throw new IOException("HTTP CONNECT отклонён прокси.");
    }

    private async Task EstablishSocks4TunnelAsync(Stream stream, CancellationToken token)
    {
        var host = Encoding.ASCII.GetBytes(options.Value.ProbeHost);
        var port = options.Value.ProbePort;
        var request = new byte[10 + host.Length];
        request[0] = 4; request[1] = 1;
        request[2] = (byte)(port >> 8); request[3] = (byte)port;
        request[7] = 1; // SOCKS4a: доменное имя следует после пустого user id.
        host.CopyTo(request, 9);
        await stream.WriteAsync(request, token);
        var response = new byte[8];
        await ReadExactlyAsync(stream, response, token);
        if (response[1] != 90) throw new IOException($"SOCKS4 отклонил соединение ({response[1]}).");
    }

    private async Task EstablishSocks5TunnelAsync(Stream stream, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        var greeting = new byte[2];
        await ReadExactlyAsync(stream, greeting, token);
        if (greeting[0] != 5 || greeting[1] != 0) throw new IOException("SOCKS5 требует неподдерживаемую авторизацию.");

        var host = Encoding.ASCII.GetBytes(options.Value.ProbeHost);
        var port = options.Value.ProbePort;
        var request = new byte[7 + host.Length];
        request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3; request[4] = (byte)host.Length;
        host.CopyTo(request, 5);
        request[^2] = (byte)(port >> 8); request[^1] = (byte)port;
        await stream.WriteAsync(request, token);

        var header = new byte[4];
        await ReadExactlyAsync(stream, header, token);
        if (header[1] != 0) throw new IOException($"SOCKS5 отклонил соединение ({header[1]}).");
        var addressLength = header[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadByteAsync(stream, token),
            _ => throw new IOException("Некорректный SOCKS5-ответ.")
        };
        await ReadExactlyAsync(stream, new byte[addressLength + 2], token);
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>(512);
        while (buffer.Count < 16 * 1024)
        {
            var value = await ReadByteAsync(stream, token);
            buffer.Add((byte)value);
            if (buffer.Count >= 4 && buffer[^4] == 13 && buffer[^3] == 10 && buffer[^2] == 13 && buffer[^1] == 10)
                return Encoding.ASCII.GetString(buffer.ToArray());
        }
        throw new IOException("Заголовок ответа прокси слишком велик.");
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

    private static async Task<string> ReadLimitedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (memory.Length < limit)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            await memory.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}

/// <summary>Кэширует внешний IP самого сервиса для корректного определения анонимности прокси.</summary>
public sealed class OriginIpProvider(IHttpClientFactory clients) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _value;
    private DateTimeOffset _expiresAt;

    public async Task<string?> GetAsync(CancellationToken token)
    {
        if (_expiresAt > DateTimeOffset.UtcNow) return _value;
        await _gate.WaitAsync(token);
        try
        {
            if (_expiresAt > DateTimeOffset.UtcNow) return _value;
            try
            {
                var json = await clients.CreateClient("origin").GetStringAsync("https://api.ipify.org/?format=json", token);
                using var document = JsonDocument.Parse(json);
                var value = document.RootElement.GetProperty("ip").GetString();
                _value = IPAddress.TryParse(value, out var address) && NetworkSafety.IsPublicAddress(address) ? value : null;
                _expiresAt = DateTimeOffset.UtcNow.AddMinutes(_value is null ? 1 : 10);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                _value = null;
                _expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
            }
            return _value;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
