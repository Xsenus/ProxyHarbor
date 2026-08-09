# Каталог источников ProxyHarbor

Встроенный каталог содержит **81 HTTPS feed-endpoint от 50 независимых проектов и сервисов**. Первые 19 провайдеров представлены несколькими протокольными потоками, ещё 31 независимый проект расширяет разнообразие входных данных. Каталог теперь выполняет оба требования: не менее 50 отдельных агрегаторов и отдельные потоки HTTP/HTTPS/SOCKS4/SOCKS5 там, где провайдер их публикует.

Последний полный live-аудит: **9 августа 2026 года**. Все **81/81** endpoint'ов были успешно обработаны одним циклом без ошибок и пустых результатов: 544 139 распознанных строк и 217 251 уникальный кандидат после межисточниковой дедупликации. Реальный validator проверил контрольную партию из 1 000 адресов и нашёл 5 живых на момент запуска; выдача этой выборки была отдельно разобрана как корректные JSON, XML, TXT и CSV. Поскольку внешние бесплатные каталоги нестабильны, ProxyHarbor повторяет временные ошибки, сохраняет `LastError`, `LastSucceededAt`, `ConsecutiveFailures` и `LastItemCount`, а работоспособность самих прокси всегда проверяет независимо.

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

Скрипт запускает настоящий production-сбор, сохраняет машиночитаемый JSON-отчёт и завершится ошибкой, если активный источник ничего не вернул или сообщил ошибку. Отдельный completeness-gate требует наличия и включённости всех 81 встроенных feed'ов и ровно 50 заявленных провайдеров, поэтому скрыть сломанный источник его отключением нельзя; пользовательские feed'ы не искажают эту проверку. Workflow `Source feed audit` повторяет аудит еженедельно и сохраняет отчёт и API-лог как GitHub Actions artifacts.

Содержимое каждого feed'а считается недоверенным: загрузка ограничена 10 МБ, parser работает без backtracking, принимает только публичные IP и останавливается при достижении `Collector__MaxProxiesPerSource`. Поэтому источник с миллионами строк не обходит эксплуатационные лимиты созданием полной промежуточной коллекции.

Для повторной проверки уже сохранённого состояния без нового сетевого сбора можно передать `-SkipCollection`; JSON явно пометит такой отчёт как `collectionTriggered: false`. Плановый workflow этот режим не использует.

Между плановыми аудитами тот же канонический расчёт доступен в `GET /api/v1/admin/diagnostics`, React-панели и Prometheus. `proxyharbor_source_catalog_complete` отвечает только за присутствие и включённость 81/50, а `proxyharbor_source_catalog_healthy` становится равен `1` лишь после успешного непустого результата каждого встроенного feed'а. Поэтому начальное состояние до первого сбора отличается от настоящего отказа и не маскируется общими пользовательскими источниками.
