using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace ProxyHarbor.Infrastructure;

/// <summary>Единые правила, запрещающие обращения сборщика к локальным и служебным сетям.</summary>
public static class NetworkSafety
{
    /// <summary>
    /// Проверяет canonical ASCII DNS hostname по wire-ограничениям labels.
    /// Underscore, пустые labels, terminal dot и дефис по краям не являются host name.
    /// </summary>
    public static bool IsCanonicalDnsName(string? host)
    {
        if (host is not { Length: >= 1 and <= 253 } || host.Any(character => character > 127) ||
            host.All(character => character is >= '0' and <= '9' or '.'))
            return false;

        var labels = host.Split('.', StringSplitOptions.None);
        return labels.All(label => label is { Length: >= 1 and <= 63 } &&
            label[0] != '-' && label[^1] != '-' &&
            label.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'));
    }

    /// <summary>
    /// Non-throwing синтаксический gate, общий для model validation, normalization и DNS-проверки.
    /// Public-маршрутизируемость адресов отдельно подтверждается непосредственно перед connect.
    /// </summary>
    public static bool TryParseSafeHttpsUrl(
        string? value,
        [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (value is not { Length: >= 1 and <= 2048 } || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || (!parsed.IsDefaultPort && parsed.Port != 443) ||
            string.IsNullOrEmpty(parsed.Host) || parsed.HostNameType == UriHostNameType.Unknown ||
            parsed.AbsoluteUri.Length > 2048 ||
            !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Fragment))
            return false;

        uri = parsed;
        return true;
    }

    /// <summary>Проверяет HTTPS URL и все его текущие DNS-адреса.</summary>
    public static async Task<bool> IsSafePublicHttpsUrlAsync(string? value, CancellationToken token)
    {
        if (!TryParseSafeHttpsUrl(value, out var uri)) return false;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, token);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }
        catch (SocketException) { return false; }
    }

    /// <summary>Разрешает только глобально маршрутизируемые IPv4/IPv6 адреса.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var globalUnicast = (bytes[0] & 0xe0) == 0x20; // 2000::/3.
            // IANA special-purpose ranges внутри 2000::/3 не являются обычными публичными
            // endpoint'ами. В частности, 6to4 способен скрыть вложенный IPv4 destination.
            var ietfAssignments = bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] & 0xfe) == 0; // 2001::/23.
            var documentation2001 = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8; // 2001:db8::/32.
            var sixToFour = bytes[0] == 0x20 && bytes[1] == 0x02; // 2002::/16.
            var formerSixBone = bytes[0] == 0x3f && bytes[1] == 0xfe; // 3ffe::/16.
            var documentation3fff = bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0; // 3fff::/20.
            return globalUnicast && !ietfAssignments && !documentation2001 && !sixToFour &&
                !formerSixBone && !documentation3fff;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        return bytes[0] switch
        {
            0 or 10 or 127 => false,
            100 when bytes[1] is >= 64 and <= 127 => false, // CGNAT 100.64.0.0/10.
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => false,
            192 when bytes[1] == 0 && bytes[2] is 0 or 2 => false, // IETF assignments и TEST-NET-1.
            192 when bytes[1] == 88 && bytes[2] == 99 => false, // Deprecated 6to4 relay anycast.
            192 when bytes[1] == 168 => false,
            198 when bytes[1] is 18 or 19 => false, // Benchmark network.
            198 when bytes[1] == 51 && bytes[2] == 100 => false, // TEST-NET-2.
            203 when bytes[1] == 0 && bytes[2] == 113 => false, // TEST-NET-3.
            >= 224 => false, // Multicast, reserved and broadcast ranges.
            _ => true
        };
    }
}

/// <summary>Повторно проверяет DNS прямо в момент TCP-соединения и тем самым блокирует DNS rebinding.</summary>
public static class PublicNetworkConnector
{
    /// <summary>
    /// Закрепляет handler за прямым соединением через проверяемый connect callback.
    /// Системный HTTP proxy намеренно запрещён: иначе DNS target разрешает proxy,
    /// а финальная защита видит адрес proxy вместо фактического назначения.
    /// </summary>
    internal static SocketsHttpHandler Harden(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler.UseProxy = false;
        handler.AllowAutoRedirect = false;
        handler.ConnectCallback = ConnectAsync;
        return handler;
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        var publicAddresses = addresses.Where(NetworkSafety.IsPublicAddress).ToArray();
        if (publicAddresses.Length == 0 || publicAddresses.Length != addresses.Length)
            throw new HttpRequestException("DNS источника содержит локальный или служебный адрес.");

        Exception? lastError = null;
        foreach (var address in publicAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException) throw;
                lastError = ex;
            }
        }

        throw new HttpRequestException("Не удалось соединиться ни с одним публичным адресом источника.", lastError);
    }
}

/// <summary>
/// Открывает validator-соединение только к каноническому публичному IP из proxy-каталога.
/// Проверка находится в последнем сетевом sink и поэтому не зависит от того, каким
/// ingestion-путём запись попала в PostgreSQL.
/// </summary>
internal static class PublicProxyConnector
{
    internal static async Task<TcpClient> ConnectAsync(
        string host,
        int port,
        CancellationToken token)
    {
        if (port is < 1 or > 65_535 ||
            !IPAddress.TryParse(host, out var address) ||
            address.IsIPv4MappedToIPv6 ||
            !string.Equals(address.ToString(), host, StringComparison.OrdinalIgnoreCase) ||
            !NetworkSafety.IsPublicAddress(address))
            throw new IOException("Proxy endpoint должен быть каноническим публичным IP с допустимым портом.");

        // Передаётся уже проверенный IPAddress, поэтому TcpClient не выполняет DNS и
        // повреждённая запись не способна перенаправить соединение через resolver.
        var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(address, port, token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
