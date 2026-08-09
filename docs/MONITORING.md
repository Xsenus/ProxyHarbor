# Мониторинг и runbook

Профиль `monitoring` запускает закреплённый distroless Prometheus без root, capabilities и writable root filesystem. Он опрашивает API внутри Docker-сети каждые 15 секунд, хранит не более 30 дней/10 ГБ и доступен только на `127.0.0.1:${PROMETHEUS_PORT}`. Production Caddy возвращает 404 для внешнего `/metrics`, поэтому operational topology не публикуется в интернет.

## Проверка

```bash
curl --fail http://127.0.0.1:9090/-/ready
curl --fail http://127.0.0.1:9090/api/v1/targets
curl --fail http://127.0.0.1:9090/api/v1/rules
```

Prometheus вычисляет предупреждения, но сам не отправляет уведомления. Подключите существующий Alertmanager/monitoring backend и проверяйте весь путь доставки тестовым alarm. Не публикуйте Prometheus UI без отдельной аутентификации.

## Реакция на alarms

| Alarm | Условие | Первое действие |
|---|---|---|
| `ProxyHarborApiDown` | scrape API неуспешен 2 минуты | Проверить `docker compose ... ps` и логи `api`, затем PostgreSQL readiness |
| `ProxyHarborBackgroundWorkersDisabled` | production workers выключены 10 минут | Проверить `BACKGROUND_WORKERS_ENABLED`; отдельная API-only replica допустима только при наличии worker-replica |
| `ProxyHarborNoPublishedProxies` | свежая выдача пуста 30 минут | Проверить control endpoint, validation queue и здоровье источников |
| `ProxyHarborCollectionStalled` | нет успеха дольше четырёх интервалов | Проверить последний collection audit, DNS/egress и ошибки feed |
| `ProxyHarborCollectionHung` | run активен более 30 минут | Проверить зависшие HTTP-загрузки и cluster lock; не удалять audit row вручную |
| `ProxyHarborSourceCatalogIncomplete` | отсутствует/выключена каноническая запись | Перезапустить актуальную версию для seed и проверить миграции |
| `ProxyHarborSourceCatalogUnhealthy` | каталог нездоров более часа | Открыть diagnostics и разбирать failing/stale/truncated feed по провайдеру |
| `ProxyHarborCollectionTruncated` | сработал source/global limit | Проверить feed на аномалию; повышать лимит только после измерения памяти |
| `ProxyHarborProbeControlUnavailable` | control endpoint недоступен 10 минут | Проверить доверенный endpoint, DNS, TLS и исходящий firewall |
| `ProxyHarborValidationStalled` | due queue есть, попыток нет 15 минут | Проверить worker logs, leases, лимит файлов и PostgreSQL |
| `ProxyHarborValidationFailures` | failed batches за последние 5 минут | Проверить первую исходную ошибку в validation audit и доступность БД |
| `ProxyHarborBackupFailed` | последний/первый backup неуспешен | Проверить свободное место, ключ, DB snapshot и backup audit |
| `ProxyHarborBackupStale` | успех старше 1.5 интервалов | Запустить admin backup, затем проверить scheduler и cluster lock |
| `ProxyHarborBackupHung` | backup активен более часа | Проверить размер БД/volume и Telegram delivery; не удалять partial во время работы |
| `ProxyHarborTelegramDeliveryFailed` | настроенная доставка не подтверждена | Проверить Bot API, chat ID, лимит частей и повтор; локальный encrypted backup сохранить |

Alert rules находятся в `deploy/prometheus/alerts.yml`, а `alerts.test.yml` фиксирует grace periods и guards. После изменения интервалов приложения правила используют опубликованные configuration metrics; фиксированные пороги длительных операций при необходимости меняйте осознанно и повторно запускайте `promtool test rules`.
