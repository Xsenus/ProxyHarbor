using System.Net;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Безопасно извлекает IP и порт из распространённых текстовых форматов.</summary>
public static partial class ProxyParser
{
    [GeneratedRegex(
        @"(?<host>(?:\[[0-9a-f:]+\])|(?:\d{1,3}\.){3}\d{1,3}):(?<port>\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EndpointRegex();

    /// <summary>Разбирает содержимое источника и возвращает только синтаксически допустимые уникальные адреса.</summary>
    public static IReadOnlyCollection<(string Host, int Port, ProxyProtocol Protocol)> Parse(
        string content,
        ProxyProtocol defaultProtocol) => ParseWithLimitStatus(content, defaultProtocol, int.MaxValue).Items;

    /// <summary>
    /// Разбирает не более <paramref name="maxResults"/> уникальных адресов, не создавая полный
    /// промежуточный список для потенциально многомиллионного недоверенного feed'а.
    /// </summary>
    public static IReadOnlyCollection<(string Host, int Port, ProxyProtocol Protocol)> Parse(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults) => ParseWithLimitStatus(content, defaultProtocol, maxResults).Items;

    /// <summary>
    /// Возвращает не только bounded-набор, но и точный признак наличия следующего уникального
    /// адреса. После обнаружения первого адреса сверх лимита разбор немедленно прекращается.
    /// </summary>
    internal static ProxyParseResult ParseWithLimitStatus(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        var result = new List<(string Host, int Port, ProxyProtocol Protocol)>(Math.Min(maxResults, 4_096));
        var unique = new HashSet<(string Host, int Port, ProxyProtocol Protocol)>();

        foreach (Match match in EndpointRegex().Matches(content))
        {
            var host = match.Groups["host"].ValueSpan;
            if (host.Length >= 2 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
            var portText = match.Groups["port"].ValueSpan;
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
                continue;

            // Regex даже не выделяет доменные endpoints: их исключение блокирует DNS rebinding.
            if (!IPAddress.TryParse(host, out var ip) || !NetworkSafety.IsPublicAddress(ip)) continue;

            // Scheme находится непосредственно перед IP, но намеренно не включён в regex:
            // так URL/временные метки в заголовке feed'а не влияют на поиск следующего endpoint.
            var protocol = ParseProtocolBefore(content.AsSpan(0, match.Index), defaultProtocol);
            var normalizedHost = ip.ToString();
            var endpoint = (normalizedHost, port, protocol);
            if (result.Count < maxResults)
            {
                if (!unique.Add(endpoint)) continue;
                result.Add(endpoint);
                continue;
            }

            // После заполнения коллекции lookup нужен только для различения безопасного
            // duplicate-tail и первого действительно потерянного уникального адреса.
            if (!unique.Contains(endpoint)) return new ProxyParseResult(result, Truncated: true);
        }

        return new ProxyParseResult(result, Truncated: false);
    }

    private static ProxyProtocol ParseProtocolBefore(ReadOnlySpan<char> prefix, ProxyProtocol fallback) =>
        prefix.EndsWith("http://", StringComparison.OrdinalIgnoreCase) ? ProxyProtocol.Http :
        prefix.EndsWith("https://", StringComparison.OrdinalIgnoreCase) ? ProxyProtocol.Https :
        prefix.EndsWith("socks4://", StringComparison.OrdinalIgnoreCase) ? ProxyProtocol.Socks4 :
        prefix.EndsWith("socks5://", StringComparison.OrdinalIgnoreCase) ? ProxyProtocol.Socks5 :
        fallback;
}

/// <summary>Bounded-результат parser с явным сигналом, что вход содержал ещё уникальные адреса.</summary>
internal sealed record ProxyParseResult(
    IReadOnlyCollection<(string Host, int Port, ProxyProtocol Protocol)> Items,
    bool Truncated);
