using System.Text;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Converts the publisher's country-suffix exports without guessing wire protocols.</summary>
internal static partial class HideIpConnectFeedAdapter
{
    internal static bool Supports(string url) => TryReadFile(url, out var file) && file != "connect.txt";

    internal static bool IsUnresolvedConnectUrl(string url) => TryReadFile(url, out var file) && file == "connect.txt";

    private static bool TryReadFile(string url, out string? file)
    {
        file = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.Host != "raw.githubusercontent.com" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        const string prefix = "/zloi-user/hideip.me/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var path = uri.AbsolutePath[prefix.Length..];
        if (path.StartsWith("refs/heads/", StringComparison.Ordinal)) path = path[11..];
        if (!path.StartsWith("main/", StringComparison.Ordinal)) return false;
        file = path[5..];
        return file is "http.txt" or "https.txt" or "socks4.txt" or "socks5.txt" or "connect.txt";
    }

    internal static string Extract(string url, string content)
    {
        if (!Supports(url) || content.Length > 10_000_000) throw new InvalidDataException("Неизвестный country export hideip.me.");
        TryReadFile(url, out var file);
        var (protocol, scheme) = file switch
        {
            "http.txt" => (ProxyProtocol.Http, "http"),
            "https.txt" => (ProxyProtocol.HttpTls, "http+tls"),
            "socks4.txt" => (ProxyProtocol.Socks4, "socks4"),
            "socks5.txt" => (ProxyProtocol.Socks5, "socks5"),
            _ => throw new InvalidDataException("Протокол hideip.me не подтверждён.")
        };
        var output = new StringBuilder();
        var unique = new HashSet<ProxyCandidateKey>();
        var rows = 0;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            if (++rows > 100_000 || line.Length > 256) throw new InvalidDataException("Превышен лимит country export.");
            var row = CountryRow().Match(line);
            if (!row.Success) throw new InvalidDataException("Неизвестный формат country export.");
            // Keep the shared canonical-IP, public-address and port checks. The
            // trailing country is metadata, never a username or password.
            if (!ProxyParser.TryParseEndpoint(row.Groups["endpoint"].Value, protocol, out var key) || !unique.Add(key)) continue;
            var endpoint = key.ToEndpoint();
            output.Append(scheme).Append("://").Append(endpoint.Host).Append(':').Append(endpoint.Port).Append('\n');
        }
        if (output.Length == 0) throw new InvalidDataException("Country export не содержит публичных прокси.");
        return output.ToString();
    }

    [GeneratedRegex(@"\A(?<endpoint>(?:[0-9]{1,3}\.){3}[0-9]{1,3}:[0-9]{1,5}):[\p{L}][\p{L} .()\-]{0,99}\z", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex CountryRow();
}
