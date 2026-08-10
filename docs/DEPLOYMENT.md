# Production-развёртывание

## До запуска

1. Установите Docker Engine 26+ и Docker Compose 2.24.4+.
2. Направьте DNS A/AAAA для выбранного имени на сервер. Если IPv6 фактически не маршрутизируется, не создавайте AAAA-запись.
3. Разрешите входящие TCP 80/443 и UDP 443. PostgreSQL, API и порт frontend наружу не публикуются.
4. Скопируйте `.env.example` в `.env`, замените все обязательные ключи и задайте `PUBLIC_HOST` без схемы/пути и `ACME_EMAIL`. Compose смонтирует чувствительные значения как read-only secrets; в environment контейнеров они не передаются.
5. До публичного запуска проверьте условия использования всех источников и ограничьте admin routes дополнительным firewall/WAF, если панель не должна быть общедоступной.

## Запуск и проверка

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml config
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d --build
docker compose -f docker-compose.yml -f docker-compose.production.yml ps
curl --fail https://proxy.example.com/health/ready
curl --fail https://proxy.example.com/api/v1/stats
```

Не добавляйте `https://` в `PUBLIC_HOST`. Первый выпуск сертификата требует корректного DNS и доступности портов 80/443. Named volumes `caddy-data` и `caddy-config` содержат сертификаты и ACME-состояние; не удаляйте их при обычном обновлении.

Проверьте отдельно admin-аутентификацию, создание зашифрованного backup, подтверждённую Telegram-доставку, расшифровку и пробное восстановление в отдельную БД. Команда `./tools/Audit-Backup.ps1 -ApiBaseUrl https://proxy.example.com -AdminKey $ADMIN_KEY -ReportPath artifacts/backup-audit.json` запускает конкретный backup и fail-closed требует канонический непустой PHB3, завершённый persisted audit и `sentToTelegram=true`; `-AllowLocalOnly` предназначен только для явно выбранного локального canary. Настройте внешний мониторинг `/health/ready`, срока TLS-сертификата, свободного диска, PostgreSQL и ключевых Prometheus-метрик.

До аварийной замены данных извлеките безопасную конфигурацию из backup v5 без подключения к БД и сохраните её вне временного restore-контейнера:

```bash
docker compose --profile tools run --rm --no-deps -T restore \
  --input /app/backups/proxyharbor-YYYYMMDD-HHMMSS.phbackup \
  --inspect-settings > recovery-settings.json
jq --exit-status '.manifest.version == 5 and .manifest.secretsIncluded == false' recovery-settings.json
```

Снимок предназначен для операторской сверки и не применяется автоматически. Он содержит collector/backup/runtime, CORS, trusted proxy и logging settings, но никогда не содержит admin key, PostgreSQL connection string, Telegram credentials или encryption key. Эти значения восстановите из внешнего secret store, затем выполните пробный restore в отдельную БД.

Для замены production-БД сначала остановите все API-реплики, выполните restore и только после успеха верните сервисы:

```bash
docker compose stop web api
docker compose --profile tools run --rm restore \
  --input /app/backups/proxyharbor-YYYYMMDD-HHMMSS.phbackup \
  --replace-existing-data
docker compose up -d api web
curl --fail https://proxy.example.com/health/ready
```

Это требование проверяется самой БД, а не только runbook: каждая API-реплика держит shared PostgreSQL lifetime-lease, restore — exclusive lease на весь migration+replace pipeline. Каждый collector/validator/backup/maintenance/source-mutation дополнительно защищён short-lived shared operation-lease, поэтому серверная потеря lifetime-сессии не позволяет текущей или новой записи пересечься с restore. Owning session проверяется bounded heartbeat каждые пять секунд; её разрыв создаёт critical log и controlled shutdown, после которого `restart: unless-stopped` поднимает новую реплику. Пока жива хотя бы одна реплика или операция, restore завершится без изменения данных; пока идёт restore, новая API-реплика и новые write pipelines не стартуют. Несколько обычных API-реплик и их операции совместимы друг с другом.

Успешное сообщение restore появляется только после подтверждённого удаления временного plaintext snapshot. Если stderr указывает cleanup-сбой, исходная ошибка или cancellation и её exit code сохранены; удалите названный каталог вручную. Если других ошибок не было, транзакция могла уже завершиться: перед повторным restore сначала проверьте целевую БД, чтобы не выполнять destructive replacement вслепую. Для Compose `run --rm` удаление контейнера дополнительно удаляет анонимный `/restore-temp`, но операторское предупреждение всё равно считается инцидентом обращения с plaintext.

## Обновление и откат

Перед обновлением создайте и проверьте backup. Затем получите новую версию и повторите production-команду `up -d --build`; постоянные volumes не заменяются. Для диагностики используйте:

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml logs --tail 200 api caddy
```

Откат выполняйте на предыдущий проверенный Git commit той же командой. Не откатывайте код через уже применённые необратимые миграции без заранее проверенного плана восстановления БД.

Для версионированного GHCR-релиза добавьте `docker-compose.release.yml` между base и production-файлами и задайте `PROXYHARBOR_IMAGE_PREFIX`/`PROXYHARBOR_IMAGE_TAG`. Такой запуск использует опубликованные multi-architecture manifests и не содержит локальных build-секций. Точные digest находятся в приложенном `proxyharbor-release.json`; полный порядок выпуска и attestation-проверки приведён в [RELEASING.md](RELEASING.md).

По умолчанию Caddy должен быть непосредственной публичной точкой входа. `docker-compose.production.yml` передаёт `PUBLIC_HOST` одновременно Caddy и API `AllowedHosts`; Production startup отклоняет пустой allowlist и `*`, а неизвестный Host получает 400. Container healthcheck Caddy обращается к некэшируемому `/health/ready` через внутренний HTTPS-порт с тем же SNI/Host. Readiness выполняет zero-row проверку пяти operational tables и актуальных колонок, поэтому Docker видит полный работоспособный маршрут TLS → gateway → API → совместимая PostgreSQL-схема, а не только живой процесс или открытый socket. При добавлении CDN/load balancer настройте доверенные proxy ranges одновременно в Caddy и API; иначе rate limiting будет видеть адрес промежуточного узла. Никогда не доверяйте произвольному `X-Forwarded-For` из интернета.

## Встроенный мониторинг

Opt-in профиль запускает Prometheus с 30-дневным/10-ГБ bounded retention и Alertmanager с Telegram-маршрутом. До запуска задайте bot token и числовой chat ID (для group/channel обычно отрицательный):

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile monitoring up -d
curl --fail http://127.0.0.1:9090/-/ready
curl --fail http://127.0.0.1:9093/-/ready
```

Оба порта привязаны только к loopback. Для удалённого просмотра используйте SSH tunnel, а не открывайте UI в интернет. История метрик находится в `prometheus-data`, silences/notification log — в `alertmanager-data`. Token и chat ID монтируются из Compose secrets и в эти volumes не записываются. Runbook и alarms описаны в [MONITORING.md](MONITORING.md).
