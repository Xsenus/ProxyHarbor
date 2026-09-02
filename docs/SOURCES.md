# Каталог источников ProxyHarbor

Операторская таблица всех провайдеров с прямыми ссылками вынесена в [`SOURCE_CATALOG.md`](SOURCE_CATALOG.md); главная страница намеренно не раскрывает источники. Этот документ описывает эксплуатационные гарантии и воспроизводимый аудит.

Встроенный каталог содержит **320 HTTPS proxy-feed от 85 независимых проектов и сервисов** с отдельными HTTP/HTTPS/SOCKS4/SOCKS5 и country-потоками там, где провайдер их публикует. VPN-каталог содержит **174 HTTPS feed от 32 провайдеров** для OpenVPN, WireGuard, VLESS, VMess, Trojan, Shadowsocks, Hysteria2 и TUIC.

Предыдущий полный end-to-end аудит базовых 81 endpoint выполнен **10 августа 2026 года, 23:50 (Asia/Novosibirsk)** без ошибок, пустых результатов, skip или усечения. **24 августа 2026 года** добавлены 17 URL шести провайдеров, **26 августа** — ещё 100 URL от 19 origin-владельцев, **28 августа** — 12 proxy-feed и 33 VPN-feed. **1 сентября 2026 года** удалены два новых `404` у `XYZS996`, добавлены 12 proxy-feed от пяти новых владельцев и 25 VPN-feed от девяти новых владельцев; весь итоговый каталог повторно проверен. **2 сентября 2026 года** исчезнувшие country-feed `XYZS996 GR`, `XYZS996 GQ` и `XYZS996 DK` последовательно заменены на непустые `XYZS996 US`, `XYZS996 TH` и `XYZS996 TR`, а опустевший `ProxyGenerator MostStable SOCKS4` — на рабочий `ProxyGenerator Cloudflare SOCKS4`; полный URL-аудит после последней замены подтвердил **320/320** успешных feed'ов от 85 владельцев. Каждый новый URL ответил HTTP 200 и вернул хотя бы один разбираемый адрес или URI конфигурации. Это URL/live-аудит, а не подмена полного сетевого прогона: workflow требует ровно **320/85** для прокси и перед production-релизом повторяет настоящий collector и validation. Каноническая дата `LastAuditedOn` публикуется в admin diagnostics и Prometheus.

| Провайдер | Feed'ов | Протоколы |
|---|---:|---|
| ProxyScrape | 4 | Mixed, HTTP, SOCKS4, SOCKS5 |
| OpenProxyList | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 |
| Proxifly | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 |
| TheSpeedX | 3 | HTTP, SOCKS4, SOCKS5 |
| IPLocate | 3 | HTTP, SOCKS4, SOCKS5 |
| Databay Labs | 3 | HTTP, SOCKS4, SOCKS5 |
| TuanMinPay | 3 | HTTP, SOCKS4, SOCKS5 |
| HProxy | 25 | Mixed, HTTP, HTTPS, SOCKS4, SOCKS5, country feeds |
| ObcbO | 3 | HTTP, SOCKS4, SOCKS5 |
| Abdal Proxy Hub | 3 | HTTPS, SOCKS4, SOCKS5 |
| Dpangestuw Free-PROXY | 3 | HTTP, SOCKS4, SOCKS5 |
| AnonymousWork | 3 | HTTP, SOCKS4, SOCKS5 |
| r00tee | 3 | HTTPS, SOCKS4, SOCKS5 |
| ProxySpace | 3 | HTTP, SOCKS4, SOCKS5 |
| Jetkai | 1 | HTTP |
| B4RC0DE | 1 | HTTP |
| Argh94 | 1 | HTTP |
| Monosans | 1 | HTTP |
| Spys.me | 1 | HTTP |

Дополнительные независимые провайдеры: CyberH4ck3r, Proxmint, Rix4Uni, Komutan234, Zaeem20, WebUnblocker, Watchttvv, VPSLab, VMHeaven, GProxyNet, Anutmagang, ProxRipper, RoosterKid, Proxy-Free, Ch4120N, XYZS996, Tianndev, KangProxy, Thordata, ErcinDedeoglu, Skillter, ClarkTM, Sunny9577, HookzOf, Vakhov, ShiftyTR, Fyvri, BesJS, TheRituRajPS, MrMarble, RelayGlass, ProxyMan, Dinoz, Mzyui, Naravid, aQuiner, Proxio, Azest Kings Crown, Pxys, Syscallh00k, Free Proxy API List, Gifted Proxies, Worldpool, Firmfox, Berkay Digital и Volkan Auto Proxy.

Канонические URL, ранги и протоколы находятся в `BuiltInSourceCatalog.cs`. На старте сервис идемпотентно добавляет недостающие встроенные feed'ы даже в существующую БД, обновляет их метаданные, но сохраняет ручной выбор администратора `Enabled/Disabled` и никогда не удаляет пользовательские источники.

