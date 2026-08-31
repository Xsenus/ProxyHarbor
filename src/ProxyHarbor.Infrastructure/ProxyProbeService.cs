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
    private readonly RemoteCertificateValidationCallback? _certificateValidationCallback;

    /// <summary>Позволяет integration-тесту заменить только транспорт, не ослабляя production-коннектор.</summary>
    internal ProxyProbeService(
        IOptions<CollectorOptions> options,
        OriginIpProvider originIpProvider,
        Func<string, int, CancellationToken, Task<TcpClient>> connectAsync,
        RemoteCertificateValidationCallback? certificateValidationCallback = null)
        : this(options, originIpProvider)
    {
        _connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
        _certificateValidationCallback = certificateValidationCallback;
    }

    /// <summary>Не позволяет арендовать пакет, пока доверенный control endpoint недоступен напрямую.</summary>
    public async Task EnsureControlEndpointAvailableAsync(CancellationToken cancellationToken) =>
        _ = await originIpProvider.GetRequiredAsync(cancellationToken);

    /// <summary>Выполняет HTTP CONNECT, SOCKS4a или SOCKS5 handshake и реальный HTTPS-запрос.</summary>
    public async Task<ProxyCheckResult> CheckAsync(ProxyEndpoint proxy, CancellationToken cancellationToken)
    {
        // Origin IP нужен только для признака анонимности и не расходует timeout самого proxy-туннеля.
        var control = await originIpProvider.GetRequiredTargetAsync(cancellationToken);
        var timer = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProbeTimeoutSeconds));

            if (proxy.Protocol == ProxyProtocol.Socks4 &&
                IPAddress.TryParse(control.Host, out var controlAddress) &&
                controlAddress.AddressFamily == AddressFamily.InterNetworkV6)
                throw new ProxyTargetUnsupportedException(
                    "SOCKS4/SOCKS4a не поддерживает IPv6 literal назначения.");

            using var tcp = await _connectAsync(proxy.Host, proxy.Port, timeout.Token);
            var stream = tcp.GetStream();

            switch (proxy.Protocol)
            {
                case ProxyProtocol.Http:
                case ProxyProtocol.Https:
                    await ProxyTunnelProtocol.EstablishHttpConnectAsync(
                        stream, control.Host, control.Port, timeout.Token);
                    break;
                case ProxyProtocol.Socks4:
                    await ProxyTunnelProtocol.EstablishSocks4aAsync(
                        stream, control.Host, control.Port, timeout.Token);
                    break;
                case ProxyProtocol.Socks5:
                    await ProxyTunnelProtocol.EstablishSocks5Async(
                        stream, control.Host, control.Port, timeout.Token);
                    break;
            }

            // Production-конструктор не задаёт callback и всегда использует системную
            // проверку цепочки/имени. Friend test assembly может доверять только своему
            // ephemeral сертификату для полного локального transport-canary.
            using var tls = _certificateValidationCallback is null
                ? new SslStream(stream, leaveInnerStreamOpen: false)
                : new SslStream(stream, leaveInnerStreamOpen: false, _certificateValidationCallback);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = control.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, timeout.Token);
            var probeHost = control.Host.Contains(':') ? $"[{control.Host}]" : control.Host;
            var probeAuthority = control.Port == 443 ? probeHost : $"{probeHost}:{control.Port}";
            var request = $"GET {control.Path} HTTP/1.1\r\nHost: {probeAuthority}\r\nUser-Agent: ProxyHarbor/1.0\r\nConnection: close\r\n\r\n";
            await tls.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            await tls.FlushAsync(timeout.Token);

            // Framing-reader завершает probe сразу после полного Content-Length/chunked body:
            // корректный keep-alive control server не заставляет ждать закрытия TLS до timeout.
            var response = await ProxyOriginResponse.ReadAsync(tls, 64 * 1024, timeout.Token);
            var exitIp = ProxyOriginResponse.ParseExitIp(response);

            timer.Stop();
            return new ProxyCheckResult(proxy.Id, true, checked((int)timer.ElapsedMilliseconds), exitIp,
                !string.Equals(control.OriginIp, exitIp, StringComparison.OrdinalIgnoreCase), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProbeControlResponseException exception)
        {
            // Валидный TLS-туннель вернул непригодный control-ответ. Это не является
            // доказательством неисправности прокси и не должно ухудшать его статистику.
            return new ProxyCheckResult(proxy.Id, false, null, null, false,
                exception.Message[..Math.Min(500, exception.Message.Length)], IsDeferred: true);
        }
        catch (ProxyTargetUnsupportedException exception)
        {
            // Ограничение wire-протокола/control-конфигурации ничего не говорит
            // о работоспособности самого прокси и не должно увеличивать failure streak.
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Immutable reference публикует value+expiry одним атомарным snapshot. Отдельные
    // поля оставляли бы 16-байтовый DateTimeOffset под torn read у сотен probe-задач.
    private CacheEntry _cache = CacheEntry.Empty;

    /// <summary>Возвращает свежий канонический origin IP или сигнализирует нейтральный Deferred.</summary>
    public async Task<string> GetRequiredAsync(CancellationToken token) =>
        (await GetRequiredTargetAsync(token)).OriginIp;

    /// <summary>
    /// Возвращает один согласованный снимок endpoint+origin IP. Поэтому прямая и
    /// проксированная пробы всегда обращаются к одному и тому же доступному сервису.
    /// </summary>
    public async Task<ResolvedProbeControl> GetRequiredTargetAsync(CancellationToken token)
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
            var settings = options.Value;
            foreach (var endpoint in ConfiguredEndpoints(settings))
            {
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProbeTimeoutSeconds, 2, 10)));
                    var client = clients.CreateClient("origin");
                    using var response = await client.GetAsync(
                        BuildUri(endpoint), HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                    response.EnsureSuccessStatusCode();
                    var body = await ReadDirectResponseAsync(response.Content, attempt.Token);
                    var publicValue = ProxyOriginResponse.ParsePublicIpBody(body);
                    var resolved = new ResolvedProbeControl(endpoint.Host, endpoint.Port, endpoint.Path, publicValue);
                    snapshot = new CacheEntry(resolved, DateTimeOffset.UtcNow.AddSeconds(60));
                    Volatile.Write(ref _cache, snapshot);
                    health.Record(available: true);
                    return resolved;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Остановка worker/API не является отказом endpoint и не должна
                    // отравлять отрицательный cache для следующего запуска.
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                    JsonException or DecoderFallbackException or TaskCanceledException)
                {
                    // Следующий независимый endpoint может оставаться доступным.
                }
            }
            snapshot = new CacheEntry(null, DateTimeOffset.UtcNow.AddSeconds(15));
            Volatile.Write(ref _cache, snapshot);
            health.Record(available: false);
            throw new ProbeControlUnavailableException();
        }
        finally { _gate.Release(); }
    }

    private static IEnumerable<ProbeControlEndpoint> ConfiguredEndpoints(CollectorOptions settings)
    {
        yield return new ProbeControlEndpoint(settings.ProbeHost, settings.ProbePort, settings.ProbePath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"{settings.ProbeHost}:{settings.ProbePort}{settings.ProbePath}"
        };
        foreach (var value in settings.ProbeFallbackUrls ?? [])
        {
            if (!CollectorOptions.TryParseProbeUrl(value, out var endpoint)) continue;
            if (seen.Add($"{endpoint.Host}:{endpoint.Port}{endpoint.Path}")) yield return endpoint;
        }
    }

    private static Uri BuildUri(ProbeControlEndpoint endpoint)
    {
        var queryIndex = endpoint.Path.IndexOf('?');
        var path = queryIndex < 0 ? endpoint.Path : endpoint.Path[..queryIndex];
        var query = queryIndex < 0 ? string.Empty : endpoint.Path[(queryIndex + 1)..];
        return new UriBuilder(Uri.UriSchemeHttps, endpoint.Host, endpoint.Port, path) { Query = query }.Uri;
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

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private sealed record CacheEntry(ResolvedProbeControl? Value, DateTimeOffset ExpiresAt)
    {
        internal static CacheEntry Empty { get; } = new(null, DateTimeOffset.MinValue);
    }
}

/// <summary>Доступный endpoint и IP прямого подключения, проверенные одним запросом.</summary>
public sealed record ResolvedProbeControl(string Host, int Port, string Path, string OriginIp);

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
    /// <summary>Unix-время последней завершённой прямой проверки либо ноль.</summary>
    public long CheckedAtUnixSeconds => Interlocked.Read(ref _checkedAtUnixSeconds);

    internal void Record(bool available)
    {
        Interlocked.Exchange(ref _checkedAtUnixSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Volatile.Write(ref _availability, available ? 1 : 0);
    }
}
