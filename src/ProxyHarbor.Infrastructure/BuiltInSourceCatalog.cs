using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Версионируемый каталог публичных proxy-feed endpoint'ов от 75 независимых провайдеров.</summary>
public static class BuiltInSourceCatalog
{
    /// <summary>Дата последнего полного production-аудита всех канонических feed'ов.</summary>
    public static DateOnly LastAuditedOn => new(2026, 8, 26);

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
        Feed(51, "CyberH4ck3r HTTP", "CyberH4ck3r", "https://raw.githubusercontent.com/cyberh4ck3r/free-proxy-list/main/proxies/checked/protocols/http/http-proxies.txt", ProxyProtocol.Http),
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
        Feed(80, "NotThinks Mixed", "NotThinks", "https://raw.githubusercontent.com/notthinks/proxy-lists/main/alive.txt", ProxyProtocol.Http),
        Feed(81, "MrMarble Mixed", "MrMarble", "https://raw.githubusercontent.com/MrMarble/proxy-list/main/all.txt", ProxyProtocol.Http),

        // Свежие независимые каталоги, проверенные 2026-08-24: каждый URL отдал
        // непустой текстовый список и принадлежит отдельному GitHub owner.
        Feed(82, "RelayGlass HTTP", "RelayGlass", "https://raw.githubusercontent.com/relayglass/free-proxy-list/main/protocol/http/http.txt", ProxyProtocol.Http),
        Feed(83, "RelayGlass HTTPS", "RelayGlass", "https://raw.githubusercontent.com/relayglass/free-proxy-list/main/protocol/https/https.txt", ProxyProtocol.Https),
        Feed(84, "RelayGlass SOCKS4", "RelayGlass", "https://raw.githubusercontent.com/relayglass/free-proxy-list/main/protocol/socks4/socks4.txt", ProxyProtocol.Socks4),
        Feed(85, "RelayGlass SOCKS5", "RelayGlass", "https://raw.githubusercontent.com/relayglass/free-proxy-list/main/protocol/socks5/socks5.txt", ProxyProtocol.Socks5),
        Feed(86, "ProxyMan HTTP", "ProxyMan", "https://raw.githubusercontent.com/Akshay7273/ProxyMan-free-proxy-list/main/protocols/http.txt", ProxyProtocol.Http),
        Feed(87, "ProxyMan SOCKS4", "ProxyMan", "https://raw.githubusercontent.com/Akshay7273/ProxyMan-free-proxy-list/main/protocols/socks4.txt", ProxyProtocol.Socks4),
        Feed(88, "ProxyMan SOCKS5", "ProxyMan", "https://raw.githubusercontent.com/Akshay7273/ProxyMan-free-proxy-list/main/protocols/socks5.txt", ProxyProtocol.Socks5),
        Feed(89, "Dinoz HTTP", "Dinoz", "https://raw.githubusercontent.com/dinoz0rg/proxy-list/main/checked_proxies/http.txt", ProxyProtocol.Http),
        Feed(90, "Dinoz SOCKS4", "Dinoz", "https://raw.githubusercontent.com/dinoz0rg/proxy-list/main/checked_proxies/socks4.txt", ProxyProtocol.Socks4),
        Feed(91, "Dinoz SOCKS5", "Dinoz", "https://raw.githubusercontent.com/dinoz0rg/proxy-list/main/checked_proxies/socks5.txt", ProxyProtocol.Socks5),
        Feed(92, "Mzyui HTTP", "Mzyui", "https://raw.githubusercontent.com/mzyui/proxy-list/main/http.txt", ProxyProtocol.Http),
        Feed(93, "Mzyui SOCKS4", "Mzyui", "https://raw.githubusercontent.com/mzyui/proxy-list/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(94, "Mzyui SOCKS5", "Mzyui", "https://raw.githubusercontent.com/mzyui/proxy-list/main/socks5.txt", ProxyProtocol.Socks5),
        Feed(95, "Naravid HTTP", "Naravid", "https://raw.githubusercontent.com/naravid19/checked-proxies/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(96, "Naravid SOCKS4", "Naravid", "https://raw.githubusercontent.com/naravid19/checked-proxies/main/proxies/socks4.txt", ProxyProtocol.Socks4),
        Feed(97, "Naravid SOCKS5", "Naravid", "https://raw.githubusercontent.com/naravid19/checked-proxies/main/proxies/socks5.txt", ProxyProtocol.Socks5),
        Feed(98, "aQuiner HTTP", "aQuiner", "https://raw.githubusercontent.com/aQuiner/free-proxy-list/main/http.txt", ProxyProtocol.Http),

        // Сто новых endpoint'ов от 19 дополнительных origin-владельцев. Все адреса
        // проверены 2026-08-26: HTTPS 200 и хотя бы один валидный IP:port в body.
        Feed(99, "Sevenworks HTTP", "Sevenworks", "https://raw.githubusercontent.com/SevenworksDev/proxy-list/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(100, "Sevenworks HTTPS", "Sevenworks", "https://raw.githubusercontent.com/SevenworksDev/proxy-list/main/proxies/https.txt", ProxyProtocol.Https),
        Feed(101, "Sevenworks SOCKS4", "Sevenworks", "https://raw.githubusercontent.com/SevenworksDev/proxy-list/main/proxies/socks4.txt", ProxyProtocol.Socks4),
        Feed(102, "Sevenworks SOCKS5", "Sevenworks", "https://raw.githubusercontent.com/SevenworksDev/proxy-list/main/proxies/socks5.txt", ProxyProtocol.Socks5),
        Feed(103, "HideIP HTTP", "HideIP", "https://raw.githubusercontent.com/zloi-user/hideip.me/main/http.txt", ProxyProtocol.Http),
        Feed(104, "HideIP HTTPS", "HideIP", "https://raw.githubusercontent.com/zloi-user/hideip.me/main/https.txt", ProxyProtocol.Https),
        Feed(105, "HideIP SOCKS4", "HideIP", "https://raw.githubusercontent.com/zloi-user/hideip.me/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(106, "HideIP SOCKS5", "HideIP", "https://raw.githubusercontent.com/zloi-user/hideip.me/main/socks5.txt", ProxyProtocol.Socks5),
        Feed(107, "Tsprnay HTTP", "Tsprnay", "https://raw.githubusercontent.com/Tsprnay/Proxy-lists/master/proxies/http.txt", ProxyProtocol.Http),
        Feed(108, "Tsprnay HTTPS", "Tsprnay", "https://raw.githubusercontent.com/Tsprnay/Proxy-lists/master/proxies/https.txt", ProxyProtocol.Https),
        Feed(109, "Tsprnay SOCKS4", "Tsprnay", "https://raw.githubusercontent.com/Tsprnay/Proxy-lists/master/proxies/socks4.txt", ProxyProtocol.Socks4),
        Feed(110, "Tsprnay SOCKS5", "Tsprnay", "https://raw.githubusercontent.com/Tsprnay/Proxy-lists/master/proxies/socks5.txt", ProxyProtocol.Socks5),
        Feed(111, "ALIILAPRO HTTP", "ALIILAPRO", "https://raw.githubusercontent.com/ALIILAPRO/Proxy/main/http.txt", ProxyProtocol.Http),
        Feed(112, "ALIILAPRO SOCKS4", "ALIILAPRO", "https://raw.githubusercontent.com/ALIILAPRO/Proxy/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(113, "ALIILAPRO SOCKS5", "ALIILAPRO", "https://raw.githubusercontent.com/ALIILAPRO/Proxy/main/socks5.txt", ProxyProtocol.Socks5),
        Feed(114, "NikolaiT HTTP", "NikolaiT", "https://raw.githubusercontent.com/NikolaiT/free-proxy-list/main/proxies/http_working.txt", ProxyProtocol.Http),
        Feed(115, "NikolaiT HTTPS", "NikolaiT", "https://raw.githubusercontent.com/NikolaiT/free-proxy-list/main/proxies/https_working.txt", ProxyProtocol.Https),
        Feed(116, "NikolaiT SOCKS4", "NikolaiT", "https://raw.githubusercontent.com/NikolaiT/free-proxy-list/main/proxies/socks4_working.txt", ProxyProtocol.Socks4),
        Feed(117, "NikolaiT SOCKS5", "NikolaiT", "https://raw.githubusercontent.com/NikolaiT/free-proxy-list/main/proxies/socks5_working.txt", ProxyProtocol.Socks5),
        Feed(118, "VannDev HTTP", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(119, "VannDev HTTPS", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/https.txt", ProxyProtocol.Https),
        Feed(120, "VannDev SOCKS4", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/socks4.txt", ProxyProtocol.Socks4),
        Feed(121, "VannDev SOCKS5", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/socks5.txt", ProxyProtocol.Socks5),
        Feed(122, "VannDev HTTP discord", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http-tested/discord.txt", ProxyProtocol.Http),
        Feed(123, "VannDev HTTP facebook", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http-tested/facebook.txt", ProxyProtocol.Http),
        Feed(124, "VannDev HTTP google", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http-tested/google.txt", ProxyProtocol.Http),
        Feed(125, "VannDev HTTP instagram", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http-tested/instagram.txt", ProxyProtocol.Http),
        Feed(126, "VannDev HTTP microsoft", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http-tested/microsoft.txt", ProxyProtocol.Http),
        Feed(127, "VannDev HTTP tiktok", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http-tested/tiktok.txt", ProxyProtocol.Http),
        Feed(128, "VannDev HTTP twitter", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http-tested/twitter.txt", ProxyProtocol.Http),
        Feed(129, "VannDev HTTP whatsapp", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http-tested/whatsapp.txt", ProxyProtocol.Http),
        Feed(130, "VannDev HTTP youtube", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/http-tested/youtube.txt", ProxyProtocol.Http),
        Feed(131, "VannDev HTTPS discord", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/https-tested/discord.txt", ProxyProtocol.Https),
        Feed(132, "VannDev HTTPS facebook", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/https-tested/facebook.txt", ProxyProtocol.Https),
        Feed(133, "VannDev HTTPS google", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/https-tested/google.txt", ProxyProtocol.Https),
        Feed(134, "VannDev HTTPS instagram", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/https-tested/instagram.txt", ProxyProtocol.Https),
        Feed(135, "VannDev HTTPS microsoft", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/https-tested/microsoft.txt", ProxyProtocol.Https),
        Feed(136, "VannDev HTTPS tiktok", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/https-tested/tiktok.txt", ProxyProtocol.Https),
        Feed(137, "VannDev HTTPS whatsapp", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/https-tested/whatsapp.txt", ProxyProtocol.Https),
        Feed(138, "VannDev HTTPS youtube", "VannDev", "https://raw.githubusercontent.com/Vann-Dev/proxy-list/main/proxies/https-tested/youtube.txt", ProxyProtocol.Https),
        Feed(139, "SoliSpirit HTTP", "SoliSpirit", "https://raw.githubusercontent.com/SoliSpirit/proxy-list/main/http.txt", ProxyProtocol.Http),
        Feed(140, "SoliSpirit HTTPS", "SoliSpirit", "https://raw.githubusercontent.com/SoliSpirit/proxy-list/main/https.txt", ProxyProtocol.Https),
        Feed(141, "SoliSpirit SOCKS4", "SoliSpirit", "https://raw.githubusercontent.com/SoliSpirit/proxy-list/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(142, "SoliSpirit SOCKS5", "SoliSpirit", "https://raw.githubusercontent.com/SoliSpirit/proxy-list/main/socks5.txt", ProxyProtocol.Socks5),
        Feed(143, "Elliottophellia HTTP", "Elliottophellia", "https://raw.githubusercontent.com/elliottophellia/proxylist/master/results/http/global/http_checked.txt", ProxyProtocol.Http),
        Feed(144, "Elliottophellia HTTP scheme", "Elliottophellia", "https://raw.githubusercontent.com/elliottophellia/proxylist/master/results/http/global/phttp_checked.txt", ProxyProtocol.Http),
        Feed(145, "Elliottophellia SOCKS4", "Elliottophellia", "https://raw.githubusercontent.com/elliottophellia/proxylist/master/results/socks4/global/socks4_checked.txt", ProxyProtocol.Socks4),
        Feed(146, "Elliottophellia SOCKS4 scheme", "Elliottophellia", "https://raw.githubusercontent.com/elliottophellia/proxylist/master/results/socks4/global/psocks4_checked.txt", ProxyProtocol.Socks4),
        Feed(147, "Elliottophellia SOCKS5", "Elliottophellia", "https://raw.githubusercontent.com/elliottophellia/proxylist/master/results/socks5/global/socks5_checked.txt", ProxyProtocol.Socks5),
        Feed(148, "Elliottophellia SOCKS5 scheme", "Elliottophellia", "https://raw.githubusercontent.com/elliottophellia/proxylist/master/results/socks5/global/psocks5_checked.txt", ProxyProtocol.Socks5),
        Feed(149, "CB-X2 Mixed", "CB-X2", "https://raw.githubusercontent.com/CB-X2-Jun/proxy-lists/main/proxy.txt", ProxyProtocol.Http),
        Feed(150, "HendrikBGR Mixed", "HendrikBGR", "https://raw.githubusercontent.com/hendrikbgr/Free-Proxy-Repo/master/proxy_list.txt", ProxyProtocol.Http),
        Feed(151, "NoArche HTTP", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/http-online.txt", ProxyProtocol.Http),
        Feed(152, "NoArche CONNECT", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/connect-online.txt", ProxyProtocol.Https),
        Feed(153, "NoArche SOCKS4", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/socks4-online.txt", ProxyProtocol.Socks4),
        Feed(154, "NoArche SOCKS5", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/socks5-online.txt", ProxyProtocol.Socks5),
        Feed(155, "NoArche Mixed", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/mixed-online.txt", ProxyProtocol.Http),
        Feed(156, "ProxyGenerator MostStable Mixed", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/MostStable/ALL.txt", ProxyProtocol.Http),
        Feed(157, "ProxyGenerator MostStable HTTP", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/MostStable/http.txt", ProxyProtocol.Http),
        Feed(158, "ProxyGenerator MostStable SOCKS4", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/MostStable/socks4.txt", ProxyProtocol.Socks4),
        Feed(159, "ProxyGenerator MostStable SOCKS5", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/MostStable/socks5.txt", ProxyProtocol.Socks5),
        Feed(160, "ProxyGenerator Stable Mixed", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/Stable/ALL.txt", ProxyProtocol.Http),
        Feed(161, "ProxyGenerator Stable HTTP", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/Stable/http.txt", ProxyProtocol.Http),
        Feed(162, "ProxyGenerator Stable HTTPS", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/Stable/https.txt", ProxyProtocol.Https),
        Feed(163, "ProxyGenerator Stable SOCKS4", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/Stable/socks4.txt", ProxyProtocol.Socks4),
        Feed(164, "ProxyGenerator Stable SOCKS5", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/Stable/socks5.txt", ProxyProtocol.Socks5),
        Feed(165, "ProxyGenerator ChatGPT Mixed", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/chatgpt.com/ALL.txt", ProxyProtocol.Http),
        Feed(166, "ProxyGenerator ChatGPT HTTP", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/chatgpt.com/http.txt", ProxyProtocol.Http),
        Feed(167, "ProxyGenerator ChatGPT HTTPS", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/chatgpt.com/https.txt", ProxyProtocol.Https),
        Feed(168, "ProxyGenerator ChatGPT SOCKS4", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/chatgpt.com/socks4.txt", ProxyProtocol.Socks4),
        Feed(169, "ProxyGenerator ChatGPT SOCKS5", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/chatgpt.com/socks5.txt", ProxyProtocol.Socks5),
        Feed(170, "ProxyGenerator Google Mixed", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/google.com/ALL.txt", ProxyProtocol.Http),
        Feed(171, "ProxyGenerator Google HTTP", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/google.com/http.txt", ProxyProtocol.Http),
        Feed(172, "ProxyGenerator Google HTTPS", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/google.com/https.txt", ProxyProtocol.Https),
        Feed(173, "ProxyGenerator Google SOCKS4", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/google.com/socks4.txt", ProxyProtocol.Socks4),
        Feed(174, "ProxyGenerator Google SOCKS5", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/google.com/socks5.txt", ProxyProtocol.Socks5),
        Feed(175, "Seeh-Saah Mixed", "Seeh-Saah", "https://raw.githubusercontent.com/Seeh-Saah/awesome-free-proxy-list/main/proxies/all.txt", ProxyProtocol.Http),
        Feed(176, "Seeh-Saah HTTP", "Seeh-Saah", "https://raw.githubusercontent.com/Seeh-Saah/awesome-free-proxy-list/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(177, "Seeh-Saah HTTPS", "Seeh-Saah", "https://raw.githubusercontent.com/Seeh-Saah/awesome-free-proxy-list/main/proxies/https.txt", ProxyProtocol.Https),
        Feed(178, "Seeh-Saah SOCKS4", "Seeh-Saah", "https://raw.githubusercontent.com/Seeh-Saah/awesome-free-proxy-list/main/proxies/socks4.txt", ProxyProtocol.Socks4),
        Feed(179, "Seeh-Saah SOCKS5", "Seeh-Saah", "https://raw.githubusercontent.com/Seeh-Saah/awesome-free-proxy-list/main/proxies/socks5.txt", ProxyProtocol.Socks5),
        Feed(180, "7and1 HTTP", "7and1", "https://raw.githubusercontent.com/7and1/free-proxy-list/main/proxies/protocols/http/data.txt", ProxyProtocol.Http),
        Feed(181, "7and1 SOCKS4", "7and1", "https://raw.githubusercontent.com/7and1/free-proxy-list/main/proxies/protocols/socks4/data.txt", ProxyProtocol.Socks4),
        Feed(182, "7and1 SOCKS5", "7and1", "https://raw.githubusercontent.com/7and1/free-proxy-list/main/proxies/protocols/socks5/data.txt", ProxyProtocol.Socks5),
        Feed(183, "TomJiu HTTP", "TomJiu", "https://raw.githubusercontent.com/tomjiu/proxy-pipeline/main/dist/online/http.txt", ProxyProtocol.Http),
        Feed(184, "TomJiu Mixed", "TomJiu", "https://raw.githubusercontent.com/tomjiu/proxy-pipeline/main/dist/online/all.txt", ProxyProtocol.Http),
        Feed(185, "TomJiu SOCKS4", "TomJiu", "https://raw.githubusercontent.com/tomjiu/proxy-pipeline/main/dist/online/socks4.txt", ProxyProtocol.Socks4),
        Feed(186, "TomJiu SOCKS5", "TomJiu", "https://raw.githubusercontent.com/tomjiu/proxy-pipeline/main/dist/online/socks5.txt", ProxyProtocol.Socks5),
        Feed(187, "GHSTFACES Mixed", "GHSTFACES", "https://raw.githubusercontent.com/GHSTFACES/PL/main/all.txt", ProxyProtocol.Http),
        Feed(188, "GHSTFACES HTTP", "GHSTFACES", "https://raw.githubusercontent.com/GHSTFACES/PL/main/http.txt", ProxyProtocol.Http),
        Feed(189, "GHSTFACES HTTPS", "GHSTFACES", "https://raw.githubusercontent.com/GHSTFACES/PL/main/https.txt", ProxyProtocol.Https),
        Feed(190, "GHSTFACES SOCKS4", "GHSTFACES", "https://raw.githubusercontent.com/GHSTFACES/PL/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(191, "GHSTFACES SOCKS5", "GHSTFACES", "https://raw.githubusercontent.com/GHSTFACES/PL/main/socks5.txt", ProxyProtocol.Socks5),
        Feed(192, "Andigwandi Mixed", "Andigwandi", "https://raw.githubusercontent.com/andigwandi/free-proxy/main/proxy_list.txt", ProxyProtocol.Http),
        Feed(193, "KevinRiver Mixed", "KevinRiver", "https://raw.githubusercontent.com/kevinriverrrr-sudo/free-proxy-list/main/proxies/all.txt", ProxyProtocol.Http),
        Feed(194, "KevinRiver HTTP", "KevinRiver", "https://raw.githubusercontent.com/kevinriverrrr-sudo/free-proxy-list/main/proxies/http.txt", ProxyProtocol.Http),
        Feed(195, "KevinRiver SOCKS5", "KevinRiver", "https://raw.githubusercontent.com/kevinriverrrr-sudo/free-proxy-list/main/proxies/socks5.txt", ProxyProtocol.Socks5),
        Feed(196, "Xnuvers Active", "Xnuvers", "https://raw.githubusercontent.com/Xnuvers007/free-proxy/main/proxy_active.txt", ProxyProtocol.Http),
        Feed(197, "Xnuvers Scheme mixed", "Xnuvers", "https://raw.githubusercontent.com/Xnuvers007/free-proxy/main/proxy_scheme.txt", ProxyProtocol.Http),
        Feed(198, "Xnuvers Scheme active", "Xnuvers", "https://raw.githubusercontent.com/Xnuvers007/free-proxy/main/proxy_scheme_active.txt", ProxyProtocol.Http),
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
