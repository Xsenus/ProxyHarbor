using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Reads Live Socks' latest drop or a dated table without guessing SOCKS versions.</summary>
internal static partial class LiveSocksHtmlFeedAdapter
{
    internal const string Url = "https://live-socks.net/";
    private static readonly string[] Headers = ["IP : port", "Country", "Type", "Response", "Uptime"];

    internal static bool Supports(string url) => IsHome(url) || TryGetPage(url, out _, out _);

    internal static bool IsHome(string url) => string.Equals(url, Url, StringComparison.Ordinal);

    internal static async Task<SourceFetchResult> FetchLatestAsync(
        Func<string, int, CancellationToken, Task<SourceFetchResult>> fetch,
        int maximumBytes, CancellationToken token)
    {
        var home = await fetch(Url, maximumBytes, token);
        if (home.NotModified || home.Content is null) throw InvalidPage();
        var latestUrl = LatestUrl(home.Content);
        var remaining = maximumBytes - Encoding.UTF8.GetByteCount(home.Content);
        if (remaining <= 0) throw InvalidPage();
        var page = await fetch(latestUrl, remaining, token);
        if (page.NotModified || page.Content is null) throw InvalidPage();
        // Validators from either page cannot authenticate this two-page representation.
        return new SourceFetchResult(Extract(latestUrl, page.Content), false, null, null);
    }

    internal static string LatestUrl(string content)
    {
        if (content.Length > 10_000_000) throw InvalidPage();
        var drop = LatestDrop().Match(content);
        if (!drop.Success || drop.NextMatch().Success || HtmlFeedContext.IsNonContent(content, drop.Index)) throw InvalidPage();
        var link = LatestLink().Match(drop.Groups["body"].Value);
        if (!link.Success || link.NextMatch().Success ||
            HtmlFeedContext.IsNonContent(drop.Groups["body"].Value, link.Index)) throw InvalidPage();
        var path = link.Groups["path"].Value;
        var url = Url.TrimEnd('/') + path;
        if (!path.StartsWith('/') || !TryGetPage(url, out var date, out var count) ||
            WebUtility.HtmlDecode(link.Groups["title"].Value).Trim() != PageTitle(date, count)) throw InvalidPage();
        return url;
    }

