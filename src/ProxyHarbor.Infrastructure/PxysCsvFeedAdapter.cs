using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Reads the publisher's protocol column instead of treating every CSV endpoint as HTTP.</summary>
internal static class PxysCsvFeedAdapter
{
    internal const string Url = "https://raw.githubusercontent.com/Pxys-io/DailyProxyList/master/working_proxies.csv";
    internal static readonly string[] Urls = [Url, "https://raw.githubusercontent.com/Pxys-io/DailyProxyList/refs/heads/master/working_proxies.csv"];
    private static readonly string[] Headers = ["protocol", "proxy_address", "latency_ms", "ssl_support", "country", "anonymity", "source", "last_tested"];

    internal static bool Supports(string url) => Urls.Contains(url, StringComparer.Ordinal);

    internal static string Extract(string content)
    {
        if (content.Length > 10_000_000) throw InvalidFormat();
        using var rows = BoundedCsvReader.ReadRows(content.TrimStart('\uFEFF'), Headers.Length).GetEnumerator();
        if (!rows.MoveNext() || !rows.Current.SequenceEqual(Headers)) throw InvalidFormat();
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        while (rows.MoveNext())
        {
            var row = rows.Current;
            var protocol = row[0].ToLowerInvariant();
            if (++count > 100_000 || row.Length != Headers.Length ||
                protocol is not ("http" or "https" or "socks4" or "socks5")) throw InvalidFormat();
            // Validate the endpoint independently of metadata and protocol declarations.
            var parsed = ProxyParser.Parse("http://" + row[1], ProxyProtocol.Http);
            if (parsed.Count != 1) throw InvalidFormat();
            var endpoint = parsed.Single();
            var host = endpoint.Host.Contains(':') ? $"[{endpoint.Host}]" : endpoint.Host;
            if (row[1] != $"{host}:{endpoint.Port}") throw InvalidFormat();
            // A publisher HTTPS label alone does not establish CONNECT443 or TLS to the proxy.
            // Keep that declaration out of the wire queue until its transport is established.
            if (protocol == "https") continue;
            endpoints.Add($"{protocol}://{host}:{endpoint.Port}");
        }
        if (endpoints.Count == 0) throw InvalidFormat();
        return string.Join('\n', endpoints);
    }

    private static InvalidDataException InvalidFormat() => new("Неизвестный формат CSV Pxys или отсутствуют прокси с подтверждённым транспортным профилем.");
}
