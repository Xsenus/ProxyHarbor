using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет полный proxy probe transport от CONNECT до классификации анонимности.</summary>
public sealed class ProxyProbeServiceTests
{
    [Theory]
    [InlineData(ProxyProtocol.Http, "1.1.1.1", true)]
    [InlineData(ProxyProtocol.Http, "8.8.8.8", false)]
    [InlineData(ProxyProtocol.Https, "1.1.1.1", true)]
    [InlineData(ProxyProtocol.Socks4, "1.1.1.1", true)]
    [InlineData(ProxyProtocol.Socks5, "1.1.1.1", true)]
    public async Task SupportedTunnelTlsProbePublishesOnlyValidatedExitIp(
        ProxyProtocol protocol,
        string exitIp,
        bool expectedAnonymous)
    {
        using var originClients = new StubHttpClientFactory("{\"ip\":\"8.8.8.8\"}");
        var settings = Options.Create(new CollectorOptions
        {
            ProbeHost = "probe.example",
            ProbePort = 8_443,
            ProbePath = "/who?format=json",
            ProbeTimeoutSeconds = 5
        });
        using var origin = new OriginIpProvider(originClients, settings, new ProbeControlHealth());
        using var rsa = RSA.Create(2_048);
        using var certificate = CreateServerCertificate(rsa);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var releaseConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeProxyOnceAsync(
            listener, certificate, exitIp, releaseConnection.Task, protocol, timeout.Token);
        var expectedCertificateHash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var probe = new ProxyProbeService(
            settings,
            origin,
            (_, _, token) => ConnectLoopbackAsync(listenerPort, token),
            (_, remoteCertificate, _, _) =>
                remoteCertificate is not null &&
                string.Equals(
                    remoteCertificate.GetCertHashString(HashAlgorithmName.SHA256),
                    expectedCertificateHash,
                    StringComparison.Ordinal));

        ProxyCheckResult result;
        try
        {
            result = await probe.CheckAsync(Proxy(protocol), timeout.Token);
        }
        finally
        {
            // Сервер намеренно не закрывает keep-alive до завершения CheckAsync. Если
            // framing-reader ждёт EOF вместо Content-Length, probe уйдёт в timeout.
            releaseConnection.TrySetResult();
            await server;
        }

        Assert.True(result.IsAlive);
        Assert.False(result.IsDeferred);
        Assert.NotNull(result.LatencyMs);
        Assert.True(result.LatencyMs >= 0);
        Assert.Equal(exitIp, result.ExitIp);
        Assert.Equal(expectedAnonymous, result.IsAnonymous);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SystemTlsValidationRejectsUntrustedProxyCertificate()
    {
        using var originClients = new StubHttpClientFactory("{\"ip\":\"8.8.8.8\"}");
        var settings = Options.Create(new CollectorOptions
        {
            ProbeHost = "probe.example",
            ProbePort = 8_443,
            ProbePath = "/who",
            ProbeTimeoutSeconds = 5
        });
        using var origin = new OriginIpProvider(originClients, settings, new ProbeControlHealth());
        using var rsa = RSA.Create(2_048);
        using var certificate = CreateServerCertificate(rsa);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var releaseConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeProxyOnceAsync(
            listener, certificate, "1.1.1.1", releaseConnection.Task, ProxyProtocol.Http, timeout.Token);
        // Callback намеренно отсутствует: этот internal-конструктор проходит тем же
        // системным certificate validation path, что публичный production-конструктор.
        var probe = new ProxyProbeService(
            settings,
            origin,
            (_, _, token) => ConnectLoopbackAsync(listenerPort, token));

        ProxyCheckResult result;
        Exception? serverFailure = null;
        try
        {
            result = await probe.CheckAsync(Proxy(), timeout.Token);
        }
        finally
        {
            releaseConnection.TrySetResult();
            serverFailure = await Record.ExceptionAsync(() => server);
        }

        Assert.True(serverFailure is AuthenticationException or IOException, serverFailure?.ToString());
        Assert.False(result.IsAlive);
        Assert.False(result.IsDeferred);
        Assert.Null(result.ExitIp);
        Assert.False(result.IsAnonymous);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task SilentProxyIsClassifiedAsTimeoutWithinConfiguredBound()
    {
        using var originClients = new StubHttpClientFactory("{\"ip\":\"8.8.8.8\"}");
        var settings = Options.Create(new CollectorOptions
        {
            ProbeHost = "probe.example",
            ProbePort = 443,
            ProbePath = "/who",
            ProbeTimeoutSeconds = 1
        });
        using var origin = new OriginIpProvider(originClients, settings, new ProbeControlHealth());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = HoldConnectionOpenAsync(listener, accepted, serverStop.Token);
        var probe = new ProxyProbeService(
            settings,
            origin,
            (_, _, token) => ConnectLoopbackAsync(listenerPort, token));

        ProxyCheckResult result;
        try
        {
            result = await probe.CheckAsync(Proxy(), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await StopServerAsync(serverStop, server);
        }

        Assert.False(result.IsAlive);
        Assert.False(result.IsDeferred);
        Assert.Equal("timeout", result.Error);
        Assert.Null(result.LatencyMs);
    }

    [Fact]
    public async Task CallerCancellationEscapesInsteadOfBeingClassifiedAsTimeout()
    {
        using var originClients = new StubHttpClientFactory("{\"ip\":\"8.8.8.8\"}");
        var settings = Options.Create(new CollectorOptions
        {
            ProbeHost = "probe.example",
            ProbeTimeoutSeconds = 10
        });
        using var origin = new OriginIpProvider(originClients, settings, new ProbeControlHealth());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = HoldConnectionOpenAsync(listener, accepted, serverStop.Token);
        var probe = new ProxyProbeService(
            settings,
            origin,
            (_, _, token) => ConnectLoopbackAsync(listenerPort, token));
        using var callerCancellation = new CancellationTokenSource();

        try
        {
            var check = probe.CheckAsync(Proxy(), callerCancellation.Token);
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await callerCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        }
        finally
        {
            await StopServerAsync(serverStop, server);
        }
    }

    private static ProxyEndpoint Proxy(ProxyProtocol protocol = ProxyProtocol.Http) => new()
    {
        Host = "203.0.113.10",
        Port = 31_280,
        Protocol = protocol
    };

    private static async Task<TcpClient> ConnectLoopbackAsync(int port, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task HoldConnectionOpenAsync(
        TcpListener listener,
        TaskCompletionSource accepted,
        CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        accepted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }

    private static async Task StopServerAsync(CancellationTokenSource stopping, Task server)
    {
        await stopping.CancelAsync();
        try { await server; }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private static async Task ServeProxyOnceAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        string exitIp,
        Task releaseConnection,
        ProxyProtocol protocol,
        CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var transport = client.GetStream();
        await EstablishTunnelAsync(transport, protocol, token);

        await using var tls = new SslStream(transport, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false
        }, token);
        var request = await ReadHeadersAsync(tls, token);
        Assert.StartsWith("GET /who?format=json HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("\r\nHost: probe.example:8443\r\n", request, StringComparison.OrdinalIgnoreCase);

        var body = Encoding.ASCII.GetBytes($"{{\"ip\":\"{exitIp}\"}}");
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n\r\n");
        await tls.WriteAsync(headers, token);
        await tls.WriteAsync(body, token);
        await tls.FlushAsync(token);
        await releaseConnection.WaitAsync(token);
    }

    private static async Task EstablishTunnelAsync(
        Stream transport,
        ProxyProtocol protocol,
        CancellationToken token)
    {
        switch (protocol)
        {
            case ProxyProtocol.Http:
            case ProxyProtocol.Https:
                var connectRequest = await ReadHeadersAsync(transport, token);
                Assert.StartsWith(
                    "CONNECT probe.example:8443 HTTP/1.1\r\n",
                    connectRequest,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "\r\nHost: probe.example:8443\r\n",
                    connectRequest,
                    StringComparison.OrdinalIgnoreCase);
                await transport.WriteAsync(
                    "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),
                    token);
                break;
            case ProxyProtocol.Socks4:
                await AcceptSocks4aAsync(transport, token);
                break;
            case ProxyProtocol.Socks5:
                await AcceptSocks5Async(transport, token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(protocol));
        }
        await transport.FlushAsync(token);
    }

    private static async Task AcceptSocks4aAsync(Stream transport, CancellationToken token)
    {
        var request = new byte[9];
        await transport.ReadExactlyAsync(request, token);
        Assert.Equal(new byte[] { 4, 1, 0x20, 0xFB, 0, 0, 0, 1, 0 }, request);
        Assert.Equal("probe.example", await ReadNullTerminatedAsciiAsync(transport, token));
        await transport.WriteAsync(new byte[] { 0, 90, 0, 0, 0, 0, 0, 0 }, token);
    }

    private static async Task AcceptSocks5Async(Stream transport, CancellationToken token)
    {
        var greeting = new byte[3];
        await transport.ReadExactlyAsync(greeting, token);
        Assert.Equal(new byte[] { 5, 1, 0 }, greeting);
        await transport.WriteAsync(new byte[] { 5, 0 }, token);
        await transport.FlushAsync(token);

        var header = new byte[5];
        await transport.ReadExactlyAsync(header, token);
        Assert.Equal(new byte[] { 5, 1, 0, 3, 13 }, header);
        var addressAndPort = new byte[15];
        await transport.ReadExactlyAsync(addressAndPort, token);
        Assert.Equal("probe.example", Encoding.ASCII.GetString(addressAndPort, 0, 13));
        Assert.Equal(new byte[] { 0x20, 0xFB }, addressAndPort[^2..]);
        await transport.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
    }

    private static async Task<string> ReadNullTerminatedAsciiAsync(
        Stream stream,
        CancellationToken token)
    {
        using var output = new MemoryStream();
        var singleByte = new byte[1];
        while (output.Length <= byte.MaxValue)
        {
            await stream.ReadExactlyAsync(singleByte, token);
            if (singleByte[0] == 0)
                return Encoding.ASCII.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            output.WriteByte(singleByte[0]);
        }
        throw new InvalidDataException("SOCKS4a test host превысил 255 байт.");
    }

    /// <summary>Читает небольшой handshake header с жёсткой верхней границей.</summary>
    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        var singleByte = new byte[1];
        while (output.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(singleByte, token);
            if (read == 0) throw new EndOfStreamException("Соединение закрылось до конца заголовков.");
            output.WriteByte(singleByte[0]);
            if (output.Length >= 4)
            {
                var buffer = output.GetBuffer();
                var end = checked((int)output.Length);
                if (buffer.AsSpan(end - 4, 4).SequenceEqual("\r\n\r\n"u8))
                    return Encoding.ASCII.GetString(buffer, 0, end);
            }
        }
        throw new InvalidDataException("Заголовки test proxy превысили 16 КБ.");
    }

    private static X509Certificate2 CreateServerCertificate(RSA rsa)
    {
        var request = new CertificateRequest(
            "CN=probe.example",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("probe.example");
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        const string password = "proxyharbor-test-certificate";
        var pfx = ephemeral.Export(X509ContentType.Pfx, password);
        try
        {
            // Windows SChannel не может выступать TLS server с ephemeral private key.
            // Reload создаёт временный переносимый key container; Dispose сертификата
            // удаляет его, а сериализованный PFX очищается немедленно.
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private sealed class StubHttpClientFactory(string json) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new StubHandler(json));
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
