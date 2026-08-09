# Каталог источников ProxyHarbor

Встроенный каталог содержит **81 HTTPS feed-endpoint от 50 независимых проектов и сервисов**. Первые 19 провайдеров представлены несколькими протокольными потоками, ещё 31 независимый проект расширяет разнообразие входных данных. Каталог теперь выполняет оба требования: не менее 50 отдельных агрегаторов и отдельные потоки HTTP/HTTPS/SOCKS4/SOCKS5 там, где провайдер их публикует.

Последний полный production-аудит: **9 августа 2026 года**. Все **81/81** endpoint'ов были успешно обработаны одним циклом без ошибок и пустых результатов: 553 699 распознанных строк до межисточниковой дедупликации и 184 005 уникальных кандидатов за 5,75 секунды. Поскольку внешние бесплатные каталоги нестабильны, ProxyHarbor повторяет временные ошибки, сохраняет `LastError`, `LastSucceededAt`, `ConsecutiveFailures` и `LastItemCount`, а работоспособность самих прокси всегда проверяет независимо.

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

Скрипт запускает настоящий production-сбор, сохраняет машиночитаемый JSON-отчёт и завершится ошибкой, если активный источник ничего не вернул или сообщил ошибку. Workflow `Source feed audit` повторяет эту проверку еженедельно и сохраняет отчёт и API-лог как GitHub Actions artifacts.
