using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Версионируемый каталог публичных proxy-feed endpoint'ов от 50 независимых провайдеров.</summary>
public static class BuiltInSourceCatalog
{
    public static DateOnly LastAuditedOn => new(2026, 8, 10);

    /// <summary>Источники ранжированы по свежести, объёму, стабильности ответа и разнообразию провайдеров.</summary>
    public static IReadOnlyList<BuiltInSource> Sources { get; } =
    [
        // HTTP/HTTPS и смешанные feed'ы.
        Feed(1, "ProxyScrape V4 Mixed", "ProxyScrape", "https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=text", ProxyProtocol.Http),
        Feed(2, "ProxyScrape V2 HTTP", "ProxyScrape", "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=http", ProxyProtocol.Http),
        Feed(3, "OpenProxyList HTTP", "OpenProxyList", "https://openproxylist.xyz/http.txt", ProxyProtocol.Http),
        Feed(4, "OpenProxyList HTTPS", "OpenProxyList", "https://openproxylist.xyz/https.txt", ProxyProtocol.Https),
        Feed(5, "Proxifly HTTP", "Proxifly", "https://raw.githubusercontent.com/proxifly/free-proxy-list/refs/heads/main/proxies/protocols/http/data.txt", ProxyProtocol.Http),
        Feed(6, "Proxifly HTTPS", "Proxifly", "https://raw.githubusercontent.com/proxifly/free-proxy-list/refs/heads/main/proxies/protocols/https/data.txt", ProxyProtocol.Https),
        Feed(7, "TheSpeedX HTTP", "TheSpeedX", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/refs/heads/master/http.txt", ProxyProtocol.Http),
        Feed(8, "IPLocate HTTP", "IPLocate", "https://raw.githubusercontent.com/iplocate/free-proxy-list/refs/heads/main/protocols/http.txt", ProxyProtocol.Http),
        Feed(9, "Databay HTTP", "Databay Labs", "https://raw.githubusercontent.com/databay-labs/free-proxy-list/refs/heads/master/http.txt", ProxyProtocol.Http),
        Feed(10, "TuanMinPay HTTP", "TuanMinPay", "https://raw.githubusercontent.com/TuanMinPay/live-proxy/refs/heads/master/http.txt", ProxyProtocol.Http),
        Feed(11, "HProxy HTTP", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/refs/heads/main/http.txt", ProxyProtocol.Http),
        Feed(12, "ObcbO HTTP", "ObcbO", "https://raw.githubusercontent.com/ObcbO/getproxy/refs/heads/master/file/http.txt", ProxyProtocol.Http),
        Feed(13, "EbraSha HTTPS", "Abdal Proxy Hub", "https://raw.githubusercontent.com/ebrasha/abdal-proxy-hub/refs/heads/main/https-proxy-list-by-EbraSha.txt", ProxyProtocol.Https),
        Feed(14, "Free-PROXY HTTP", "Dpangestuw", "https://raw.githubusercontent.com/dpangestuw/Free-PROXY/refs/heads/main/http_proxies.txt", ProxyProtocol.Http),
        Feed(15, "AnonymousWork HTTP", "AnonymousWork", "https://raw.githubusercontent.com/Anonym0usWork1221/Free-Proxies/refs/heads/main/proxy_files/http_proxies.txt", ProxyProtocol.Http),
        Feed(16, "r00tee HTTPS", "r00tee", "https://raw.githubusercontent.com/r00tee/Proxy-List/refs/heads/main/Https.txt", ProxyProtocol.Https),
        Feed(17, "ProxySpace HTTP", "ProxySpace", "https://proxyspace.pro/http.txt", ProxyProtocol.Http),
        Feed(18, "Jetkai HTTP", "Jetkai", "https://raw.githubusercontent.com/jetkai/proxy-list/refs/heads/main/online-proxies/txt/proxies-http.txt", ProxyProtocol.Http),
        Feed(19, "B4RC0DE HTTP", "B4RC0DE", "https://raw.githubusercontent.com/B4RC0DE-TM/proxy-list/refs/heads/main/HTTP.txt", ProxyProtocol.Http),
        Feed(20, "Argh94 HTTP", "Argh94", "https://raw.githubusercontent.com/Argh94/Proxy-List/refs/heads/main/HTTP.txt", ProxyProtocol.Http),
        Feed(21, "Monosans HTTP", "Monosans", "https://raw.githubusercontent.com/monosans/proxy-list/refs/heads/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(22, "Spys.me HTTP", "Spys.me", "https://spys.me/proxy.txt", ProxyProtocol.Http),

        // SOCKS4 feed'ы.
        Feed(23, "ProxyScrape V2 SOCKS4", "ProxyScrape", "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=socks4", ProxyProtocol.Socks4),
        Feed(24, "OpenProxyList SOCKS4", "OpenProxyList", "https://openproxylist.xyz/socks4.txt", ProxyProtocol.Socks4),
        Feed(25, "Proxifly SOCKS4", "Proxifly", "https://raw.githubusercontent.com/proxifly/free-proxy-list/refs/heads/main/proxies/protocols/socks4/data.txt", ProxyProtocol.Socks4),
        Feed(26, "TheSpeedX SOCKS4", "TheSpeedX", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/refs/heads/master/socks4.txt", ProxyProtocol.Socks4),
        Feed(27, "IPLocate SOCKS4", "IPLocate", "https://raw.githubusercontent.com/iplocate/free-proxy-list/refs/heads/main/protocols/socks4.txt", ProxyProtocol.Socks4),
        Feed(28, "Databay SOCKS4", "Databay Labs", "https://raw.githubusercontent.com/databay-labs/free-proxy-list/refs/heads/master/socks4.txt", ProxyProtocol.Socks4),
        Feed(29, "TuanMinPay SOCKS4", "TuanMinPay", "https://raw.githubusercontent.com/TuanMinPay/live-proxy/refs/heads/master/socks4.txt", ProxyProtocol.Socks4),
        Feed(30, "HProxy SOCKS4", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/refs/heads/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(31, "ObcbO SOCKS4", "ObcbO", "https://raw.githubusercontent.com/ObcbO/getproxy/refs/heads/master/file/socks4.txt", ProxyProtocol.Socks4),
        Feed(32, "EbraSha SOCKS4", "Abdal Proxy Hub", "https://raw.githubusercontent.com/ebrasha/abdal-proxy-hub/refs/heads/main/socks4-proxy-list-by-EbraSha.txt", ProxyProtocol.Socks4),
        Feed(33, "Free-PROXY SOCKS4", "Dpangestuw", "https://raw.githubusercontent.com/dpangestuw/Free-PROXY/refs/heads/main/socks4_proxies.txt", ProxyProtocol.Socks4),
        Feed(34, "AnonymousWork SOCKS4", "AnonymousWork", "https://raw.githubusercontent.com/Anonym0usWork1221/Free-Proxies/refs/heads/main/proxy_files/socks4_proxies.txt", ProxyProtocol.Socks4),
        Feed(35, "r00tee SOCKS4", "r00tee", "https://raw.githubusercontent.com/r00tee/Proxy-List/refs/heads/main/Socks4.txt", ProxyProtocol.Socks4),
        Feed(36, "ProxySpace SOCKS4", "ProxySpace", "https://proxyspace.pro/socks4.txt", ProxyProtocol.Socks4),

        // SOCKS5 feed'ы.
        Feed(37, "ProxyScrape V2 SOCKS5", "ProxyScrape", "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=socks5", ProxyProtocol.Socks5),
        Feed(38, "OpenProxyList SOCKS5", "OpenProxyList", "https://openproxylist.xyz/socks5.txt", ProxyProtocol.Socks5),
        Feed(39, "Proxifly SOCKS5", "Proxifly", "https://raw.githubusercontent.com/proxifly/free-proxy-list/refs/heads/main/proxies/protocols/socks5/data.txt", ProxyProtocol.Socks5),
        Feed(40, "TheSpeedX SOCKS5", "TheSpeedX", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/refs/heads/master/socks5.txt", ProxyProtocol.Socks5),
        Feed(41, "IPLocate SOCKS5", "IPLocate", "https://raw.githubusercontent.com/iplocate/free-proxy-list/refs/heads/main/protocols/socks5.txt", ProxyProtocol.Socks5),
        Feed(42, "Databay SOCKS5", "Databay Labs", "https://raw.githubusercontent.com/databay-labs/free-proxy-list/refs/heads/master/socks5.txt", ProxyProtocol.Socks5),
        Feed(43, "TuanMinPay SOCKS5", "TuanMinPay", "https://raw.githubusercontent.com/TuanMinPay/live-proxy/refs/heads/master/socks5.txt", ProxyProtocol.Socks5),
        Feed(44, "HProxy SOCKS5", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/refs/heads/main/socks5.txt", ProxyProtocol.Socks5),
        Feed(45, "ObcbO SOCKS5", "ObcbO", "https://raw.githubusercontent.com/ObcbO/getproxy/refs/heads/master/file/socks5.txt", ProxyProtocol.Socks5),
        Feed(46, "EbraSha SOCKS5", "Abdal Proxy Hub", "https://raw.githubusercontent.com/ebrasha/abdal-proxy-hub/refs/heads/main/socks5-proxy-list-by-EbraSha.txt", ProxyProtocol.Socks5),
        Feed(47, "Free-PROXY SOCKS5", "Dpangestuw", "https://raw.githubusercontent.com/dpangestuw/Free-PROXY/refs/heads/main/socks5_proxies.txt", ProxyProtocol.Socks5),
        Feed(48, "AnonymousWork SOCKS5", "AnonymousWork", "https://raw.githubusercontent.com/Anonym0usWork1221/Free-Proxies/refs/heads/main/proxy_files/socks5_proxies.txt", ProxyProtocol.Socks5),
        Feed(49, "r00tee SOCKS5", "r00tee", "https://raw.githubusercontent.com/r00tee/Proxy-List/refs/heads/main/Socks5.txt", ProxyProtocol.Socks5),
        Feed(50, "ProxySpace SOCKS5", "ProxySpace", "https://proxyspace.pro/socks5.txt", ProxyProtocol.Socks5),

        // Дополнительные независимые проекты. Каждый endpoint ниже прошёл отдельный живой аудит.
        Feed(51, "CyberH4ck3r HTTP", "CyberH4ck3r", "https://raw.githubusercontent.com/cyberh4ck3r/free-proxy-list/main/http-proxies.txt", ProxyProtocol.Http),
        Feed(52, "Proxmint HTTP", "Proxmint", "https://raw.githubusercontent.com/proxmint/free-proxy-list/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(53, "Rix4Uni Mixed", "Rix4Uni", "https://raw.githubusercontent.com/rix4uni/fresh-proxy-list/main/proxylist.txt", ProxyProtocol.Http),
        Feed(54, "Komutan234 HTTP", "Komutan234", "https://raw.githubusercontent.com/komutan234/Proxy-List-Free/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(55, "Zaeem20 HTTP", "Zaeem20", "https://raw.githubusercontent.com/Zaeem20/FREE_PROXIES_LIST/master/http.txt", ProxyProtocol.Http),
        Feed(56, "WebUnblocker HTTP", "WebUnblocker", "https://raw.githubusercontent.com/webunblocker/free-proxy-list/main/proxies/protocols/http/data.txt", ProxyProtocol.Http),
        Feed(57, "Watchttvv SOCKS5", "Watchttvv", "https://raw.githubusercontent.com/watchttvv/free-proxy-list/main/proxy.txt", ProxyProtocol.Socks5),
        Feed(58, "VPSLab HTTP", "VPSLab", "https://raw.githubusercontent.com/VPSLabCloud/VPSLab-Free-Proxy-List/main/http_all.txt", ProxyProtocol.Http),
        Feed(59, "VMHeaven Mixed", "VMHeaven", "https://raw.githubusercontent.com/vmheaven/VMHeaven.io-Free-Proxy-List/main/allproxy.txt", ProxyProtocol.Http),
        Feed(60, "GProxyNet HTTP", "GProxyNet", "https://raw.githubusercontent.com/gproxynet/free-proxy-list/main/http.txt", ProxyProtocol.Http),
        Feed(61, "Anutmagang HTTP", "Anutmagang", "https://raw.githubusercontent.com/anutmagang/Free-HighQuality-Proxy-Socks/main/results/http.txt", ProxyProtocol.Http),
        Feed(62, "ProxRipper HTTP", "ProxRipper", "https://raw.githubusercontent.com/Mohammedcha/ProxRipper/main/full_proxies/http.txt", ProxyProtocol.Http),
        Feed(63, "RoosterKid HTTPS", "RoosterKid", "https://raw.githubusercontent.com/roosterkid/openproxylist/main/HTTPS_RAW.txt", ProxyProtocol.Https),
        Feed(64, "Proxy-Free HTTP", "Proxy-Free", "https://raw.githubusercontent.com/proxy-free/free-proxy-list/main/http.txt", ProxyProtocol.Http),
        Feed(65, "Ch4120N HTTP", "Ch4120N", "https://raw.githubusercontent.com/Ch4120N/Ch4120N-Proxy-List/master/proxies/http.txt", ProxyProtocol.Http),
        Feed(66, "XYZS996 HTTP", "XYZS996", "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/http.txt", ProxyProtocol.Http),
        Feed(67, "Tianndev Mixed", "Tianndev", "https://raw.githubusercontent.com/Tianndev/free-proxy/main/proxy/all.txt", ProxyProtocol.Http),
        Feed(68, "KangProxy HTTP", "KangProxy", "https://raw.githubusercontent.com/officialputuid/KangProxy/main/http/http.txt", ProxyProtocol.Http),
        Feed(69, "Thordata HTTP", "Thordata", "https://raw.githubusercontent.com/Thordata/awesome-free-proxy-list/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(70, "ErcinDedeoglu HTTP", "ErcinDedeoglu", "https://raw.githubusercontent.com/ErcinDedeoglu/proxies/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(71, "Skillter HTTP", "Skillter", "https://raw.githubusercontent.com/Skillter/ProxyGather/master/proxies/working-proxies-http.txt", ProxyProtocol.Http),
        Feed(72, "ClarkTM HTTP", "ClarkTM", "https://raw.githubusercontent.com/clarketm/proxy-list/master/proxy-list-raw.txt", ProxyProtocol.Http),
        Feed(73, "Sunny9577 HTTP", "Sunny9577", "https://raw.githubusercontent.com/sunny9577/proxy-scraper/master/proxies.txt", ProxyProtocol.Http),
        Feed(74, "HookzOf SOCKS5", "HookzOf", "https://raw.githubusercontent.com/hookzof/socks5_list/master/proxy.txt", ProxyProtocol.Socks5),
        Feed(75, "Vakhov HTTP", "Vakhov", "https://raw.githubusercontent.com/vakhov/fresh-proxy-list/master/http.txt", ProxyProtocol.Http),
        Feed(76, "ShiftyTR HTTP", "ShiftyTR", "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/http.txt", ProxyProtocol.Http),
        Feed(77, "Fyvri HTTP", "Fyvri", "https://raw.githubusercontent.com/fyvri/fresh-proxy-list/archive/storage/classic/http.txt", ProxyProtocol.Http),
        Feed(78, "BesJS Mixed", "BesJS", "https://raw.githubusercontent.com/Bes-js/public-proxy-list/main/proxies.txt", ProxyProtocol.Http),
        Feed(79, "TheRituRajPS HTTP", "TheRituRajPS", "https://raw.githubusercontent.com/theriturajps/proxy-list/main/proxies.txt", ProxyProtocol.Http),
        Feed(80, "Stormsia HTTP", "Stormsia", "https://raw.githubusercontent.com/stormsia/proxy-list/main/http.txt", ProxyProtocol.Http),
        Feed(81, "MrMarble Mixed", "MrMarble", "https://raw.githubusercontent.com/MrMarble/proxy-list/main/all.txt", ProxyProtocol.Http),
    ];

    private static readonly Dictionary<string, BuiltInSource> SourcesByUrl =
        Sources.ToDictionary(source => source.Url, StringComparer.Ordinal);

    /// <summary>
    /// Число независимых origin-владельцев, а не произвольных отображаемых названий.
    /// Для GitHub identity определяется owner path, для остальных feed'ов — DNS hostname.
    /// </summary>
    public static int ProviderCount { get; } = Sources.Select(source => source.ProviderIdentity)
        .Distinct(StringComparer.Ordinal).Count();

    /// <summary>Возвращает канонические метаданные только для точного встроенного endpoint.</summary>
    public static BuiltInSource? FindByUrl(string url) =>
        SourcesByUrl.TryGetValue(url, out var source) ? source : null;

    private static BuiltInSource Feed(int rank, string name, string provider, string url, ProxyProtocol protocol) =>
        new(rank, name, provider, ProviderIdentity(url), url, protocol);

    /// <summary>Канонизирует технического владельца feed endpoint для completeness-gate.</summary>
    private static string ProviderIdentity(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.IdnHost.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            var owner = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            return $"github:{owner.ToLowerInvariant()}";
        }
        return $"host:{uri.IdnHost.ToLowerInvariant()}";
    }
}

/// <summary>Неизменяемое описание одного встроенного публичного feed'а.</summary>
public sealed record BuiltInSource(
    int Rank,
    string Name,
    string Provider,
    string ProviderIdentity,
    string Url,
    ProxyProtocol Protocol);
