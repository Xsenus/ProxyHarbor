using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ProxyHarbor.Infrastructure;

/// <summary>Certificate policy applies only to the encrypted client-to-proxy hop.</summary>
internal enum ProxyTlsCertificatePolicy
{
    SystemTrust,
    EncryptionOnly
}

/// <summary>Establishes TLS before HTTP CONNECT without changing destination TLS validation.</summary>
internal static class ProxyTlsTransport
{
    internal static async Task<SslStream> AuthenticateAsync(
        Stream transport,
        string proxyHost,
        ProxyTlsCertificatePolicy certificatePolicy,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyHost);
        if (!Enum.IsDefined(certificatePolicy))
            throw new ArgumentOutOfRangeException(nameof(certificatePolicy));

        // An encryption-only hop has no proxy identity guarantee, just as a plaintext
        // HTTP proxy has none. Callers must explicitly select this policy; it never
        // affects the separately authenticated TLS connection to the destination.
        var tls = certificatePolicy == ProxyTlsCertificatePolicy.EncryptionOnly
            ? new SslStream(transport, leaveInnerStreamOpen: true,
                static (_, certificate, _, _) => certificate is not null)
            : new SslStream(transport, leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = proxyHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, token);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync();
            throw;
        }
    }
}
