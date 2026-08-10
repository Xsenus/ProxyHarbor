# Каталог источников ProxyHarbor

Просматриваемая таблица всех провайдеров с прямыми ссылками вынесена в [`SOURCE_CATALOG.md`](SOURCE_CATALOG.md); этот документ описывает эксплуатационные гарантии и воспроизводимый аудит.

Встроенный каталог содержит **81 HTTPS feed-endpoint от 50 независимых проектов и сервисов**. Первые 19 провайдеров представлены несколькими протокольными потоками, ещё 31 независимый проект расширяет разнообразие входных данных. Каталог теперь выполняет оба требования: не менее 50 отдельных агрегаторов и отдельные потоки HTTP/HTTPS/SOCKS4/SOCKS5 там, где провайдер их публикует.

Последний полный source-аудит: **10 августа 2026 года, 23:47 (Asia/Novosibirsk)**. Все **81/81** endpoint'ов от **50/50** провайдеров были успешно обработаны одним production-циклом на отдельной чистой схеме PostgreSQL за 5,232 секунды без ошибок, пустых результатов, skip или усечения: 888 134 распознанных строк и 290 039 уникальных кандидатов после межисточниковой дедупликации. Предшествующий полный validation/export-аудит в 22:47 отдельно выполнил 1 600 объективных HTTPS-проверок без `Deferred`, нашёл 6 рабочих адресов и без расхождений опубликовал живой набор как JSON, XML, TXT и CSV. Оба запуска подтвердили, что предел 500 000 полностью обрабатывает крупные Rix4Uni, ProxRipper и Fyvri и не достигает общего лимита 500 000 кандидатов. Еженедельный `source-feed-audit` воспроизводит полный сбор и отдельную validation-партию. Поскольку внешние бесплатные каталоги нестабильны, ProxyHarbor повторяет временные ошибки, сохраняет `LastError`, `LastSucceededAt`, `ConsecutiveFailures`, `LastItemCount` и признаки полноты, а работоспособность и текущая производительность validator наблюдаются независимо. Каноническая дата `LastAuditedOn` публикуется в diagnostics, React-панели и метрике `proxyharbor_builtin_catalog_audit_timestamp_seconds`.

| Провайдер | Feed'ов | Протоколы |
|---|---:|---|
| ProxyScrape | 4 | Mixed, HTTP, SOCKS4, SOCKS5 |
| OpenProxyList | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 |
| Proxifly | 4 | HTTP, HTTPS, SOCKS4, SOCKS5 |
| TheSpeedX | 3 | HTTP, SOCKS4, SOCKS5 |
| IPLocate | 3 | HTTP, SOCKS4, SOCKS5 |
| Databay Labs | 3 | HTTP, SOCKS4, SOCKS5 |
| TuanMinPay | 3 | HTTP, SOCKS4, SOCKS5 |
| HProxy | 3 | HTTP, SOCKS4, SOCKS5 |
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