    internal static string Extract(string url, string content)
    {
        if (!TryGetPage(url, out var date, out var expectedRows) || content.Length > 10_000_000) throw InvalidPage();
        var heading = Heading().Match(content);
        if (!heading.Success || heading.NextMatch().Success || HtmlFeedContext.IsNonContent(content, heading.Index) ||
            WebUtility.HtmlDecode(heading.Groups["title"].Value).Trim() != PageTitle(date, expectedRows) + " · Free SOCKS Proxy List") throw InvalidPage();
        var table = Table().Match(content);
        if (!table.Success || table.NextMatch().Success || HtmlFeedContext.IsNonContent(content, table.Index)) throw InvalidPage();
        var sections = Sections().Match(table.Groups["body"].Value);
        if (!sections.Success || WebUtility.HtmlDecode(sections.Groups["caption"].Value).Trim() !=
            "SOCKS proxies published on " + date.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture)) throw InvalidPage();
        var headerBody = sections.Groups["headers"].Value;
        var headers = Header().Matches(headerBody);
        if (headers.Count != Headers.Length || !headers.Select(m => m.Groups["value"].Value).SequenceEqual(Headers) ||
            !Header().Replace(headerBody, "").AsSpan().IsWhiteSpace()) throw InvalidPage();
        var rows = sections.Groups["rows"].Value;
        var output = new StringBuilder();
        var unique = new HashSet<ProxyCandidateKey>();
        var position = 0;
        var rowCount = 0;
        foreach (Match row in Row().Matches(rows))
        {
            if (!rows.AsSpan(position, row.Index - position).IsWhiteSpace() || ++rowCount > expectedRows) throw InvalidPage();
            position = row.Index + row.Length;
            var cells = Cells().Match(row.Groups["body"].Value);
            var endpoint = row.Groups["endpoint"].Value;
            if (!cells.Success || cells.Groups["endpoint"].Value != endpoint ||
                !int.TryParse(cells.Groups["width"].Value, out var width) || width is < 0 or > 100 ||
                !int.TryParse(cells.Groups["uptime"].Value, out var uptime) || width != uptime) throw InvalidPage();
            var split = endpoint.Split(':');
            if (split.Length != 2 || !IPAddress.TryParse(split[0], out var address) || address.ToString() != split[0] ||
                !int.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) throw InvalidPage();
            var protocol = cells.Groups["protocol"].Value == "SOCKS4" ? ProxyProtocol.Socks4 : ProxyProtocol.Socks5;
            if (ProxyParser.TryParseEndpoint(endpoint, protocol, out var candidate) && unique.Add(candidate))
                output.Append(protocol == ProxyProtocol.Socks4 ? "socks4://" : "socks5://").Append(endpoint).Append('\n');
        }
        if (!rows.AsSpan(position).IsWhiteSpace() || rowCount != expectedRows || unique.Count == 0) throw InvalidPage();
        return output.ToString();
    }

    private static string PageTitle(DateOnly date, int count) =>
        date.ToString("dd-MM-yy", CultureInfo.InvariantCulture) + " | Socks 5 Servers (" + count.ToString(CultureInfo.InvariantCulture) + ")";

    private static bool TryGetPage(string url, out DateOnly date, out int count)
    {
        date = default;
        count = 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "live-socks.net" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || url.Contains('%') || url.Contains('\\')) return false;
        var match = PagePath().Match(uri.AbsolutePath);
        if (!match.Success || !DateOnly.TryParseExact(match.Groups["year"].Value + "-" + match.Groups["month"].Value + "-" + match.Groups["day"].Value,
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
        return date.ToString("MM-yy", CultureInfo.InvariantCulture) == match.Groups["monthYear"].Value &&
            int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count is >= 1 and <= 10_000;
    }

    private static InvalidDataException InvalidPage() => new("Источник Live Socks не содержит поддерживаемой публикации прокси.");

    [GeneratedRegex(@"\A/(?<year>20[0-9]{2})/(?<month>0[1-9]|1[0-2])/(?<day>0[1-9]|[12][0-9]|3[01])-(?<monthYear>(?:0[1-9]|1[0-2])-[0-9]{2})-socks-5-servers-(?<count>[1-9][0-9]{0,4})(?:_[1-9][0-9]{0,2})?\.html\z", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex PagePath();
    [GeneratedRegex("<section class=\"sec\">\\s*<div class=\"wrap\">\\s*<div class=\"sec__head\"><h2>Latest drop</h2><a href=\"/archive.html\">[^<>]*</a></div>\\s*<article class=\"drop\">(?<body>.*?)</article>\\s*</div>\\s*</section>", RegexOptions.Singleline | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex LatestDrop();
    [GeneratedRegex("<h3>\\s*<a href=\"(?<path>[^\"<>]+)\">(?<title>[^<>]*)</a>\\s*</h3>", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex LatestLink();
    [GeneratedRegex("<h1(?:\\s+style=\"[^\"<>]*\")?>(?<title>[^<>]*)</h1>", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex Heading();
    [GeneratedRegex(@"<table> (?<body>.*?) </table>", RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex Table();
    [GeneratedRegex("\\A\\s*<caption class=\"sr\">(?<caption>[^<>]*)</caption>\\s*<thead>\\s*<tr>(?<headers>.*?)</tr>\\s*</thead>\\s*<tbody id=\"tb\">(?<rows>.*?)</tbody>\\s*\\z", RegexOptions.Singleline | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex Sections();
    [GeneratedRegex(@"<th>(?<value>[^<>]*)</th>", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex Header();
    [GeneratedRegex("<tr data-addr=\"(?<endpoint>[^\"<>]+)\">(?<body>.*?)</tr>", RegexOptions.Singleline | RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex Row();
    [GeneratedRegex("\\A\\s*<td class=\"addr\">(?<endpoint>[^<>]+)</td>\\s*<td><span class=\"fi fi-[a-z]{2}\"></span>\\s*[^<>]+</td>\\s*<td>(?<protocol>SOCKS[45])</td>\\s*<td class=\"(?:ok|mid|slow)\">[0-9]+(?:\\.[0-9]+)? s</td>\\s*<td><span class=\"bar\"><i style=\"width:(?<width>[0-9]{1,3})%\"></i></span>\\s*(?<uptime>[0-9]{1,3})%</td>\\s*\\z", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex Cells();
}
