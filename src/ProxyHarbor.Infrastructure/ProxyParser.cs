using System.Net;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Безопасно извлекает IP/hostname и порт из распространённых текстовых форматов.</summary>
public static partial class ProxyParser
{
    [GeneratedRegex(@"(?im)(?:(?<scheme>https?|socks4|socks5)://)?(?<host>(?:\[[0-9a-f:]+\])|(?:\d{1,3}\.){3}\d{1,3}|(?:[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?)):(?<port>\d{1,5})")]
    private static partial Regex EndpointRegex();

    /// <summary>Разбирает содержимое источника и возвращает только синтаксически допустимые уникальные адреса.</summary>
    public static IReadOnlyCollection<(string Host, int Port, ProxyProtocol Protocol)> Parse(
        string content,
        ProxyProtocol defaultProtocol)
    {
        var result = new Dictionary<string, (string, int, ProxyProtocol)>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in EndpointRegex().Matches(content))
        {
            var host = match.Groups["host"].Value.Trim('[', ']');
            if (!int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535)
                continue;

            // Доменные имена исключены намеренно: они позволяют DNS rebinding к внутренней сети.
            if (!IPAddress.TryParse(host, out var ip) || !NetworkSafety.IsPublicAddress(ip)) continue;

            var protocol = ParseProtocol(match.Groups["scheme"].Value, defaultProtocol);
            var normalizedHost = ip.ToString();
            result[$"{protocol}:{normalizedHost}:{port}"] = (normalizedHost, port, protocol);
        }

        return result.Values;
    }

    private static ProxyProtocol ParseProtocol(string value, ProxyProtocol fallback) => value.ToLowerInvariant() switch
    {
        "http" => ProxyProtocol.Http,
        "https" => ProxyProtocol.Https,
        "socks4" => ProxyProtocol.Socks4,
        "socks5" => ProxyProtocol.Socks5,
        _ => fallback
    };
}
