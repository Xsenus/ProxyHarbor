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
    private readonly Func<string, int, CancellationToken, Task<TcpClient>> _connectAsync =
        PublicProxyConnector.ConnectAsync;

    /// <summary>Позволяет integration-тесту заменить только транспорт, не ослабляя production-коннектор.</summary>
    internal ProxyProbeService(
        IOptions<CollectorOptions> options,
        OriginIpProvider originIpProvider,
        Func<string, int, CancellationToken, Task<TcpClient>> connectAsync)
        : this(options, originIpProvider)
    {
        _connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
    }

    /// <summary>Не позволяет арендовать пакет, пока доверенный control endpoint недоступен напрямую.</summary>
    public async Task EnsureControlEndpointAvailableAsync(CancellationToken cancellationToken) =>
        _ = await originIpProvider.GetRequiredAsync(cancellationToken);

    /// <summary>Выполняет HTTP CONNECT, SOCKS4a или SOCKS5 handshake и реальный HTTPS-запрос.</summary>
    public async Task<ProxyCheckResult> CheckAsync(ProxyEndpoint proxy, CancellationToken cancellationToken)
    {
        // Origin IP нужен только для признака анонимности и не расходует timeout самого proxy-туннеля.
        var originIp = await originIpProvider.GetRequiredAsync(cancellationToken);
        var timer = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProbeTimeoutSeconds));

            using var tcp = await _connectAsync(proxy.Host, proxy.Port, timeout.Token);
            var stream = tcp.GetStream();

            switch (proxy.Protocol)
            {
                case ProxyProtocol.Http:
                case ProxyProtocol.Https:
                    await ProxyTunnelProtocol.EstablishHttpConnectAsync(
                        stream, options.Value.ProbeHost, options.Value.ProbePort, timeout.Token);
                    break;
                case ProxyProtocol.Socks4:
                    await ProxyTunnelProtocol.EstablishSocks4aAsync(
                        stream, options.Value.ProbeHost, options.Value.ProbePort, timeout.Token);
                    break;
                case ProxyProtocol.Socks5:
                    await ProxyTunnelProtocol.EstablishSocks5Async(
                        stream, options.Value.ProbeHost, options.Value.ProbePort, timeout.Token);
                    break;
            }

            using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.Value.ProbeHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, timeout.Token);
            var probeHost = options.Value.ProbeHost.Contains(':') ? $"[{options.Value.ProbeHost}]" : options.Value.ProbeHost;
            var probeAuthority = options.Value.ProbePort == 443 ? probeHost : $"{probeHost}:{options.Value.ProbePort}";
            var request = $"GET {options.Value.ProbePath} HTTP/1.1\r\nHost: {probeAuthority}\r\nUser-Agent: ProxyHarbor/1.0\r\nConnection: close\r\n\r\n";
            await tls.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            await tls.FlushAsync(timeout.Token);

            // Framing-reader завершает probe сразу после полного Content-Length/chunked body:
            // корректный keep-alive control server не заставляет ждать закрытия TLS до timeout.
            var response = await ProxyOriginResponse.ReadAsync(tls, 64 * 1024, timeout.Token);
            var exitIp = ProxyOriginResponse.ParseExitIp(response);

            timer.Stop();
            return new ProxyCheckResult(proxy.Id, true, checked((int)timer.ElapsedMilliseconds), exitIp,
                !string.Equals(originIp, exitIp, StringComparison.OrdinalIgnoreCase), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProbeControlResponseException exception)
        {
            // Валидный TLS-туннель вернул непригодный control-ответ. Это не является
            // доказательством неисправности прокси и не должно ухудшать его статистику.
            return new ProxyCheckResult(proxy.Id, false, null, null, false,
                exception.Message[..Math.Min(500, exception.Message.Length)], IsDeferred: true);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or JsonException or OperationCanceledException)
        {
            return new ProxyCheckResult(proxy.Id, false, null, null, false,
                ex is OperationCanceledException ? "timeout" : ex.Message[..Math.Min(500, ex.Message.Length)]);
        }
    }

}

