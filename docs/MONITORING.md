# Мониторинг и runbook

Профиль `monitoring` запускает закреплённые Prometheus и Alertmanager без root, capabilities и writable root filesystem. Prometheus опрашивает API и Alertmanager внутри Docker-сети каждые 15 секунд, хранит не более 30 дней/10 ГБ и доступен только на `127.0.0.1:${PROMETHEUS_PORT}`. Alertmanager доступен на loopback-порту `${ALERTMANAGER_PORT}`, группирует alarms и передаёт firing/resolved группы во внутренний API. API ставит их в ту же постоянную Telegram-очередь, что использует основной бот, поэтому применяются настроенные SOCKS5 failover, повторы и аудит доставки. Production Caddy возвращает 404 для внешнего `/metrics`, поэтому operational topology не публикуется в интернет.

Alertmanager получает только отдельный `ALERTMANAGER_WEBHOOK_TOKEN` через Compose secret-файл. Telegram bot token и chat ID ему не монтируются: Bot API URL с token не может попасть в журнал сетевой ошибки. Внутренний endpoint дополнительно принимает только service-host `api`, ограничивает тело 64 КиБ и 20 alerts, сравнивает Bearer token за постоянное время, экранирует Telegram HTML и дедуплицирует краткие повторы. Получателем служит выбранный в настройках backup Telegram-диалог с fallback на `TELEGRAM_CHAT_ID`. Критические alarms повторяются раз в час, warning — раз в двенадцать часов; resolved-сообщение закрывает инцидент.

Runtime HTTP SLI публикуются как `proxyharbor_http_requests_total` и cumulative `proxyharbor_http_request_duration_seconds`. Labels ограничены фиксированными группами route/status; произвольные URL, IP и заголовки никогда не попадают в time-series, а `/metrics` исключён из измерения. Counters локальны реплике и сбрасываются при рестарте, поэтому alarms используют `rate()` и суммируют все scrape targets.

Checker-auth snapshot наблюдается через `proxyharbor_checker_auth_attempts_total`, `..._failures_total`, `..._snapshot_hits_total`, `..._database_reads_total` и `..._invalidations_total`. Они не содержат node ID, IP или token labels. Устойчивый рост failures требует проверить рассинхронизацию токена/узла и попытки доступа; число database reads при штатном потоке должно быть существенно меньше числа attempts.

Все database-derived series одного scrape вычисляются в общем retry-safe PostgreSQL `REPEATABLE READ` snapshot. Concurrent collection/validation не может смешать старый proxy count с новым source/run состоянием и создать ложную комбинацию alarms; in-process HTTP/maintenance counters считываются после тех же DB-запросов и остаются локальными реплике.

Отдельная PostgreSQL lifetime-lock session каждой API-реплики проверяется bounded heartbeat каждые пять секунд. Потеря owning backend создаёт critical `RuntimeLeaseLost` и инициирует controlled shutdown; Docker restart policy восстанавливает реплику, а per-operation shared locks до остановки не позволяют write pipeline пересечься с exclusive restore.

## Проверка

```bash
curl --fail http://127.0.0.1:9090/-/ready
curl --fail http://127.0.0.1:9090/api/v1/targets
curl --fail http://127.0.0.1:9090/api/v1/rules
curl --fail http://127.0.0.1:9093/-/ready
```

После первого запуска создайте контролируемый тестовый alarm и подтвердите получение и resolved-сообщение в правильном чате. Не публикуйте Prometheus или Alertmanager UI без отдельной аутентификации.

## Реакция на alarms

