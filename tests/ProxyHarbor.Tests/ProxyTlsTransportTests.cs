using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Exercises real nested TLS and the separate proxy-hop certificate policy.</summary>
public sealed class ProxyTlsTransportTests
{
    [Theory]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.Tls12 | SslProtocols.Tls13)]
    public async Task ExplicitEncryptionOnlyPolicySupportsConnectOverTls(SslProtocols protocol)
    {
        using var certificate = Certificate("proxy.example");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeConnectAsync(listener, certificate, protocol, timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port(listener), timeout.Token);
        var transport = client.GetStream();
        await using (var proxyTls = await ProxyTlsTransport.AuthenticateAsync(
            transport, "proxy.example", ProxyTlsCertificatePolicy.EncryptionOnly, timeout.Token))
        {
            Assert.True(proxyTls.IsEncrypted);
            await ProxyTunnelProtocol.EstablishHttpConnectAsync(proxyTls, "destination.example", 443, timeout.Token);
        }
        Assert.True(transport.CanWrite); // The caller retains ownership of its TCP stream.
        Assert.Equal("CONNECT destination.example:443 HTTP/1.1", await server);
    }

    [Fact]
    public async Task SystemTrustRejectsSelfSignedProxyCertificate()
    {
        using var certificate = Certificate("proxy.example");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeConnectAsync(listener, certificate, SslProtocols.Tls12, timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port(listener), timeout.Token);
        await Assert.ThrowsAsync<AuthenticationException>(() => ProxyTlsTransport.AuthenticateAsync(
            client.GetStream(), "proxy.example", ProxyTlsCertificatePolicy.SystemTrust, timeout.Token));
        client.Dispose();
        var serverFailure = await Record.ExceptionAsync(() => server);
        Assert.True(serverFailure is AuthenticationException or IOException, serverFailure?.ToString());
    }

    [Fact]
    public async Task EncryptionOnlyProxyDoesNotTrustDestinationCertificate()
    {
        using var proxyCertificate = Certificate("proxy.example");
        using var destinationCertificate = Certificate("destination.example");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeNestedTlsAsync(listener, proxyCertificate, destinationCertificate, timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port(listener), timeout.Token);
        await using var proxyTls = await ProxyTlsTransport.AuthenticateAsync(
            client.GetStream(), "proxy.example", ProxyTlsCertificatePolicy.EncryptionOnly, timeout.Token);
        await ProxyTunnelProtocol.EstablishHttpConnectAsync(proxyTls, "destination.example", 443, timeout.Token);
        await using var destinationTls = new SslStream(proxyTls, leaveInnerStreamOpen: true);
        await Assert.ThrowsAsync<AuthenticationException>(() => destinationTls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = "destination.example", EnabledSslProtocols = SslProtocols.Tls12 }, timeout.Token));
        client.Dispose();
        var serverFailure = await Record.ExceptionAsync(() => server);
        Assert.True(serverFailure is AuthenticationException or IOException, serverFailure?.ToString());
    }

    [Fact]
    public async Task CancellationInterruptsSilentProxyAndPreservesOwnedTransport()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port(listener));
        using var server = await listener.AcceptTcpClientAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var transport = client.GetStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProxyTlsTransport.AuthenticateAsync(
            transport, "proxy.example", ProxyTlsCertificatePolicy.EncryptionOnly, timeout.Token));
        Assert.True(transport.CanWrite);
    }

    [Fact]
    public async Task InvalidPolicyCannotBecomeEncryptionOnlyFallback()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProxyTlsTransport.AuthenticateAsync(
            Stream.Null, "proxy.example", (ProxyTlsCertificatePolicy)99, CancellationToken.None));
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static async Task<string> ServeConnectAsync(
        TcpListener listener, X509Certificate2 certificate, SslProtocols protocol, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        { ServerCertificate = certificate, EnabledSslProtocols = protocol }, token);
        var request = await ReadConnectAsync(tls, token);
        await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), token);
        return request;
    }

    private static async Task ServeNestedTlsAsync(
        TcpListener listener, X509Certificate2 proxy, X509Certificate2 destination, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var outer = new SslStream(client.GetStream());
        await outer.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        { ServerCertificate = proxy, EnabledSslProtocols = SslProtocols.Tls12 }, token);
        await ReadConnectAsync(outer, token);
        await outer.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), token);
        await using var inner = new SslStream(outer, leaveInnerStreamOpen: true);
        await inner.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        { ServerCertificate = destination, EnabledSslProtocols = SslProtocols.Tls12 }, token);
        if (await inner.ReadAsync(new byte[1], token) == 0)
            throw new IOException("Client rejected destination TLS before sending application data");
    }

    private static async Task<string> ReadConnectAsync(Stream stream, CancellationToken token)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        while (text.Length < 4096 && !text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Missing CONNECT request");
            text.Append((char)one[0]);
        }
        return text.ToString().Split("\r\n", StringSplitOptions.None)[0];
    }

    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            // SChannel server authentication needs a temporary key container on Windows.
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}
