# Каталог 85 провайдеров бесплатных прокси

Все 85 независимых провайдеров уже входят в `BuiltInSourceCatalog`, а их 320 HTTPS endpoint обрабатываются production collector. Документ предназначен оператору; публичная главная страница источники не раскрывает.

Совместимый read-only endpoint `GET /api/v1/sources` пока сохраняется для API-клиентов, но обычный React-интерфейс его не запрашивает и не показывает. Runtime errors, backoff и внутреннее состояние доступны только администратору.

Базовые 81 endpoint прошли полный end-to-end аудит 10 августа 2026 года. 24 августа дополнительно проверены 17 URL шести провайдеров, 26 августа — ещё 100 URL от 19 origin-владельцев. 28 августа проверены ещё 100 protocol- и country-feed. 1 сентября два удалённых country-feed `XYZS996` заменены, добавлены 12 URL пяти новых origin-владельцев и повторно проверен весь каталог. Полный 320-feed прогон является обязательным release-gate и не подменяется URL-проверкой.

| № | Провайдер | Feed'ов | Протоколы | Представительный источник |
|---:|---|---:|---|---|
| 1 | ProxyScrape | 4 | Mixed, HTTP, SOCKS4, SOCKS5 | [официальный API](https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=text) |
| 2 | OpenProxyList | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [openproxylist.xyz](https://openproxylist.xyz/http.txt) |
| 3 | Proxifly | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/proxifly/free-proxy-list) |
| 4 | TheSpeedX | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/TheSpeedX/PROXY-List) |
| 5 | IPLocate | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/iplocate/free-proxy-list) |
| 6 | Databay Labs | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/databay-labs/free-proxy-list) |
| 7 | TuanMinPay | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/TuanMinPay/live-proxy) |
| 8 | HProxy | 25 | Mixed, HTTP, HTTPS, SOCKS4, SOCKS5, country feeds | [GitHub](https://github.com/hproxy-com/free-proxy-list) |
| 9 | ObcbO | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/ObcbO/getproxy) |
| 10 | Abdal Proxy Hub | 3 | HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/ebrasha/abdal-proxy-hub) |
| 11 | Dpangestuw Free-PROXY | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/dpangestuw/Free-PROXY) |
| 12 | AnonymousWork | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/Anonym0usWork1221/Free-Proxies) |
| 13 | r00tee | 3 | HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/r00tee/Proxy-List) |
| 14 | ProxySpace | 3 | HTTP, SOCKS4, SOCKS5 | [proxyspace.pro](https://proxyspace.pro/http.txt) |
| 15 | Jetkai | 1 | HTTP | [GitHub](https://github.com/jetkai/proxy-list) |
| 16 | B4RC0DE | 1 | HTTP | [GitHub](https://github.com/B4RC0DE-TM/proxy-list) |
| 17 | Argh94 | 1 | HTTP | [GitHub](https://github.com/Argh94/Proxy-List) |
| 18 | Monosans | 1 | HTTP | [GitHub](https://github.com/monosans/proxy-list) |
| 19 | Spys.me | 1 | HTTP | [spys.me](https://spys.me/proxy.txt) |
| 20 | CyberH4ck3r | 1 | HTTP | [GitHub](https://github.com/cyberh4ck3r/free-proxy-list) |
| 21 | Proxmint | 1 | HTTP | [GitHub](https://github.com/proxmint/free-proxy-list) |
| 22 | Rix4Uni | 1 | Mixed | [GitHub](https://github.com/rix4uni/fresh-proxy-list) |
| 23 | Komutan234 | 1 | HTTP | [GitHub](https://github.com/komutan234/Proxy-List-Free) |
| 24 | Zaeem20 | 1 | HTTP | [GitHub](https://github.com/Zaeem20/FREE_PROXIES_LIST) |
| 25 | WebUnblocker | 1 | HTTP | [GitHub](https://github.com/webunblocker/free-proxy-list) |
| 26 | Watchttvv | 1 | SOCKS5 | [GitHub](https://github.com/watchttvv/free-proxy-list) |
| 27 | VPSLab | 1 | HTTP | [GitHub](https://github.com/VPSLabCloud/VPSLab-Free-Proxy-List) |
| 28 | VMHeaven | 1 | Mixed | [GitHub](https://github.com/vmheaven/VMHeaven.io-Free-Proxy-List) |
| 29 | GProxyNet | 1 | HTTP | [GitHub](https://github.com/gproxynet/free-proxy-list) |
| 30 | Anutmagang | 1 | HTTP | [GitHub](https://github.com/anutmagang/Free-HighQuality-Proxy-Socks) |
| 31 | ProxRipper | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/Mohammedcha/ProxRipper) |
| 32 | RoosterKid | 1 | HTTPS | [GitHub](https://github.com/roosterkid/openproxylist) |
| 33 | Proxy-Free | 1 | HTTP | [GitHub](https://github.com/proxy-free/free-proxy-list) |
| 34 | Ch4120N | 1 | HTTP | [GitHub](https://github.com/Ch4120N/Ch4120N-Proxy-List) |
| 35 | XYZS996 | 68 | Mixed, HTTP, HTTPS, country feeds | [GitHub](https://github.com/xyzs996/free-proxy-health-list) |
| 36 | Tianndev | 5 | Mixed, HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/Tianndev/free-proxy) |
| 37 | KangProxy | 1 | HTTP | [GitHub](https://github.com/officialputuid/KangProxy) |
| 38 | Thordata | 1 | HTTP | [GitHub](https://github.com/Thordata/awesome-free-proxy-list) |
| 39 | ErcinDedeoglu | 1 | HTTP | [GitHub](https://github.com/ErcinDedeoglu/proxies) |
| 40 | Skillter | 1 | HTTP | [GitHub](https://github.com/Skillter/ProxyGather) |
| 41 | ClarkTM | 1 | HTTP | [GitHub](https://github.com/clarketm/proxy-list) |
| 42 | Sunny9577 | 1 | HTTP | [GitHub](https://github.com/sunny9577/proxy-scraper) |
| 43 | HookzOf | 1 | SOCKS5 | [GitHub](https://github.com/hookzof/socks5_list) |
| 44 | Vakhov | 1 | HTTP | [GitHub](https://github.com/vakhov/fresh-proxy-list) |
| 45 | ShiftyTR | 1 | HTTP | [GitHub](https://github.com/ShiftyTR/Proxy-List) |
| 46 | Fyvri | 1 | HTTP | [GitHub](https://github.com/fyvri/fresh-proxy-list) |
| 47 | BesJS | 1 | Mixed | [GitHub](https://github.com/Bes-js/public-proxy-list) |
| 48 | TheRituRajPS | 1 | HTTP | [GitHub](https://github.com/theriturajps/proxy-list) |
| 49 | NotThinks | 1 | Mixed | [GitHub](https://github.com/notthinks/proxy-lists) |
| 50 | MrMarble | 1 | Mixed | [GitHub](https://github.com/MrMarble/proxy-list) |
| 51 | RelayGlass | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/relayglass/free-proxy-list) |
| 52 | ProxyMan | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/Akshay7273/ProxyMan-free-proxy-list) |
| 53 | Dinoz | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/dinoz0rg/proxy-list) |
| 54 | Mzyui | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/mzyui/proxy-list) |
| 55 | Naravid | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/naravid19/checked-proxies) |
| 56 | aQuiner | 1 | HTTP | [GitHub](https://github.com/aQuiner/free-proxy-list) |
| 57 | Sevenworks | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/SevenworksDev/proxy-list) |
| 58 | Zevtyardt | 4 | Mixed, HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/zevtyardt/proxy-list) |
| 59 | Tsprnay | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/Tsprnay/Proxy-lists) |
| 60 | ALIILAPRO | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/ALIILAPRO/Proxy) |
| 61 | NikolaiT | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/NikolaiT/free-proxy-list) |
| 62 | VannDev | 21 | HTTP, HTTPS, SOCKS4, SOCKS5, site-tested | [GitHub](https://github.com/Vann-Dev/proxy-list) |
| 63 | SoliSpirit | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/SoliSpirit/proxy-list) |
| 64 | Elliottophellia | 6 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/elliottophellia/proxylist) |
| 65 | TheMiralay | 1 | Mixed | [GitHub](https://github.com/themiralay/Proxy-List-World) |
| 66 | HendrikBGR | 1 | Mixed | [GitHub](https://github.com/hendrikbgr/Free-Proxy-Repo) |
| 67 | NoArche | 5 | Mixed, HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/noarche/proxylist-socks5-sock4-exported-updates) |
| 68 | ProxyGenerator | 19 | Mixed, HTTP, HTTPS, SOCKS4, SOCKS5, service-tested | [GitHub](https://github.com/proxygenerator1/ProxyGenerator) |
| 69 | Seeh-Saah | 5 | Mixed, HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/Seeh-Saah/awesome-free-proxy-list) |
| 70 | 7and1 | 1 | HTTP | [GitHub](https://github.com/7and1/free-proxy-list) |
| 71 | TomJiu | 4 | Mixed, HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/tomjiu/proxy-pipeline) |
| 72 | GHSTFACES | 5 | Mixed, HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/GHSTFACES/PL) |
| 73 | Andigwandi | 1 | Mixed | [GitHub](https://github.com/andigwandi/free-proxy) |
| 74 | KevinRiver | 3 | Mixed, HTTP, SOCKS5 | [GitHub](https://github.com/kevinriverrrr-sudo/free-proxy-list) |
| 75 | Xnuvers | 3 | Mixed | [GitHub](https://github.com/Xnuvers007/free-proxy) |
| 76 | Proxio | 5 | Mixed, HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/proxio-io/proxy-list) |
| 77 | Azest Kings Crown | 1 | Mixed | [GitHub](https://github.com/azestkingscrown/Free_Proxy_List) |
| 78 | Pxys | 2 | Mixed, CSV | [GitHub](https://github.com/Pxys-io/DailyProxyList) |
| 79 | Syscallh00k | 5 | Mixed, HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/Syscallh00k/proxy-list) |
| 80 | Free Proxy API List | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/gnxD3RfTT2WE/free-proxy-api-list) |
| 81 | Gifted Proxies | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/mauricegift/free-proxies) |
| 82 | Worldpool | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/CelestialBrain/worldpool) |
| 83 | Firmfox | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/Firmfox/Proxify) |
| 84 | Berkay Digital | 1 | Mixed | [GitHub](https://github.com/berkay-digital/Proxy-Scraper) |
| 85 | Volkan Auto Proxy | 1 | Mixed | [GitHub](https://github.com/VolkanSah/Auto-Proxy-Fetcher) |

## Что именно гарантирует ProxyHarbor

Доступность feed и работоспособность прокси — разные проверки. Source audit требует, чтобы каждый встроенный endpoint присутствовал, был включён, успешно и полностью обработан текущим production-циклом и вернул хотя бы одного кандидата. После межисточниковой дедупликации validator независимо открывает соединение через каждый кандидат, проверяет заявленный протокол, TLS/HTTP-маршрут, внешний адрес и задержку. В публичные JSON, XML, TXT и CSV попадают только недавно подтверждённые живые адреса.

Бесплатные прокси меняются каждую минуту, поэтому нельзя честно обещать, что все строки внешнего feed будут рабочими. Гарантия сервиса другая и проверяемая: все 85 провайдеров входят в сбор; неработающие адреса не публикуются; отказавший, пустой или усечённый feed сразу становится нездоровым в diagnostics, Prometheus и еженедельном CI-аудите.

Полные 320 URL, их протоколы и эксплуатационный порядок находятся в [`BuiltInSourceCatalog.cs`](../src/ProxyHarbor.Infrastructure/BuiltInSourceCatalog.cs). Команда воспроизводимого production-аудита описана в [`SOURCES.md`](SOURCES.md).
