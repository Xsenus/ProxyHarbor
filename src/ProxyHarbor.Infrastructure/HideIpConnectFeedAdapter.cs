using System.Text;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Converts the publisher's CONNECT country-suffix export to typed endpoints.</summary>
internal static partial class HideIpConnectFeedAdapter
{
    internal static bool Supports(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath is "/zloi-user/hideip.me/main/connect.txt" or
            "/zloi-user/hideip.me/refs/heads/main/connect.txt";

    internal static string Extract(string url, string content)
    {
        if (!Supports(url)) throw new InvalidDataException("Неизвестный CONNECT export.");
        var output = new StringBuilder();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            var row = CountryRow().Match(line);
            if (!row.Success) throw new InvalidDataException("Неизвестный формат CONNECT export.");
            // Keep the shared canonical-IP, public-address and port checks. The
            // trailing country is metadata, never a username or password.
            if (!ProxyParser.TryParseEndpoint(row.Groups["endpoint"].Value, ProxyProtocol.Https, out var key)) continue;
            var endpoint = key.ToEndpoint();
            output.Append("https://").Append(endpoint.Host).Append(':').Append(endpoint.Port).Append('\n');
        }
        if (output.Length == 0) throw new InvalidDataException("CONNECT export не содержит публичных прокси.");
        return output.ToString();
    }

    [GeneratedRegex(@"\A(?<endpoint>(?:[0-9]{1,3}\.){3}[0-9]{1,3}:[0-9]{1,5}):[\p{L}][\p{L} .()\-]{0,99}\z", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex CountryRow();
}
