using System.Globalization;
using System.Net;
using System.Text;

namespace ProxyHarbor.Infrastructure;

/// <summary>Preserves the explicit wire protocols in HProxy's complete 48-hour CSV pool.</summary>
internal static class HProxyCsvFeedAdapter
{
    internal const string Url = "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/all.csv";
    private static readonly string[] Headers = ["ip", "port", "protocols", "anonymity", "country", "city", "latency_ms", "uptime_pct", "alive", "uptime_24h", "uptime_7d", "reliability"];

    internal static bool Supports(string url) => TryGetCountry(url, out _);

    internal static bool TryGetCountry(string url, out string? country)
    {
        country = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.Host != "raw.githubusercontent.com" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is not ("/hproxy-com/free-proxy-list/main/all.csv" or "/hproxy-com/free-proxy-list/refs/heads/main/all.csv")) return false;
        if (uri.Query.Length == 0) return true;
        // This is a local projection selector, not a publisher API parameter.
        if (uri.Query.Length != 11 || !uri.Query.StartsWith("?country=", StringComparison.Ordinal) ||
            uri.Query[9] is < 'A' or > 'Z' || uri.Query[10] is < 'A' or > 'Z') return false;
        country = uri.Query[9..];
        return true;
    }

    internal static string FetchUrl(string url) => Supports(url) ? Url : throw InvalidFormat();

    internal static IEnumerable<KeyValuePair<string, string>> LegacyUrlReplacements()
    {
        foreach (var source in BuiltInSourceCatalog.Sources.Where(source => Supports(source.Url)))
        {
            TryGetCountry(source.Url, out var country);
            var path = country is null ? "all.txt" : $"by-country/{country}.txt";
            yield return new($"https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/{path}", source.Url);
            yield return new($"https://raw.githubusercontent.com/hproxy-com/free-proxy-list/refs/heads/main/{path}", source.Url);
        }
    }

    internal static string Extract(string url, string content)
    {
        if (!TryGetCountry(url, out var country)) throw InvalidFormat();
        return Parse(content).Project(country);
    }

    internal static Document Parse(string content)
    {
        if (content.Length > 10_000_000) throw InvalidFormat();
        using var rows = ReadRows(content.TrimStart('\uFEFF')).GetEnumerator();
        if (!rows.MoveNext() || !rows.Current.SequenceEqual(Headers)) throw InvalidFormat();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var countries = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var count = 0;
        while (rows.MoveNext())
        {
            var row = rows.Current;
            if (++count > 100_000 || row.Length != Headers.Length ||
                !IPAddress.TryParse(row[0], out var address) || !NetworkSafety.IsPublicAddress(address) ||
                !int.TryParse(row[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535 ||
                row[4].Length != 2 || row[4].Any(c => c is < 'A' or > 'Z')) throw InvalidFormat();
            var protocols = row[2].Split('|');
            if (protocols.Distinct(StringComparer.Ordinal).Count() != protocols.Length) throw InvalidFormat();
            var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            foreach (var protocol in protocols)
            {
                if (protocol is not ("http" or "https" or "socks4" or "socks5")) throw InvalidFormat();
                var endpoint = $"{protocol}://{host}:{port}";
                unique.Add(endpoint);
                if (!countries.TryGetValue(row[4], out var countryKeys)) countries.Add(row[4], countryKeys = new(StringComparer.Ordinal));
                countryKeys.Add(endpoint);
            }
        }
        if (count == 0 || unique.Count == 0) throw InvalidFormat();
        return new Document(string.Join('\n', unique), countries.ToDictionary(pair => pair.Key, pair => string.Join('\n', pair.Value), StringComparer.Ordinal));
    }

    internal sealed record Document(string All, IReadOnlyDictionary<string, string> Countries)
    {
        internal string Project(string? country) => country is null ? All :
            Countries.TryGetValue(country, out var result) ? result : throw InvalidFormat();
    }

    private static IEnumerable<string[]> ReadRows(string content)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        for (var index = 0; index < content.Length; index++)
        {
            var c = content[index];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (index + 1 < content.Length && content[index + 1] == '"') { field.Append('"'); index++; }
                else { quoted = false; closed = true; }
            }
            else if (c is ',' or '\r' or '\n')
            {
                fields.Add(field.ToString());
                if (fields.Count > Headers.Length) throw InvalidFormat();
                field.Clear();
                closed = false;
                if (c == ',') continue;
                if (c == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
                yield return fields.ToArray();
                fields.Clear();
            }
            else if (closed) throw InvalidFormat();
            else if (c == '"')
            {
                if (field.Length != 0) throw InvalidFormat();
                quoted = true;
            }
            else field.Append(c);
            if (field.Length > 16_384) throw InvalidFormat();
        }
        if (quoted) throw InvalidFormat();
        if (fields.Count != 0 || field.Length != 0 || closed)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }

    private static InvalidDataException InvalidFormat() => new("Неизвестный формат полной CSV-выгрузки HProxy или отсутствуют прокси выбранной страны.");
}