/// <summary>Кэширует внешний IP самого сервиса для корректного определения анонимности прокси.</summary>
public sealed class OriginIpProvider(
    IHttpClientFactory clients,
    IOptions<CollectorOptions> options,
    ProbeControlHealth health) : IDisposable
{
    private const int MaxDirectResponseBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Immutable reference публикует value+expiry одним атомарным snapshot. Отдельные
    // поля оставляли бы 16-байтовый DateTimeOffset под torn read у сотен probe-задач.
    private CacheEntry _cache = CacheEntry.Empty;

    public async Task<string> GetRequiredAsync(CancellationToken token)
    {
        var snapshot = Volatile.Read(ref _cache);
        if (snapshot.ExpiresAt > DateTimeOffset.UtcNow)
            return snapshot.Value ?? throw new ProbeControlUnavailableException();
        await _gate.WaitAsync(token);
        try
        {
            snapshot = Volatile.Read(ref _cache);
            if (snapshot.ExpiresAt > DateTimeOffset.UtcNow)
                return snapshot.Value ?? throw new ProbeControlUnavailableException();
            try
            {
                var settings = options.Value;
                var queryIndex = settings.ProbePath.IndexOf('?');
                var path = queryIndex < 0 ? settings.ProbePath : settings.ProbePath[..queryIndex];
                var query = queryIndex < 0 ? string.Empty : settings.ProbePath[(queryIndex + 1)..];
                var builder = new UriBuilder(Uri.UriSchemeHttps, settings.ProbeHost, settings.ProbePort, path)
                {
                    Query = query
                };
                var client = clients.CreateClient("origin");
                using var response = await client.GetAsync(builder.Uri, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                var jsonUtf8 = await ReadDirectResponseAsync(response.Content, token);
                // Строки JSON декодируются лениво, поэтому валидируем весь body до DOM.
                // Повреждённая кодировка не превращается в допустимый U+FFFD.
                _ = StrictUtf8.GetCharCount(jsonUtf8.Span);
                using var document = JsonDocument.Parse(jsonUtf8);
                var value = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("ip", out var ipElement) &&
                    ipElement.ValueKind == JsonValueKind.String
                    ? ipElement.GetString()
                    : null;
                var publicValue = IPAddress.TryParse(value, out var address) && NetworkSafety.IsPublicAddress(address)
                    ? address.ToString()
                    : null;
                snapshot = new CacheEntry(
                    publicValue,
                    DateTimeOffset.UtcNow.AddSeconds(publicValue is null ? 15 : 60));
                Volatile.Write(ref _cache, snapshot);
                health.Record(publicValue is not null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Остановка worker/API не является отказом внешнего endpoint и не должна
                // отравлять короткий отрицательный cache для следующего запуска.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                JsonException or DecoderFallbackException or TaskCanceledException)
            {
                snapshot = new CacheEntry(null, DateTimeOffset.UtcNow.AddSeconds(15));
                Volatile.Write(ref _cache, snapshot);
                health.Record(available: false);
            }
            return snapshot.Value ?? throw new ProbeControlUnavailableException();
        }
        finally { _gate.Release(); }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadDirectResponseAsync(
        HttpContent content,
        CancellationToken token)
    {
        if (content.Headers.ContentLength is > MaxDirectResponseBytes)
            throw new InvalidDataException("Прямой ответ контрольного endpoint превышает 16 КБ.");
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(Math.Min(MaxDirectResponseBytes, 4 * 1024));
        var buffer = new byte[4 * 1024];
        while (true)
        {
            var remaining = checked(MaxDirectResponseBytes - (int)output.Length);
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), token);
            if (read == 0)
                return output.GetBuffer().AsMemory(0, checked((int)output.Length));
            if (output.Length + read > MaxDirectResponseBytes)
                throw new InvalidDataException("Прямой ответ контрольного endpoint превышает 16 КБ.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record CacheEntry(string? Value, DateTimeOffset ExpiresAt)
    {
        internal static CacheEntry Empty { get; } = new(null, DateTimeOffset.MinValue);
    }
}

/// <summary>Control endpoint недоступен напрямую, поэтому пакет нельзя объективно проверять.</summary>
internal sealed class ProbeControlUnavailableException()
    : IOException("Контрольный endpoint проверки временно недоступен.");

/// <summary>Потокобезопасный снимок последней прямой health-проверки control endpoint.</summary>
public sealed class ProbeControlHealth
{
    private int _availability = -1;
    private long _checkedAtUnixSeconds;

    /// <summary>-1 до первой проверки, 0 при сбое, 1 при успешном ответе.</summary>
    public int Availability => Volatile.Read(ref _availability);
    public long CheckedAtUnixSeconds => Interlocked.Read(ref _checkedAtUnixSeconds);

    internal void Record(bool available)
    {
        Interlocked.Exchange(ref _checkedAtUnixSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Volatile.Write(ref _availability, available ? 1 : 0);
    }
}
