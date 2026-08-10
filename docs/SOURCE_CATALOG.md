# Топ-50 провайдеров бесплатных прокси

Каталог ниже — это не перечень сайтов «на будущее»: все 50 независимых провайдеров уже входят в `BuiltInSourceCatalog`, а их 81 HTTPS endpoint обрабатывается production collector. Позиция задаётся первым endpoint провайдера в текущем эксплуатационном рейтинге; один провайдер может публиковать несколько раздельных протокольных лент.

Тот же каталог доступен без административного ключа через `GET /api/v1/sources` и показан в обычном React-интерфейсе. Публичный ответ содержит только неизменяемые метаданные; ошибки, backoff и внутреннее состояние конкретной установки не раскрываются.

Последний полный production-аудит выполнен **10 августа 2026 года в 22:47 (Asia/Novosibirsk)** на отдельной чистой схеме PostgreSQL: **81/81** endpoint от **50/50** провайдеров прошли настоящий collector, ограничения размера, parser и межисточниковую дедупликацию за **5,238 секунды**. Получено 897 686 распознанных строк и **291 220 уникальных кандидатов**; ошибок, пустых результатов, пропущенных/усечённых feed'ов и достижения общего лимита не было. Следующая партия выполнила 1 600 объективных проверок без `Deferred`, подтвердила 6 живых адресов и одинаково опубликовала текущий живой набор в JSON, XML, TXT и CSV.

| № | Провайдер | Feed'ов | Протоколы | Представительный источник |
|---:|---|---:|---|---|
| 1 | ProxyScrape | 4 | Mixed, HTTP, SOCKS4, SOCKS5 | [официальный API](https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=text) |
| 2 | OpenProxyList | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [openproxylist.xyz](https://openproxylist.xyz/http.txt) |
| 3 | Proxifly | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 | [GitHub](https://github.com/proxifly/free-proxy-list) |
| 4 | TheSpeedX | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/TheSpeedX/PROXY-List) |
| 5 | IPLocate | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/iplocate/free-proxy-list) |
| 6 | Databay Labs | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/databay-labs/free-proxy-list) |
| 7 | TuanMinPay | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/TuanMinPay/live-proxy) |
| 8 | HProxy | 3 | HTTP, SOCKS4, SOCKS5 | [GitHub](https://github.com/hproxy-com/free-proxy-list) |
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
| 31 | ProxRipper | 1 | HTTP | [GitHub](https://github.com/Mohammedcha/ProxRipper) |
| 32 | RoosterKid | 1 | HTTPS | [GitHub](https://github.com/roosterkid/openproxylist) |
| 33 | Proxy-Free | 1 | HTTP | [GitHub](https://github.com/proxy-free/free-proxy-list) |
| 34 | Ch4120N | 1 | HTTP | [GitHub](https://github.com/Ch4120N/Ch4120N-Proxy-List) |
| 35 | XYZS996 | 1 | HTTP | [GitHub](https://github.com/xyzs996/free-proxy-health-list) |
| 36 | Tianndev | 1 | Mixed | [GitHub](https://github.com/Tianndev/free-proxy) |
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
| 49 | Stormsia | 1 | HTTP | [GitHub](https://github.com/stormsia/proxy-list) |
| 50 | MrMarble | 1 | Mixed | [GitHub](https://github.com/MrMarble/proxy-list) |

## Что именно гарантирует ProxyHarbor

Доступность feed и работоспособность прокси — разные проверки. Source audit требует, чтобы каждый встроенный endpoint присутствовал, был включён, успешно и полностью обработан текущим production-циклом и вернул хотя бы одного кандидата. После межисточниковой дедупликации validator независимо открывает соединение через каждый кандидат, проверяет заявленный протокол, TLS/HTTP-маршрут, внешний адрес и задержку. В публичные JSON, XML, TXT и CSV попадают только недавно подтверждённые живые адреса.

Бесплатные прокси меняются каждую минуту, поэтому нельзя честно обещать, что все строки внешнего feed будут рабочими. Гарантия сервиса другая и проверяемая: все 50 провайдеров собираются; неработающие адреса не публикуются; отказавший, пустой или усечённый feed сразу становится нездоровым в diagnostics, Prometheus и еженедельном CI-аудите.

Полные 81 URL, их протоколы и эксплуатационный порядок находятся в [`BuiltInSourceCatalog.cs`](../src/ProxyHarbor.Infrastructure/BuiltInSourceCatalog.cs). Команда воспроизводимого production-аудита описана в [`SOURCES.md`](SOURCES.md).