Для повторного живого аудита после запуска контейнеров:

```powershell
./tools/Audit-SourceFeeds.ps1 -AdminKey 'значение ADMIN_API_KEY' -ReportPath './source-audit.json'
```

Встроенные VPN-feed проверяются отдельно без запуска приложения:

```powershell
./tools/Test-BuiltInVpnSourceEndpoints.ps1
```

Проверка требует ровно 174 уникальных HTTPS URL от 32 провайдеров, непустую лицензию или условия публикации и хотя бы одну поддерживаемую конфигурацию в каждом ответе. В CI используется быстрый `-CatalogOnly`, а еженедельный source-audit выполняет живую сетевую проверку всех VPN-feed.

Скрипт запускает настоящий production-сбор, сохраняет машиночитаемый JSON-отчёт даже при раннем отказе и завершится ошибкой, если admin-команда не вернула завершённый цикл, хотя бы один активный источник не был заново обработан именно в этом цикле, его результат пуст/ошибочен/усечён, forced-run пропустил источник, не создал ни одного уникального кандидата, счётчики `CollectionRun` расходятся с состоянием feed'ов либо достигнут `MaxCandidatesPerRun`. Artifact отдельно сохраняет длительность, число обработанных/пропущенных источников, уникальных кандидатов и новых строк БД. Отдельный completeness-gate требует наличия и включённости всех 320 встроенных feed'ов и ровно 85 независимых origin-владельцев. Независимость считается не по display-name: `providerIdentity` равен GitHub owner либо DNS hostname. Пользовательские feed'ы не искажают эту проверку. Workflow `Source feed audit` повторяет аудит еженедельно и сохраняет отчёт и API-лог как GitHub Actions artifacts.

Содержимое каждого feed'а считается недоверенным: загрузка ограничена 10 МБ, parser работает без backtracking, принимает только публичные IP и не хранит больше `Collector__MaxProxiesPerSource`. Первый следующий уникальный адрес выставляет постоянный признак усечения; дубликаты сверх лимита не считаются потерянными данными. Межисточниковый `Collector__MaxCandidatesPerRun` обладает той же точной семантикой при конкурентной загрузке: сигнал появляется только для реально отброшенного уникального endpoint, но не при точном заполнении или повторе уже известного адреса. Поэтому источник с миллионами строк не обходит эксплуатационные лимиты созданием полной промежуточной коллекции, но и не может выглядеть полностью обработанным.

Ответы с `text/html`, `application/xhtml+xml` либо HTML envelope отклоняются даже при HTTP 200 и наличии похожей на `IP:port` строки. Поэтому login, WAF и error page не могут ложно пометить источник успешным и добавить служебный адрес как proxy-кандидат; отсутствующий media type, plain text, JSON и binary feed остаются совместимыми.

Временные 429/5xx и транспортные сбои повторяются с bounded jitter/backoff. Ответ с непрочитанным body закрывается до ожидания повтора, поэтому недоступный провайдер не удерживает ограниченные connection-pool slots во время паузы.

Успешный `200` сохраняет bounded HTTP validators `ETag` и `Last-Modified` в строке источника, backup и restore. Следующий запрос передаёт их как `If-None-Match`/`If-Modified-Since`; `304 Not Modified` считается свежей успешной проверкой, сохраняет прежние count/truncation и не создаёт кандидатов или сетевой/CPU расход повторного body. Отдельный `LastContentFetchedAt` не меняется на `304`. Когда полный body старше половины membership-retention, но максимум суток, collector выполняет unconditional fetch: это предотвращает необратимую потерю удалённого Pending/Dead proxy из feed с вечным неизменным ETag. Ответ `304` принимается только после прежнего успешного непустого результата и реально отправленного validator. При смене URL/protocol validators и дата полного body сбрасываются, а malformed или содержащий control characters ETag отклоняется fail-closed.

Ручной `POST /api/v1/admin/collect` и вызывающий его source-аудит не отправляют conditional validators: каждый feed обязан вернуть body. JSON и gate требуют `LastContentFetchedAt` каждого enabled source внутри `StartedAt…FinishedAt`, поэтому старый `304` либо сохранённый count не считаются доказательством текущего parser-run.

Для повторной проверки уже сохранённого состояния без нового сетевого сбора можно передать `-SkipCollection`; JSON явно пометит такой отчёт как `collectionTriggered: false`. Плановый workflow этот режим не использует.

Между плановыми аудитами тот же канонический расчёт доступен в `GET /api/v1/admin/diagnostics`, закрытой React admin-панели и Prometheus. `proxyharbor_source_catalog_complete` отвечает за присутствие и включённость 320/85, а `proxyharbor_source_catalog_healthy` становится равен `1` лишь после свежего успешного непустого результата каждого встроенного feed'а. Свежесть ограничена тремя настроенными интервалами сбора и отдельно видна в `proxyharbor_builtin_sources_stale`.
