using System.Text;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Converts explicitly supported My-Proxy list pages to typed feeds.</summary>
internal static partial class MyProxyHtmlFeedAdapter
{
    private const int MaximumRows = 10_000;

    internal static bool Supports(string url) => TryGetProtocol(url, out _);

    internal static void EnsureSupportedMediaType(string? mediaType)
    {
        if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase)) return;
        SourceFeedParser.EnsureSupportedMediaType(mediaType);
    }

    internal static string Extract(string url, string content)
    {
        if (!TryGetProtocol(url, out var protocol)) throw InvalidPage();
        var section = ListContainer().Match(content);
        if (!section.Success || section.NextMatch().Success || IsNonContent(content, section.Index)) throw InvalidPage();
        var lines = LineBreak().Split(section.Groups["body"].Value, MaximumRows + 2);
        if (lines.Length > MaximumRows + 1) throw InvalidPage();
        var output = new StringBuilder();
        var unique = new HashSet<ProxyCandidateKey>();
        var scheme = protocol switch
        {
            ProxyProtocol.Socks4 => "socks4",
            ProxyProtocol.Socks5 => "socks5",
            _ => "http"
        };
        foreach (var line in lines)
        {
            var row = line.Trim();
            if (row.Length == 0) continue;
            var match = ListRow().Match(row);
            if (!match.Success) throw InvalidPage();
            var endpoint = match.Groups["endpoint"].Value;
            if (!ProxyParser.TryParseEndpoint(endpoint, protocol, out var candidate) || !unique.Add(candidate)) continue;
            output.Append(scheme).Append("://").Append(endpoint).Append('\n');
        }
        if (unique.Count == 0) throw InvalidPage();
        return output.ToString();
    }

    private static bool TryGetProtocol(string url, out ProxyProtocol protocol)
    {
        protocol = ProxyProtocol.Http;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "www.my-proxy.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        if (uri.AbsolutePath is "/free-proxy-list.html" or "/free-elite-proxy.html" or
            "/free-anonymous-proxy.html" or "/free-transparent-proxy.html") return true;
        for (var index = 2; index <= 10; index++)
            if (uri.AbsolutePath == $"/free-proxy-list-{index}.html") return true;
        if (uri.AbsolutePath == "/free-socks-4-proxy.html") { protocol = ProxyProtocol.Socks4; return true; }
        if (uri.AbsolutePath == "/free-socks-5-proxy.html") { protocol = ProxyProtocol.Socks5; return true; }
        return false;
    }

    private static InvalidDataException InvalidPage() => new("Источник My-Proxy не содержит поддерживаемого списка прокси.");

    private static bool IsNonContent(string content, int index)
    {
        var prefix = content.AsSpan(0, index);
        if (prefix.LastIndexOf('<') > prefix.LastIndexOf('>') ||
            prefix.LastIndexOf("<!--", StringComparison.Ordinal) > prefix.LastIndexOf("-->", StringComparison.Ordinal)) return true;
        foreach (var tag in new[] { "script", "style", "textarea", "title" })
            if (prefix.LastIndexOf("<" + tag, StringComparison.OrdinalIgnoreCase) >
                prefix.LastIndexOf("</" + tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // These are fixed publisher containers, not a scan for IPs in arbitrary HTML.
    // Unknown nested markup, login/error pages and duplicate containers fail closed.
    [GeneratedRegex("<div\\s+class\\s*=\\s*\"list\"\\s*>(?<body>.*?)</div\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex ListContainer();

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex LineBreak();

    [GeneratedRegex("\\A(?<endpoint>(?:[0-9]{1,3}\\.){3}[0-9]{1,3}:[0-9]{1,5})(?:#[A-Z]{2})?\\z", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex ListRow();
}