Дополнительные независимые провайдеры (по одному прошедшему аудит feed'у): CyberH4ck3r, Proxmint, Rix4Uni, Komutan234, Zaeem20, WebUnblocker, Watchttvv, VPSLab, VMHeaven, GProxyNet, Anutmagang, ProxRipper, RoosterKid, Proxy-Free, Ch4120N, XYZS996, Tianndev, KangProxy, Thordata, ErcinDedeoglu, Skillter, ClarkTM, Sunny9577, HookzOf, Vakhov, ShiftyTR, Fyvri, BesJS, TheRituRajPS, Stormsia и MrMarble.

Канонические URL, ранги и протоколы находятся в `BuiltInSourceCatalog.cs`. На старте сервис идемпотентно добавляет недостающие встроенные feed'ы даже в существующую БД, обновляет их метаданные, но сохраняет ручной выбор администратора `Enabled/Disabled` и никогда не удаляет пользовательские источники.

Для повторного живого аудита после запуска контейнеров:

```powershell
./tools/Audit-SourceFeeds.ps1 -AdminKey 'значение ADMIN_API_KEY' -ReportPath './source-audit.json'
```

Скрипт запускает настоящий production-сбор, сохраняет машиночитаемый JSON-отчёт даже при раннем отказе и завершится ошибкой, если admin-команда не вернула завершённый цикл, хотя бы один активный источник не был заново обработан именно в этом цикле, его результат пуст/ошибочен/усечён, forced-run пропустил источник, не создал ни одного уникального кандидата, счётчики `CollectionRun` расходятся с состоянием feed'ов либо достигнут `MaxCandidatesPerRun`. Artifact отдельно сохраняет длительность, число обработанных/пропущенных источников, уникальных кандидатов и новых строк БД, поэтому полноту и производительность можно проверить без чтения свободного текста логов. Отдельный completeness-gate требует наличия и включённости всех 81 встроенных feed'ов и ровно 50 независимых origin-владельцев, поэтому скрыть сломанный или неполный источник его отключением нельзя. Независимость считается не по свободному display-name: `providerIdentity` канонически равен GitHub owner для `raw.githubusercontent.com` либо отдельному DNS hostname для остальных feed'ов; тест запрещает связывать один label с несколькими identity и наоборот. Пользовательские feed'ы не искажают эту проверку. Workflow `Source feed audit` повторяет аудит еженедельно и сохраняет отчёт и API-лог как GitHub Actions artifacts; отсутствие любого ожидаемого artifact также считается ошибкой. Основной CI запускает локальный HTTP contract-test и доказывает принятие свежего согласованного цикла, проверку сохранённого состояния, отклонение stale/empty/skipped evidence и сохранение отчёта при ранней HTTP-ошибке.

Содержимое каждого feed'а считается недоверенным: загрузка ограничена 10 МБ, parser работает без backtracking, принимает только публичные IP и не хранит больше `Collector__MaxProxiesPerSource`. Первый следующий уникальный адрес выставляет постоянный признак усечения; дубликаты сверх лимита не считаются потерянными данными. Межисточниковый `Collector__MaxCandidatesPerRun` обладает той же точной семантикой при конкурентной загрузке: сигнал появляется только для реально отброшенного уникального endpoint, но не при точном заполнении или повторе уже известного адреса. Поэтому источник с миллионами строк не обходит эксплуатационные лимиты созданием полной промежуточной коллекции, но и не может выглядеть полностью обработанным.

Ответы с `text/html`, `application/xhtml+xml` либо HTML envelope отклоняются даже при HTTP 200 и наличии похожей на `IP:port` строки. Поэтому login, WAF и error page не могут ложно пометить источник успешным и добавить служебный адрес как proxy-кандидат; отсутствующий media type, plain text, JSON и binary feed остаются совместимыми.

Временные 429/5xx и транспортные сбои повторяются с bounded jitter/backoff. Ответ с непрочитанным body закрывается до ожидания повтора, поэтому недоступный провайдер не удерживает ограниченные connection-pool slots во время паузы.

Успешный `200` сохраняет bounded HTTP validators `ETag` и `Last-Modified` в строке источника, backup и restore. Следующий запрос передаёт их как `If-None-Match`/`If-Modified-Since`; `304 Not Modified` считается свежей успешной проверкой, сохраняет прежние count/truncation и не создаёт кандидатов или сетевой/CPU расход повторного body. Отдельный `LastContentFetchedAt` не меняется на `304`. Когда полный body старше половины membership-retention, но максимум суток, collector выполняет unconditional fetch: это предотвращает необратимую потерю удалённого Pending/Dead proxy из feed с вечным неизменным ETag. Ответ `304` принимается только после прежнего успешного непустого результата и реально отправленного validator. При смене URL/protocol validators и дата полного body сбрасываются, а malformed или содержащий control characters ETag отклоняется fail-closed.

Ручной `POST /api/v1/admin/collect` и вызывающий его source-аудит не отправляют conditional validators: каждый feed обязан вернуть body. JSON и gate требуют `LastContentFetchedAt` каждого enabled source внутри `StartedAt…FinishedAt`, поэтому старый `304` либо сохранённый count не считаются доказательством текущего parser-run.

Для повторной проверки уже сохранённого состояния без нового сетевого сбора можно передать `-SkipCollection`; JSON явно пометит такой отчёт как `collectionTriggered: false`. Плановый workflow этот режим не использует.

Между плановыми аудитами тот же канонический расчёт доступен в `GET /api/v1/admin/diagnostics`, React-панели и Prometheus. `proxyharbor_source_catalog_complete` отвечает только за присутствие и включённость 81/50, а `proxyharbor_source_catalog_healthy` становится равен `1` лишь после свежего успешного непустого результата каждого встроенного feed'а. Свежесть ограничена тремя настроенными интервалами сбора и отдельно видна в `proxyharbor_builtin_sources_stale`, поэтому остановившийся collector не оставляет исторически зелёный статус. Начальное состояние до первого сбора отличается от настоящего отказа и не маскируется общими пользовательскими источниками.
