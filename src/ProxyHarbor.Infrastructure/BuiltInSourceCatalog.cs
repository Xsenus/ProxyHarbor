using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Версионируемый каталог публичных proxy-feed endpoint'ов от 85 независимых провайдеров.</summary>
public static class BuiltInSourceCatalog
{
    /// <summary>Дата последнего полного URL/live-аудита всех канонических feed'ов.</summary>
    public static DateOnly LastAuditedOn => new(2026, 9, 2);

    /// <summary>Источники ранжированы по свежести, объёму, стабильности ответа и разнообразию провайдеров.</summary>
    public static IReadOnlyList<BuiltInSource> Sources { get; } = RankSources(
    [
        // HTTP/HTTPS и смешанные feed'ы.
        Feed(1, "ProxyScrape V4 Mixed", "ProxyScrape", "https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=text", ProxyProtocol.Http),
        Feed(2, "ProxyScrape V2 HTTP", "ProxyScrape", "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=http", ProxyProtocol.Http),
        Feed(3, "OpenProxyList HTTP", "OpenProxyList", "https://openproxylist.xyz/http.txt", ProxyProtocol.Http),
        Feed(4, "OpenProxyList HTTPS", "OpenProxyList", "https://openproxylist.xyz/https.txt", ProxyProtocol.Https),
        Feed(5, "Proxifly HTTP", "Proxifly", "https://raw.githubusercontent.com/proxifly/free-proxy-list/refs/heads/main/proxies/protocols/http/data.txt", ProxyProtocol.Http),
        Feed(6, "Proxifly HTTPS", "Proxifly", "https://raw.githubusercontent.com/proxifly/free-proxy-list/refs/heads/main/proxies/protocols/https/data.txt", ProxyProtocol.Https),
        Feed(7, "TheSpeedX HTTP", "TheSpeedX", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt", ProxyProtocol.Http),
        Feed(8, "IPLocate HTTP", "IPLocate", "https://raw.githubusercontent.com/iplocate/free-proxy-list/refs/heads/main/protocols/http.txt", ProxyProtocol.Http),
        Feed(9, "Databay HTTP", "Databay Labs", "https://raw.githubusercontent.com/databay-labs/free-proxy-list/master/http.txt", ProxyProtocol.Http),
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
        Feed(26, "TheSpeedX SOCKS4", "TheSpeedX", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks4.txt", ProxyProtocol.Socks4),
        Feed(27, "IPLocate SOCKS4", "IPLocate", "https://raw.githubusercontent.com/iplocate/free-proxy-list/refs/heads/main/protocols/socks4.txt", ProxyProtocol.Socks4),
        Feed(28, "Databay SOCKS4", "Databay Labs", "https://raw.githubusercontent.com/databay-labs/free-proxy-list/master/socks4.txt", ProxyProtocol.Socks4),
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
        Feed(40, "TheSpeedX SOCKS5", "TheSpeedX", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt", ProxyProtocol.Socks5),
        Feed(41, "IPLocate SOCKS5", "IPLocate", "https://raw.githubusercontent.com/iplocate/free-proxy-list/refs/heads/main/protocols/socks5.txt", ProxyProtocol.Socks5),
        Feed(42, "Databay SOCKS5", "Databay Labs", "https://raw.githubusercontent.com/databay-labs/free-proxy-list/master/socks5.txt", ProxyProtocol.Socks5),
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
        Feed(77, "Fyvri HTTP", "Fyvri", "https://raw.githubusercontent.com/fyvri/fresh-proxy-list/refs/heads/archive/storage/classic/http.txt", ProxyProtocol.Http),
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
        Feed(103, "Zevtyardt HTTP", "Zevtyardt", "https://raw.githubusercontent.com/zevtyardt/proxy-list/main/http.txt", ProxyProtocol.Http),
        Feed(104, "Zevtyardt Mixed", "Zevtyardt", "https://raw.githubusercontent.com/zevtyardt/proxy-list/main/all.txt", ProxyProtocol.Http),
        Feed(105, "Zevtyardt SOCKS4", "Zevtyardt", "https://raw.githubusercontent.com/zevtyardt/proxy-list/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(106, "Zevtyardt SOCKS5", "Zevtyardt", "https://raw.githubusercontent.com/zevtyardt/proxy-list/main/socks5.txt", ProxyProtocol.Socks5),
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
        Feed(149, "TheMiralay Mixed", "TheMiralay", "https://raw.githubusercontent.com/themiralay/Proxy-List-World/master/data.txt", ProxyProtocol.Http),
        Feed(150, "HendrikBGR Mixed", "HendrikBGR", "https://raw.githubusercontent.com/hendrikbgr/Free-Proxy-Repo/master/proxy_list.txt", ProxyProtocol.Http),
        Feed(151, "NoArche HTTP", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/http-online.txt", ProxyProtocol.Http),
        Feed(152, "NoArche CONNECT", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/connect-online.txt", ProxyProtocol.Https),
        Feed(153, "NoArche SOCKS4", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/socks4-online.txt", ProxyProtocol.Socks4),
        Feed(154, "NoArche SOCKS5", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/socks5-online.txt", ProxyProtocol.Socks5),
        Feed(155, "NoArche Mixed", "NoArche", "https://raw.githubusercontent.com/noarche/proxylist-socks5-sock4-exported-updates/main/mixed-online.txt", ProxyProtocol.Http),
        Feed(156, "ProxyGenerator MostStable Mixed", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/MostStable/ALL.txt", ProxyProtocol.Http),
        Feed(157, "ProxyGenerator MostStable HTTP", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/MostStable/http.txt", ProxyProtocol.Http),
        Feed(158, "ProxyGenerator Cloudflare SOCKS4", "ProxyGenerator", "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/cloudflare.com/socks4.txt", ProxyProtocol.Socks4),
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
        Feed(180, "7and1 HTTP", "7and1", "https://raw.githubusercontent.com/7and1/free-proxy-list/main/proxies/protocols/http/data.txt", ProxyProtocol.Http),
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

        // Используем только агрегированные XYZS996 feed'ы. Country-файлы этого
        // генератора исчезают при временно пустой стране и потому не являются
        // стабильными независимыми источниками; их данные уже входят в all/http/https.
        Feed(199, "XYZS996 All", "XYZS996", "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/all.txt", ProxyProtocol.Http),
        Feed(201, "XYZS996 HTTPS", "XYZS996", "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/https.txt", ProxyProtocol.Https),
        Feed(242, "Proxio Mixed", "Proxio", "https://raw.githubusercontent.com/proxio-io/proxy-list/main/all.txt", ProxyProtocol.Http),
        Feed(252, "Proxio HTTP", "Proxio", "https://raw.githubusercontent.com/proxio-io/proxy-list/main/http.txt", ProxyProtocol.Http),
        Feed(253, "Syscallh00k Mixed", "Syscallh00k", "https://raw.githubusercontent.com/Syscallh00k/proxy-list/main/all.txt", ProxyProtocol.Http),
        Feed(256, "Gifted Proxies HTTP", "Gifted Proxies", "https://raw.githubusercontent.com/mauricegift/free-proxies/master/files/http.json", ProxyProtocol.Http),
        Feed(257, "Gifted Proxies SOCKS4", "Gifted Proxies", "https://raw.githubusercontent.com/mauricegift/free-proxies/master/files/socks4.json", ProxyProtocol.Socks4),
        Feed(263, "Pxys Daily CSV", "Pxys", "https://raw.githubusercontent.com/Pxys-io/DailyProxyList/master/working_proxies.csv", ProxyProtocol.Http),
        Feed(269, "ProxRipper HTTPS", "ProxRipper", "https://raw.githubusercontent.com/Mohammedcha/ProxRipper/main/full_proxies/https.txt", ProxyProtocol.Https),
        Feed(270, "ProxRipper SOCKS4", "ProxRipper", "https://raw.githubusercontent.com/Mohammedcha/ProxRipper/main/full_proxies/socks4.txt", ProxyProtocol.Socks4),
        Feed(271, "ProxRipper SOCKS5", "ProxRipper", "https://raw.githubusercontent.com/Mohammedcha/ProxRipper/main/full_proxies/socks5.txt", ProxyProtocol.Socks5),
        Feed(273, "Tianndev HTTP", "Tianndev", "https://raw.githubusercontent.com/Tianndev/free-proxy/main/proxy/http.txt", ProxyProtocol.Http),
        Feed(274, "Tianndev HTTPS", "Tianndev", "https://raw.githubusercontent.com/Tianndev/free-proxy/main/proxy/https.txt", ProxyProtocol.Https),
        Feed(275, "Tianndev SOCKS4", "Tianndev", "https://raw.githubusercontent.com/Tianndev/free-proxy/main/proxy/socks4.txt", ProxyProtocol.Socks4),
        Feed(276, "Tianndev SOCKS5", "Tianndev", "https://raw.githubusercontent.com/Tianndev/free-proxy/main/proxy/socks5.txt", ProxyProtocol.Socks5),
        Feed(277, "HProxy All", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/all.txt", ProxyProtocol.Http),
        Feed(278, "HProxy HTTPS", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/https.txt", ProxyProtocol.Https),
        Feed(279, "HProxy US", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/US.txt", ProxyProtocol.Http),
        Feed(280, "HProxy DE", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/DE.txt", ProxyProtocol.Http),
        Feed(281, "HProxy FR", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/FR.txt", ProxyProtocol.Http),
        Feed(282, "HProxy GB", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/GB.txt", ProxyProtocol.Http),
        Feed(283, "HProxy NL", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/NL.txt", ProxyProtocol.Http),
        Feed(284, "HProxy CA", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/CA.txt", ProxyProtocol.Http),
        Feed(285, "HProxy SG", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/SG.txt", ProxyProtocol.Http),
        Feed(286, "HProxy JP", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/JP.txt", ProxyProtocol.Http),
        Feed(287, "HProxy KR", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/KR.txt", ProxyProtocol.Http),
        Feed(288, "HProxy IN", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/IN.txt", ProxyProtocol.Http),
        Feed(289, "HProxy BR", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/BR.txt", ProxyProtocol.Http),
        Feed(290, "HProxy AU", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/AU.txt", ProxyProtocol.Http),
        Feed(291, "HProxy CH", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/CH.txt", ProxyProtocol.Http),
        Feed(292, "HProxy SE", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/SE.txt", ProxyProtocol.Http),
        Feed(293, "HProxy NO", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/NO.txt", ProxyProtocol.Http),
        Feed(294, "HProxy FI", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/FI.txt", ProxyProtocol.Http),
        Feed(295, "HProxy PL", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/PL.txt", ProxyProtocol.Http),
        Feed(296, "HProxy IT", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/IT.txt", ProxyProtocol.Http),
        Feed(297, "HProxy ES", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/ES.txt", ProxyProtocol.Http),
        Feed(298, "HProxy CZ", "HProxy", "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/CZ.txt", ProxyProtocol.Http),

        // Новые независимые владельцы, найденные и проверенные 28.08.2026.
        // Каждый URL отвечает по HTTPS и содержит хотя бы один разбираемый IP:port.
        Feed(299, "Proxio HTTPS", "Proxio", "https://raw.githubusercontent.com/proxio-io/proxy-list/main/https.txt", ProxyProtocol.Https),
        Feed(300, "Proxio SOCKS4", "Proxio", "https://raw.githubusercontent.com/proxio-io/proxy-list/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(301, "Proxio SOCKS5", "Proxio", "https://raw.githubusercontent.com/proxio-io/proxy-list/main/socks5.txt", ProxyProtocol.Socks5),
        Feed(302, "Azest Kings Crown Mixed", "Azest Kings Crown", "https://raw.githubusercontent.com/azestkingscrown/Free_Proxy_List/main/working_proxies.txt", ProxyProtocol.Http),
        Feed(303, "Pxys Daily Mixed", "Pxys", "https://raw.githubusercontent.com/Pxys-io/DailyProxyList/master/working_proxies.txt", ProxyProtocol.Http),
        Feed(304, "Syscallh00k HTTP", "Syscallh00k", "https://raw.githubusercontent.com/Syscallh00k/proxy-list/main/http.txt", ProxyProtocol.Http),
        Feed(305, "Syscallh00k HTTPS", "Syscallh00k", "https://raw.githubusercontent.com/Syscallh00k/proxy-list/main/https.txt", ProxyProtocol.Https),
        Feed(306, "Syscallh00k SOCKS4", "Syscallh00k", "https://raw.githubusercontent.com/Syscallh00k/proxy-list/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(307, "Syscallh00k SOCKS5", "Syscallh00k", "https://raw.githubusercontent.com/Syscallh00k/proxy-list/main/socks5.txt", ProxyProtocol.Socks5),
        Feed(308, "Free Proxy API HTTP", "Free Proxy API List", "https://raw.githubusercontent.com/gnxD3RfTT2WE/free-proxy-api-list/main/http.txt", ProxyProtocol.Http),
        Feed(309, "Free Proxy API SOCKS4", "Free Proxy API List", "https://raw.githubusercontent.com/gnxD3RfTT2WE/free-proxy-api-list/main/socks4.txt", ProxyProtocol.Socks4),
        Feed(310, "Free Proxy API SOCKS5", "Free Proxy API List", "https://raw.githubusercontent.com/gnxD3RfTT2WE/free-proxy-api-list/main/socks5.txt", ProxyProtocol.Socks5),

        // Независимые владельцы, повторно проверенные 01.09.2026. Каталог включает
        // только файлы с явной открытой лицензией и фактическим IP:port содержимым.
        Feed(311, "Gifted Proxies SOCKS5", "Gifted Proxies", "https://raw.githubusercontent.com/mauricegift/free-proxies/master/files/socks5.json", ProxyProtocol.Socks5),
        Feed(315, "Firmfox HTTP", "Firmfox", "https://raw.githubusercontent.com/Firmfox/Proxify/main/proxy/http.txt", ProxyProtocol.Http),
        Feed(316, "Firmfox HTTPS", "Firmfox", "https://raw.githubusercontent.com/Firmfox/Proxify/main/proxy/https.txt", ProxyProtocol.Https),
        Feed(317, "Firmfox SOCKS4", "Firmfox", "https://raw.githubusercontent.com/Firmfox/Proxify/main/proxy/socks4.txt", ProxyProtocol.Socks4),
        Feed(318, "Firmfox SOCKS5", "Firmfox", "https://raw.githubusercontent.com/Firmfox/Proxify/main/proxy/socks5.txt", ProxyProtocol.Socks5),
        Feed(319, "Berkay Digital Mixed", "Berkay Digital", "https://raw.githubusercontent.com/berkay-digital/Proxy-Scraper/main/proxies.txt", ProxyProtocol.Http),
        Feed(320, "Volkan Auto Proxy Mixed", "Volkan Auto Proxy", "https://raw.githubusercontent.com/VolkanSah/Auto-Proxy-Fetcher/main/proxies.txt", ProxyProtocol.Http),

        // Сто новых country-feed, live-проверенных с production VPS 10.09.2026.
        // 71 endpoint ориентирован на СНГ и Европу; остальные расширяют fallback-географию.
        .. RegionalCountryFeeds(),

        // Независимые GitHub-владельцы, найденные и content-проверенные 10.09.2026.
        // У восьми репозиториев GitHub распознаёт открытую лицензию; остальные 23 публикуют
        // общедоступный raw-feed без распознанного SPDX identifier.
        .. IndependentProviderFeeds(),

        // Вторая поисковая волна. Два синхронных mirror-owner исключены после byte-identical
        // сравнения; эти три endpoint принадлежат самостоятельным владельцам.
        .. ExtendedIndependentProviderFeeds(),

        // Длинный хвост независимых владельцев из страниц 2-10 GitHub Search. Из результата
        // удалены две byte-identical mirror-копии; каждый оставшийся owner представлен один раз.
        .. PaginatedIndependentProviderFeeds(),

        // Четвёртая волна: исключены byte-identical копия существующего feed и bot-mirror owner.
        .. FreeSearchIndependentProviderFeeds(),

        // Пятая волна: рабочие proxy-checker feeds из длинного хвоста GitHub Search.
        .. WorkingSearchIndependentProviderFeeds(),

        // Независимый web-origin из curated source-list, live-проверенный 10.09.2026.
        Feed(591, "CyberGateway HTTP", "CyberGateway", "https://cyber-gateway.net/get-proxy/free-proxy/24-free-http-proxy", ProxyProtocol.Http),

        // Седьмая волна: GitHub topic proxy-list, без известной bot-mirror сети.
        .. ProxyListTopicIndependentProviderFeeds(),

        // Восьмая волна: широкий GitHub topic proxies, mirror-owner исключён.
        Feed(607, "Riz4d Mixed", "riz4d", "https://raw.githubusercontent.com/riz4d/MineProxy/main/proxies.txt", ProxyProtocol.Http),
        Feed(608, "2cz5 Mixed", "2cz5", "https://raw.githubusercontent.com/2cz5/ipv4-Proxies/main/proxies.txt", ProxyProtocol.Http),
        Feed(609, "Gingteam Mixed", "gingteam", "https://raw.githubusercontent.com/gingteam/proxy-scraper/main/proxies.txt", ProxyProtocol.Http),

        // Десятая волна: специализированный http-proxy topic.
        Feed(610, "SCZ0x Mixed", "scz0x", "https://raw.githubusercontent.com/scz0x/SCZ0x-Proxy/main/proxies.txt", ProxyProtocol.Http),

        // Финальная волна: десять наиболее содержательных уникальных proxy-scraper feeds.
        .. ProxyScraperSearchIndependentProviderFeeds(),
    ]);

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

    private static BuiltInSource[] RankSources(IEnumerable<BuiltInSource> sources) =>
        sources.Select((source, index) => source with { Rank = index + 1 }).ToArray();

    private static IEnumerable<BuiltInSource> RegionalCountryFeeds()
    {
        const string hProxyCountries =
            "RU UA BY KZ AM AZ GE KG MD UZ AL AT BA BE BG CY DK EE GR HR HU IE LT LV ME PT RO RS SI SK TR XK " +
            "AE AF AO AR BD BF BI BJ BO BT BW CD CG CI CL CM CN CO CR DO DZ EC EG GA GH GM GQ GT HK";
        const string proxiflyCountries =
            "RU UA KZ AM GE MD UZ AL AT BG CH CY DE DK EE ES FI FR GB GR HR HU IE IT LT LU LV ME NL NO PL PT RO RS SE SI SK TR CZ";

        var rank = 321;
        foreach (var country in hProxyCountries.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            yield return Feed(rank++, $"HProxy country {country}", "HProxy",
                $"https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/{country}.txt",
                ProxyProtocol.Http);
        foreach (var country in proxiflyCountries.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            yield return Feed(rank++, $"Proxifly country {country}", "Proxifly",
                $"https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/countries/{country}/data.txt",
                ProxyProtocol.Http);
    }

    private static IEnumerable<BuiltInSource> IndependentProviderFeeds()
    {
        var rank = 421;
        yield return Feed(rank++, "Just Not Google HTTP", "just-not-google", "https://raw.githubusercontent.com/just-not-google/full-free-proxy/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "NDT Proxy Scraper HTTP", "nguyenduytan", "https://raw.githubusercontent.com/nguyenduytan/NDT-Proxy-Scraper/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "ProxyScraper HTTP", "ProxyScraper", "https://raw.githubusercontent.com/ProxyScraper/ProxyScraper/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Proxy List Gamt HTTP", "Denisyoya", "https://raw.githubusercontent.com/Denisyoya/Proxy-List-Gamt/main/proxy/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "MetaFetch Mixed", "lanzm", "https://raw.githubusercontent.com/lanzm/MetaFetch/master/list.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Stormsia HTTP", "stormsia", "https://raw.githubusercontent.com/stormsia/proxy-list/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Lalifeier SOCKS4", "lalifeier", "https://raw.githubusercontent.com/lalifeier/proxy-scraper/main/proxies/socks4.txt", ProxyProtocol.Socks4);
        yield return Feed(rank++, "XigmaDev Mixed", "XigmaDev", "https://raw.githubusercontent.com/XigmaDev/proxy/main/proxies.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Bunthea Taing Mixed", "BuntheaTaing", "https://raw.githubusercontent.com/BuntheaTaing/Proxy-Scraper/main/proxy.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Duong Trung Hieu HTTP", "du0ngtrunghieu", "https://raw.githubusercontent.com/du0ngtrunghieu/proxy-scraper/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Simple Proxylist HTTP", "Cheagjihvg", "https://raw.githubusercontent.com/Cheagjihvg/simple-proxylist/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Faiz Proxy Scraper Mixed", "faizdotid", "https://raw.githubusercontent.com/faizdotid/Proxy-Scraper/main/proxies.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Riyoway HTTP", "Riyoway", "https://raw.githubusercontent.com/Riyoway/Proxies/master/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "RioMMO HTTP", "RioMMO", "https://raw.githubusercontent.com/RioMMO/ProxyFree/main/HTTP.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "IPParrot HTTP", "IPParrot", "https://raw.githubusercontent.com/IPParrot/proxy_ips/main/proxies/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Netro Indonesia HTTP", "NetroIndonesia", "https://raw.githubusercontent.com/NetroIndonesia/proxy-live/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Ian Lusule HTTP", "Ian-Lusule", "https://raw.githubusercontent.com/Ian-Lusule/Proxies/main/proxies/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Shubham Shendre HTTP", "shubhamshendre", "https://raw.githubusercontent.com/shubhamshendre/Free-Proxies/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Snove Emby Mixed", "snove999", "https://raw.githubusercontent.com/snove999/emby-proxy-list/main/all.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Moleway HTTP", "Moleway", "https://raw.githubusercontent.com/Moleway/Free-Proxy-List/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "6yyn HTTP", "6yyn", "https://raw.githubusercontent.com/6yyn/free-proxy-list/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Access At HTTP", "Access-At", "https://raw.githubusercontent.com/Access-At/proxy-list/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Krokmazagaga HTTP", "krokmazagaga", "https://raw.githubusercontent.com/krokmazagaga/http-proxy-list/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Trio666 HTTP", "trio666", "https://raw.githubusercontent.com/trio666/proxy-checker/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "TazaProxy Working", "abusaeeidx", "https://raw.githubusercontent.com/abusaeeidx/TazaProxy-Troxy/main/working_proxies.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Pwnx0 HTTP", "pwnx0", "https://raw.githubusercontent.com/pwnx0/proxy/main/proxies/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Itsanwar HTTP", "itsanwar", "https://raw.githubusercontent.com/itsanwar/proxy-scraper/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Shinzzyak Mixed", "Shinzzyak", "https://raw.githubusercontent.com/Shinzzyak/proxy-scraper/main/proxies.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Ester Mixed", "notfaj", "https://raw.githubusercontent.com/notfaj/ester/master/proxies.txt", ProxyProtocol.Http);
        yield return Feed(rank++, "Proxy Pulse HTTP", "BlacKSnowDot0", "https://raw.githubusercontent.com/BlacKSnowDot0/Proxy-Pulse/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank, "LoneKing HTTP", "LoneKingCode", "https://raw.githubusercontent.com/LoneKingCode/free-proxy-db/main/proxies/http.txt", ProxyProtocol.Http);
    }

    private static IEnumerable<BuiltInSource> ExtendedIndependentProviderFeeds()
    {
        var rank = 452;
        yield return Feed(rank++, "HankNovic SOCKS5", "HankNovic", "https://raw.githubusercontent.com/HankNovic/ProxyClean/main/SOCKS5.txt", ProxyProtocol.Socks5);
        yield return Feed(rank++, "Allaux HTTP", "Allaux", "https://raw.githubusercontent.com/Allaux/fresh-proxy-list/main/http.txt", ProxyProtocol.Http);
        yield return Feed(rank, "Mahdi Proxies Mixed", "MAHDI-143", "https://raw.githubusercontent.com/MAHDI-143/proxies/main/proxies.txt", ProxyProtocol.Http);
    }

    private static IEnumerable<BuiltInSource> PaginatedIndependentProviderFeeds()
    {
        const string feeds = """
            javadbazokar|https://raw.githubusercontent.com/javadbazokar/PROXY-List/main/http.txt|Http
            zwced|https://raw.githubusercontent.com/zwced/open-proxy-list/main/proxies.txt|Http
            MuRongPIG|https://raw.githubusercontent.com/MuRongPIG/Proxy-Master/main/http.txt|Http
            FifzzSENZE|https://raw.githubusercontent.com/FifzzSENZE/ProxyList/master/proxies/http.txt|Http
            mishakorzik|https://raw.githubusercontent.com/mishakorzik/100000-Proxy/main/proxy.txt|Http
            likhonsheikhidk|https://raw.githubusercontent.com/likhonsheikhidk/proxy/main/http.txt|Http
            tov-a|https://raw.githubusercontent.com/tov-a/proxy-lists-Auto-renew-/main/proxies/http.txt|Http
            I3L4CK-H4CK3I2|https://raw.githubusercontent.com/I3L4CK-H4CK3I2/ProxyChecker/main/proxy.txt|Http
            akshat0098|https://raw.githubusercontent.com/akshat0098/proxy-list/master/http.txt|Http
            Jakee8718|https://raw.githubusercontent.com/Jakee8718/Free-Proxies/main/proxy/http.txt|Http
            Wh1zz52|https://raw.githubusercontent.com/Wh1zz52/Proxy-List/main/proxies.txt|Http
            Skiddle-ID|https://raw.githubusercontent.com/Skiddle-ID/proxylist/main/proxies.txt|Http
            watashibeme|https://raw.githubusercontent.com/watashibeme/openproxy.space/main/http.txt|Http
            lighscent|https://raw.githubusercontent.com/lighscent/proxies/master/http.txt|Http
            rdavydov|https://raw.githubusercontent.com/rdavydov/proxy-list/main/proxies/http.txt|Http
            wuye999|https://raw.githubusercontent.com/wuye999/proxy-365/main/SOCKS5.txt|Socks5
            merlinepedra|https://raw.githubusercontent.com/merlinepedra/PROXY-LIST-1/master/proxy-list.txt|Http
            dotargz|https://raw.githubusercontent.com/dotargz/proxy-list-unblocked/master/proxy-list.txt|Http
            irwan-aidan|https://raw.githubusercontent.com/irwan-aidan/proxy-list/master/proxy-list.txt|Http
            AbhinavBaranwal|https://raw.githubusercontent.com/AbhinavBaranwal/Proxy-List/master/proxy-list.txt|Http
            Fahmi-XD|https://raw.githubusercontent.com/Fahmi-XD/proxy-list/main/socks5.txt|Socks5
            amit-pathak009|https://raw.githubusercontent.com/amit-pathak009/checkProxy/master/proxy.txt|Http
            CallocGD|https://raw.githubusercontent.com/CallocGD/Boomlings-Proxy-list/main/socks5.txt|Socks5
            yoannchb-pro|https://raw.githubusercontent.com/yoannchb-pro/https-proxies-template/main/proxies.txt|Http
            CrateC|https://raw.githubusercontent.com/CrateC/proxy_list/main/proxies.txt|Http
            buffies1|https://raw.githubusercontent.com/buffies1/FreeProxyList/main/proxy-list.txt|Http
            Loclki|https://raw.githubusercontent.com/Loclki/PROXY-List/main/http.txt|Http
            Reytzydev|https://raw.githubusercontent.com/Reytzydev/PROXY-LIST/main/http.txt|Http
            MOMMY2034|https://raw.githubusercontent.com/MOMMY2034/Free-Proxy-List/main/http.txt|Http
            HUZAKI-PROJECT|https://raw.githubusercontent.com/HUZAKI-PROJECT/proxy_list/main/proxies.txt|Http
            merwin-asm|https://raw.githubusercontent.com/merwin-asm/proxy.list/main/proxies.txt|Http
            Ankur7373|https://raw.githubusercontent.com/Ankur7373/proxy-list/main/proxies.txt|Http
            prisbre|https://raw.githubusercontent.com/prisbre/proxy-list/main/http.txt|Http
            Yogazyy|https://raw.githubusercontent.com/Yogazyy/PROXY-List/main/http.txt|Http
            xiaocaiji61|https://raw.githubusercontent.com/xiaocaiji61/proxy-list-git/main/proxies.txt|Http
            devmjun|https://raw.githubusercontent.com/devmjun/ProxyList/main/http.txt|Http
            proxy4parsing|https://raw.githubusercontent.com/proxy4parsing/proxy-list/main/http.txt|Http
            gastonochoa24|https://raw.githubusercontent.com/gastonochoa24/MiProxyList/main/socks5.txt|Socks5
            AndzentsXD|https://raw.githubusercontent.com/AndzentsXD/proxy-list/main/http.txt|Http
            NotserpIsHere|https://raw.githubusercontent.com/NotserpIsHere/Proxy-List/main/http.txt|Http
            sydeptraivc|https://raw.githubusercontent.com/sydeptraivc/SocksProxyList/main/proxies.txt|Http
            iptotal|https://raw.githubusercontent.com/iptotal/free-proxy-list/master/socks4.txt|Socks4
            monster8d|https://raw.githubusercontent.com/monster8d/Elite_Proxy-List/main/proxy.txt|Http
            Rkdeve|https://raw.githubusercontent.com/Rkdeve/PROXY-List/main/socks4.txt|Socks4
            DarlingSh1337|https://raw.githubusercontent.com/DarlingSh1337/ProxyList/main/http.txt|Http
            Brembo19|https://raw.githubusercontent.com/Brembo19/Proxy-list/main/proxy.txt|Http
            alt45emailnotpro-web|https://raw.githubusercontent.com/alt45emailnotpro-web/proxy-list-FREE-not-checked-/main/proxies.txt|Http
            KIRAN-KUMAR-K3|https://raw.githubusercontent.com/KIRAN-KUMAR-K3/proxy-list/main/http.txt|Http
            Alex877-xmr|https://raw.githubusercontent.com/Alex877-xmr/PROXY-List/master/http.txt|Http
            chipsed|https://raw.githubusercontent.com/chipsed/proxies/main/proxies.txt|Http
            bachkhoasoft|https://raw.githubusercontent.com/bachkhoasoft/PROXY-List-master/main/http.txt|Http
            cyberbudy|https://raw.githubusercontent.com/cyberbudy/proxy-list-5/main/all.txt|Http
            BinhPhuongIT|https://raw.githubusercontent.com/BinhPhuongIT/PROXY-List/master/http.txt|Http
            HackerSM9|https://raw.githubusercontent.com/HackerSM9/proxy-list/main/http.txt|Http
            fadlifadle|https://raw.githubusercontent.com/fadlifadle/MyProxy-List/main/http.txt|Http
            gamerpro83|https://raw.githubusercontent.com/gamerpro83/proxy-list/master/http.txt|Http
            impactproxies|https://raw.githubusercontent.com/impactproxies/Proxy-List/main/http.txt|Http
            nffluaefoanr|https://raw.githubusercontent.com/nffluaefoanr/PROXY-List/master/http.txt|Http
            ProxyListt|https://raw.githubusercontent.com/ProxyListt/PROXY-list/main/http.txt|Http
            Sage520|https://raw.githubusercontent.com/Sage520/Proxy-List/main/http.txt|Http
            ProxiesPage|https://raw.githubusercontent.com/ProxiesPage/proxy-list/main/HTTP.txt|Http
            JeffroMF|https://raw.githubusercontent.com/JeffroMF/PROXY-List5/master/http.txt|Http
            prestonator|https://raw.githubusercontent.com/prestonator/PROXY-List-prefixed/main/http.txt|Http
            ItzRazvyy|https://raw.githubusercontent.com/ItzRazvyy/ProxyList/main/http.txt|Http
            MeDawideK|https://raw.githubusercontent.com/MeDawideK/proxy-list/main/proxy.txt|Http
            cybblog|https://raw.githubusercontent.com/cybblog/proxy-list/main/https.txt|Https
            Okenwa899|https://raw.githubusercontent.com/Okenwa899/PROXY-LIST/main/http.txt|Http
            ProTechEx|https://raw.githubusercontent.com/ProTechEx/PROXY-List/master/http.txt|Http
            khoivutru|https://raw.githubusercontent.com/khoivutru/proxy-list/main/http.txt|Http
            rolandmccarthy13|https://raw.githubusercontent.com/rolandmccarthy13/free-proxy-list/master/http.txt|Http
            MFTEAM1|https://raw.githubusercontent.com/MFTEAM1/PROXY-List/main/http.txt|Http
            Mohamedfinder|https://raw.githubusercontent.com/Mohamedfinder/proxy-list/main/http.txt|Http
            DeadmanXXXII|https://raw.githubusercontent.com/DeadmanXXXII/Free_Proxy_List/main/proxies.txt|Http
            Khnaz35|https://raw.githubusercontent.com/Khnaz35/proxy-list/main/socks5.txt|Socks5
            proxiesmaster|https://raw.githubusercontent.com/proxiesmaster/Free-Proxy-List/main/proxies.txt|Http
            dangquocvinh05|https://raw.githubusercontent.com/dangquocvinh05/proxy-list/main/proxy.txt|Http
            mawinno|https://raw.githubusercontent.com/mawinno/PROXY-List/main/http.txt|Http
            proxylist-to|https://raw.githubusercontent.com/proxylist-to/proxy-list/main/http.txt|Http
            gaurav-321|https://raw.githubusercontent.com/gaurav-321/Public-Fast-Proxy-List/master/http.txt|Http
            exz1pe|https://raw.githubusercontent.com/exz1pe/proxy-list-/main/proxies.txt|Http
            kingsley1996|https://raw.githubusercontent.com/kingsley1996/proxy-list/master/proxies/http.txt|Http
            RAOdotSH|https://raw.githubusercontent.com/RAOdotSH/Proxy-List/main/proxy-list.txt|Http
            adasd223|https://raw.githubusercontent.com/adasd223/proxy-list-github/main/http.txt|Http
            yoitelinuxherew-ship-it|https://raw.githubusercontent.com/yoitelinuxherew-ship-it/proxy-list.txt/main/proxy-list.txt|Http
            udman1336|https://raw.githubusercontent.com/udman1336/Proxy-List/main/proxies/http.txt|Http
            TundzhayDzhansaz|https://raw.githubusercontent.com/TundzhayDzhansaz/proxy-list-auto-pull-in-30min/main/proxies/http.txt|Http
            caedencode|https://raw.githubusercontent.com/caedencode/proxy-list/main/http.txt|Http
            AdamParker19|https://raw.githubusercontent.com/AdamParker19/Proxy-list/main/proxy.txt|Http
            mrgadotti|https://raw.githubusercontent.com/mrgadotti/echolink-proxy-list/main/proxy.txt|Http
            iiTzAm1ne|https://raw.githubusercontent.com/iiTzAm1ne/ScrapperFor-FreeProxyList.net_v.1/main/http.txt|Http
            KD01-1494|https://raw.githubusercontent.com/KD01-1494/Free-Proxy-List-Parse/main/proxy.txt|Http
            osxma|https://raw.githubusercontent.com/osxma/proxy-list/main/proxies.txt|Http
            FosterG4|https://raw.githubusercontent.com/FosterG4/proxy-list/main/http.txt|Http
            sussyname|https://raw.githubusercontent.com/sussyname/proxy-list/main/https.txt|Https
            alexander-bui|https://raw.githubusercontent.com/alexander-bui/proxy-list-reformat-jdownloader/main/proxy.txt|Http
            rmdhfz|https://raw.githubusercontent.com/rmdhfz/free-proxy-list/main/list.txt|Http
            zeynoxwashere|https://raw.githubusercontent.com/zeynoxwashere/proxy-list/main/http.txt|Http
            museu-do-novo|https://raw.githubusercontent.com/museu-do-novo/proxy-list/main/proxies.txt|Http
            Abobys228|https://raw.githubusercontent.com/Abobys228/Checked-Proxy-List/main/proxy-list.txt|Http
            weird1337|https://raw.githubusercontent.com/weird1337/proxies-list/main/proxies.txt|Http
            Zyhkss|https://raw.githubusercontent.com/Zyhkss/ProxyList/main/http.txt|Http
            SteepSmit|https://raw.githubusercontent.com/SteepSmit/my-proxy-list/main/proxies.txt|Http
            imam3543|https://raw.githubusercontent.com/imam3543/Free-HighQuality-Proxy-Socks/main/results/http.txt|Http
            botdev72|https://raw.githubusercontent.com/botdev72/proxy-list/main/http.txt|Http
            LuciverXploit|https://raw.githubusercontent.com/LuciverXploit/Proxy-List/main/socks4.txt|Socks4
            threatcode|https://raw.githubusercontent.com/threatcode/proxy-list/master/proxies/http.txt|Http
            Dufrasne4242|https://raw.githubusercontent.com/Dufrasne4242/dufrasne-proxy-list/main/proxies.txt|Http
            Proxy-List|https://raw.githubusercontent.com/Proxy-List/socks4/main/socks4.txt|Socks4
            vaitan|https://raw.githubusercontent.com/vaitan/proxy-list/main/http.txt|Http
            prxchk|https://raw.githubusercontent.com/prxchk/proxy-list/main/http.txt|Http
            disaxmmm|https://raw.githubusercontent.com/disaxmmm/proxy-list/main/http.txt|Http
            Syafii-XD|https://raw.githubusercontent.com/Syafii-XD/Proxy-List/main/http.txt|Http
            diyarsirwan|https://raw.githubusercontent.com/diyarsirwan/PROXY-List/main/socks4.txt|Socks4
            Hatois|https://raw.githubusercontent.com/Hatois/free-proxy-list/main/proxies.txt|Http
            djmohsen|https://raw.githubusercontent.com/djmohsen/downloader/main/list.txt|Http
            Sanrakuu|https://raw.githubusercontent.com/Sanrakuu/proxy-list/master/proxies/http.txt|Http
            """;

        var rank = 455;
        foreach (var line in feeds.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            var protocol = Enum.Parse<ProxyProtocol>(parts[2]);
            yield return Feed(rank++, $"{parts[0]} {protocol}", parts[0], parts[1], protocol);
        }
    }

    private static IEnumerable<BuiltInSource> FreeSearchIndependentProviderFeeds()
    {
        const string feeds = """
            icecoffie|https://raw.githubusercontent.com/icecoffie/free-proxy/main/proxy.txt|Http
            wccrawford|https://raw.githubusercontent.com/wccrawford/free-proxy/main/http.txt|Http
            MidnightSpy|https://raw.githubusercontent.com/MidnightSpy/Free-Proxy-Servers-List/main/proxies.txt|Http
            gariox3d|https://raw.githubusercontent.com/gariox3d/Proxy-List/master/socks5.txt|Socks5
            YanStar|https://raw.githubusercontent.com/YanStar/free-proxy/main/http.txt|Http
            Sudo-Bigbtc|https://raw.githubusercontent.com/Sudo-Bigbtc/socks/main/http.txt|Http
            Kaseki587|https://raw.githubusercontent.com/Kaseki587/proxys/main/proxy.txt|Http
            Fox-Boss|https://raw.githubusercontent.com/Fox-Boss/Free-Proxy/main/http.txt|Http
            NightR4ge|https://raw.githubusercontent.com/NightR4ge/Free-proxy/main/proxies.txt|Http
            Muhammadcracker|https://raw.githubusercontent.com/Muhammadcracker/free-rpoxy/main/proxies.txt|Http
            nguyet0310|https://raw.githubusercontent.com/nguyet0310/nodriver_proxy/master/proxy-list.txt|Http
            okor314|https://raw.githubusercontent.com/okor314/free-proxy/master/proxy.txt|Http
            fenfenlei888|https://raw.githubusercontent.com/fenfenlei888/free_proxies/main/socks4.txt|Socks4
            theahmadov|https://raw.githubusercontent.com/theahmadov/EvilMap/main/output/socks5.txt|Socks5
            """;

        var rank = 571;
        foreach (var line in feeds.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            var protocol = Enum.Parse<ProxyProtocol>(parts[2]);
            yield return Feed(rank++, $"{parts[0]} {protocol}", parts[0], parts[1], protocol);
        }
    }

    private static IEnumerable<BuiltInSource> WorkingSearchIndependentProviderFeeds()
    {
        const string feeds = """
            itzNamanDev|https://raw.githubusercontent.com/itzNamanDev/HW-Scraper/main/proxies.txt|Http
            crunchtechnologies|https://raw.githubusercontent.com/crunchtechnologies/proxychecker/master/proxies.txt|Http
            Ahmed-pk|https://raw.githubusercontent.com/Ahmed-pk/FastestProxyChecker/main/proxies.txt|Http
            Abdulkadirbulbul|https://raw.githubusercontent.com/Abdulkadirbulbul/Free-Proxy-Creator/master/working_proxies.txt|Http
            dusikasss|https://raw.githubusercontent.com/dusikasss/ProxyHunter/main/working_proxies.txt|Http
            zuhrrl|https://raw.githubusercontent.com/zuhrrl/NgetestProxy/master/proxies.txt|Http
            """;

        var rank = 585;
        foreach (var line in feeds.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            var protocol = Enum.Parse<ProxyProtocol>(parts[2]);
            yield return Feed(rank++, $"{parts[0]} {protocol}", parts[0], parts[1], protocol);
        }
    }

    private static IEnumerable<BuiltInSource> ProxyListTopicIndependentProviderFeeds()
    {
        const string feeds = """
            j0rd1s3rr4n0|https://raw.githubusercontent.com/j0rd1s3rr4n0/api/main/proxy/http.txt|Http
            uncrypt3d|https://raw.githubusercontent.com/uncrypt3d/Browser-with-Proxy-manager/main/proxies.txt|Http
            nguywnben|https://raw.githubusercontent.com/nguywnben/daily-proxy-updates/main/proxies/http.txt|Http
            antidoxable|https://raw.githubusercontent.com/antidoxable/ProxyChecker/master/proxies.txt|Http
            AmirTyper|https://raw.githubusercontent.com/AmirTyper/Proxy-Checker/main/proxy.txt|Http
            BD1493|https://raw.githubusercontent.com/BD1493/proxy-link-generator/main/proxies.txt|Http
            codedbyelif|https://raw.githubusercontent.com/codedbyelif/kimse-bas/main/proxies.txt|Http
            junioralive|https://raw.githubusercontent.com/junioralive/PROXY-Alive/main/socks4.txt|Socks4
            kami2k1|https://raw.githubusercontent.com/kami2k1/scan-proxy-zmap/main/http.txt|Http
            almroot|https://raw.githubusercontent.com/almroot/proxylist/master/list.txt|Http
            opsxcq|https://raw.githubusercontent.com/opsxcq/proxy-list/master/list.txt|Http
            pilo21|https://raw.githubusercontent.com/pilo21/TwitchBotting/main/proxy.txt|Http
            KhaiNguyenDuc|https://raw.githubusercontent.com/KhaiNguyenDuc/proxy-generator/main/proxies.txt|Http
            rolki-png|https://raw.githubusercontent.com/rolki-png/proxies/main/proxies/http.txt|Http
            0xarchit|https://raw.githubusercontent.com/0xarchit/duckduckgo-webscraper/main/proxies.txt|Http
            """;

        var rank = 592;
        foreach (var line in feeds.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            var protocol = Enum.Parse<ProxyProtocol>(parts[2]);
            yield return Feed(rank++, $"{parts[0]} {protocol}", parts[0], parts[1], protocol);
        }
    }

    private static IEnumerable<BuiltInSource> ProxyScraperSearchIndependentProviderFeeds()
    {
        const string feeds = """
            Serhiiilnytskyi|https://raw.githubusercontent.com/Serhiiilnytskyi/proxy_scraper/main/proxies.txt|Http
            iamthebestm85|https://raw.githubusercontent.com/iamthebestm85/Proxy-Scraper-And-Checker/main/proxy.txt|Http
            NelFeast|https://raw.githubusercontent.com/NelFeast/ProxyScraper/main/proxies.txt|Http
            xavierontop|https://raw.githubusercontent.com/xavierontop/proxy-scraper/main/proxies.txt|Http
            morpheous2333|https://raw.githubusercontent.com/morpheous2333/Simple-Proxy-Scraper-Discord-/main/http.txt|Http
            o7-Fire|https://raw.githubusercontent.com/o7-Fire/Proxy-Scraper/main/proxies.txt|Http
            xANet7|https://raw.githubusercontent.com/xANet7/Proxy-Scraper/main/proxies.txt|Http
            ceb10n|https://raw.githubusercontent.com/ceb10n/proxytron/master/proxies.txt|Http
            Trxpinethan|https://raw.githubusercontent.com/Trxpinethan/proxyscraper/main/proxies.txt|Http
            t3xxdev|https://raw.githubusercontent.com/t3xxdev/ProxyStorm/main/proxies.txt|Http
            """;

        var rank = 611;
        foreach (var line in feeds.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            var protocol = Enum.Parse<ProxyProtocol>(parts[2]);
            yield return Feed(rank++, $"{parts[0]} {protocol}", parts[0], parts[1], protocol);
        }
    }

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