| Alarm | Условие | Первое действие |
|---|---|---|
| `ProxyHarborApiDown` | scrape API неуспешен 2 минуты | Проверить `docker compose ... ps` и логи `api`, затем PostgreSQL readiness |
| `ProxyHarborAlertmanagerDown` | notification router недоступен 2 минуты | Проверить контейнер, secrets и `alertmanager` logs |
| `ProxyHarborTelegramNotificationErrors` | были ошибки отправки за 10 минут | Проверить Bot API, token/chat ID, egress и rate limit |
| `ProxyHarborBackgroundWorkersDisabled` | production workers выключены 10 минут | Проверить `BACKGROUND_WORKERS_ENABLED`; отдельная API-only replica допустима только при наличии worker-replica |
| `ProxyHarborNoPublishedProxies` | свежая выдача пуста 30 минут | Проверить control endpoint, validation queue и здоровье источников |
| `ProxyHarborPublicApiErrorRate` | более 5% публичных запросов дают 5xx 10 минут при нагрузке выше 0.1 req/s | Проверить API/PostgreSQL logs, readiness, saturation CPU/RAM и последние deploy/migration |
| `ProxyHarborPublicApiLatency` | совокупный p95 публичных запросов выше 2 секунд 15 минут при нагрузке выше 0.1 req/s | Разделить histogram по bounded `route`, проверить PostgreSQL query latency, export size и resource ceilings |
| `ProxyHarborCollectionStalled` | нет успеха дольше четырёх интервалов | Проверить последний collection audit, DNS/egress и ошибки feed |
| `ProxyHarborCollectionHung` | run активен более 30 минут | Проверить зависшие HTTP-загрузки и cluster lock; не удалять audit row вручную |
| `ProxyHarborSourceCatalogIncomplete` | отсутствует/выключена каноническая запись | Перезапустить актуальную версию для seed и проверить миграции |
| `ProxyHarborSourceCatalogUnhealthy` | менее 95% встроенных feed здоровы более часа | Открыть diagnostics и разбирать failing/stale/truncated feed по провайдеру |
| `ProxyHarborVpnSourceCatalogIncomplete` | отсутствует/выключен канонический VPN feed либо провайдер | Перезапустить актуальную версию для seed и проверить `VpnSources` |
| `ProxyHarborVpnSourceCatalogUnhealthy` | менее 95% встроенных VPN feed здоровы более часа | Открыть вкладку источников VPN и разбирать failing/stale/empty feed по провайдеру |
| `ProxyHarborCollectionTruncated` | сработал source/global limit | Проверить feed на аномалию; повышать лимит только после измерения памяти |
| `ProxyHarborProbeControlUnavailable` | control endpoint недоступен 10 минут | Проверить доверенный endpoint, DNS, TLS и исходящий firewall |
| `ProxyHarborValidationStalled` | due queue есть, попыток нет 15 минут | Проверить worker logs, leases, лимит файлов и PostgreSQL |
| `ProxyHarborValidationFailures` | не менее трёх failed batches удерживаются в пятиминутном окне пять минут | Проверить первую исходную ошибку в validation audit и доступность БД |
| `ProxyHarborValidationBacklogAtRisk` | ETA ещё не арендованной due-очереди 10 минут превышает окно публичной свежести | Проверить latency/timeout, файловые дескрипторы и CPU; после измерения увеличить concurrency либо добавить worker-replica |
| `ProxyHarborVpnValidationStalled` | due VPN-очередь есть, но завершённых проверок нет 15 минут | Проверить worker logs, DNS/egress, лимит файлов и PostgreSQL; VPN-проверка не зависит от proxy control endpoint |
| `ProxyHarborVpnValidationBacklogAtRisk` | ETA due VPN-очереди 10 минут превышает окно публичной свежести | Проверить TCP timeout, файловые дескрипторы, CPU/RAM и новые VPN concurrency/batch settings; повышать concurrency только после измерения ресурсов |
| `ProxyHarborStaleProxyRetention` | есть устаревшие неарендованные Pending/Dead, а последний успешный maintenance старше 75 минут; состояние подтверждается 15 минут | Проверить hourly maintenance, PostgreSQL delete и cluster lock; исторические адреса, которые когда-либо работали, намеренно не входят в эту метрику |
| `ProxyHarborMaintenanceFailed` | ошибка hourly retention не перекрыта успехом 15 минут | Проверить maintenance log, PostgreSQL locks/permissions и свободное место |
| `ProxyHarborBackupConfigurationUnreadable` | runtime-настройки backup не читаются 5 минут | Проверить PostgreSQL, Data Protection key-ring и запись настроек backup; до восстановления метрики используют deploy-конфигурацию |
| `ProxyHarborBackupFailed` | последний/первый backup неуспешен | Проверить место, ключ, DB snapshot и audit; transient failure повторяется через 15 минут, oversized delivery-policy — через штатный interval |
| `ProxyHarborBackupStale` | успех старше 1.5 интервалов | Запустить admin backup, затем проверить scheduler и cluster lock |
| `ProxyHarborBackupHung` | backup активен более часа | Проверить размер БД/volume и Telegram delivery; не удалять partial во время работы |
| `ProxyHarborBackupProtectionUnassessed` | при включённом routing последний завершённый run нельзя оценить 10 минут | Проверить policy snapshot, routes и adapter; нулевые счётчики при `assessed=0` не означают защиту |
| `ProxyHarborBackupRequiredCopiesUnmet` | последний routed run 10 минут не достигает обязательного quorum | Открыть состояние копий в админке; проверить failure domains, worker и доступный staging, не считать локальный файл внешней копией |
| `ProxyHarborBackupCopyOutcomeUnknown` | есть copy с неизвестным исходом 10 минут | Проверить reconciliation и provider HEAD; не повторять PUT вслепую |
| `ProxyHarborBackupDeliveryBacklog` | самая старая уже due pending job ждёт более часа ещё 10 минут | Проверить worker, lease, staging и доступность destination; отложенная retry job до `NotBefore` не считается просроченной |
| `ProxyHarborTelegramDeliveryFailed` | в legacy-режиме настроенная доставка не подтверждена | Проверить Bot API, chat ID и размер: максимум 20 частей; в routing-режиме Telegram не считается независимой verified-копией |

Новые метрики `proxyharbor_backup_latest_*` относятся только к последнему завершённому routed run. Счётчики quorum/debt имеют смысл лишь при `proxyharbor_backup_latest_routed_run_assessed=1`; legacy или некорректный snapshot не выдаётся за защищённый. Отдельно публикуются агрегированные UNKNOWN/manual-review copies, pending/reconciling jobs, возраст самой старой уже due pending job и время последней успешной изолированной restore-проверки. Список destination, locator, имена аккаунтов и секреты в labels не выводятся. Это пока не метрика времени последнего защищённого backup и не утверждённый RPO/DR SLO.

Alert rules находятся в `deploy/prometheus/alerts.yml`, а `alerts.test.yml` фиксирует grace periods и guards. Telegram route и HTML-шаблон находятся в `deploy/alertmanager`. После изменения интервалов приложения правила используют опубликованные configuration metrics; фиксированные пороги длительных операций при необходимости меняйте осознанно и повторно запускайте `promtool test rules` и `amtool check-config`.
