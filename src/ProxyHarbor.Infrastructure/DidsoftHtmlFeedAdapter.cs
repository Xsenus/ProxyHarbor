using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Reads the publisher's bounded proxy table, preserving HTTP CONNECT capability.</summary>
internal static partial class DidsoftHtmlFeedAdapter
{
    private static readonly string[] Headers = ["IP Address", "Port", "Code", "Country", "Anonymity", "Google", "Https", "Last Checked"];

    internal static bool Supports(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        if (uri.Host == "free-proxy-list.net")
            return uri.AbsolutePath is "/" or "/uk-proxy.html" or "/anonymous-proxy.html" or
                "/ssl-proxy.html" or "/us-proxy.html" or "/google-proxy.html";
        return uri.AbsolutePath == "/" && uri.Host is "www.sslproxies.org" or "www.us-proxy.org" or "www.google-proxy.net";
    }

    internal static string Extract(string url, string content)
    {
        if (!Supports(url) || content.Length > 10_000_000) throw InvalidPage();
        var table = Table().Match(content);
        if (!table.Success || table.NextMatch().Success || IsNonContent(content, table.Index)) throw InvalidPage();
        var sections = Sections().Match(table.Groups["body"].Value);
        if (!sections.Success) throw InvalidPage();
        var headers = ReadCells(sections.Groups["headers"].Value, HeaderCell());
        if (!headers.SequenceEqual(Headers)) throw InvalidPage();
        var rows = sections.Groups["rows"].Value;
        var output = new StringBuilder();
        var unique = new HashSet<ProxyCandidateKey>();
        var position = 0;
        var count = 0;
        foreach (Match row in Row().Matches(rows))
        {
            if (!rows.AsSpan(position, row.Index - position).IsWhiteSpace() || ++count > 10_000) throw InvalidPage();
            position = row.Index + row.Length;
            var cells = ReadCells(row.Groups["body"].Value, DataCell());
            if (cells.Count != 8 || cells[6] is not ("yes" or "no") ||
                !IPAddress.TryParse(cells[0], out var address) ||
                !int.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                throw InvalidPage();
            var host = address.ToString();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && host != cells[0]) throw InvalidPage();
            var endpoint = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{host}]:{port}" : $"{host}:{port}";
            Add(endpoint, ProxyProtocol.Http, "http");
            if (cells[6] == "yes") Add(endpoint, ProxyProtocol.Https, "https");
        }
        if (!rows.AsSpan(position).IsWhiteSpace() || unique.Count == 0) throw InvalidPage();
        return output.ToString();

        void Add(string endpoint, ProxyProtocol protocol, string scheme)
        {
            if (ProxyParser.TryParseEndpoint(endpoint, protocol, out var candidate) && unique.Add(candidate))
                output.Append(scheme).Append("://").Append(endpoint).Append('\n');
        }
    }

    private static List<string> ReadCells(string content, Regex regex)
    {
        var cells = new List<string>();
        var position = 0;
        foreach (Match match in regex.Matches(content))
        {
            if (!content.AsSpan(position, match.Index - position).IsWhiteSpace() || cells.Count >= 8) throw InvalidPage();
            cells.Add(WebUtility.HtmlDecode(match.Groups["value"].Value).Trim());
            position = match.Index + match.Length;
        }
        if (!content.AsSpan(position).IsWhiteSpace()) throw InvalidPage();
        return cells;
    }

    private static bool IsNonContent(string content, int index)
    {
        string? rawTag = null;
        var templateDepth = 0;
        for (var position = 0; position < index;)
        {
            if (rawTag is not null)
            {
                var closing = content.IndexOf("</" + rawTag, position, StringComparison.OrdinalIgnoreCase);
                if (closing < 0 || closing >= index) return true;
                var next = closing + rawTag.Length + 2;
                if (next < content.Length && !char.IsWhiteSpace(content[next]) && content[next] != '>')
                {
                    position = next;
                    continue;
                }
                position = closing;
                rawTag = null;
            }
            if (content[position] != '<') { position++; continue; }
            if (content.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                var closing = content.IndexOf("-->", position + 4, StringComparison.Ordinal);
                if (closing < 0 || closing >= index) return true;
                position = closing + 3;
                continue;
            }
            var tagStart = position + 1;
            var end = tagStart;
            var quote = '\0';
            for (; end < index; end++)
            {
                var value = content[end];
                if (quote != '\0') { if (value == quote) quote = '\0'; }
                else if (value is '\'' or '"') quote = value;
                else if (value == '>') break;
            }
            if (end >= index) return true;
            var closingTag = content[tagStart] == '/';
            var nameStart = closingTag ? tagStart + 1 : tagStart;
            var nameEnd = nameStart;
            while (nameEnd < end && char.IsAsciiLetter(content[nameEnd])) nameEnd++;
            var tag = content[nameStart..nameEnd].ToLowerInvariant();
            if (tag == "template")
            {
                templateDepth += closingTag ? -1 : 1;
                if (templateDepth < 0) return true;
            }
            if (!closingTag && tag == "plaintext") return true;
            if (!closingTag && tag is "script" or "style" or "textarea" or "title" or "noscript" or "xmp" or "iframe" or "noembed" or "noframes") rawTag = tag;
            position = end + 1;
        }
        return rawTag is not null || templateDepth != 0;
    }

    private static InvalidDataException InvalidPage() => new("Источник Didsoft не содержит поддерживаемой таблицы прокси.");

    [GeneratedRegex("<table\\s+class\\s*=\\s*[\"']table table-striped table-bordered[\"']\\s*>(?<body>.*?)</table\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex Table();

    [GeneratedRegex("\\A\\s*<thead>\\s*<tr>(?<headers>.*?)</tr>\\s*</thead>\\s*<tbody>(?<rows>.*?)</tbody>\\s*\\z", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex Sections();

    [GeneratedRegex("<th(?:\\s+class\\s*=\\s*[\"'](?:hm|hx)[\"'])?>(?<value>[^<>]*)</th>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex HeaderCell();

    [GeneratedRegex("<tr>(?<body>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex Row();

    [GeneratedRegex("<td(?:\\s+class\\s*=\\s*[\"'](?:hm|hx)[\"'])?>(?<value>[^<>]*)</td>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex DataCell();
}
