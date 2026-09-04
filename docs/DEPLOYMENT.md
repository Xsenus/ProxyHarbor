# Production-развёртывание

## До запуска

1. Установите Docker Engine 26+ и Docker Compose 2.24.4+.
2. Направьте DNS A/AAAA для выбранного имени на сервер. Если IPv6 фактически не маршрутизируется, не создавайте AAAA-запись.
3. Разрешите входящие TCP 80/443 и UDP 443. PostgreSQL, API и порт frontend наружу не публикуются.
4. Скопируйте `.env.example` в `.env`, замените все обязательные ключи и задайте `PUBLIC_HOST` без схемы/пути и `ACME_EMAIL`. Production overlay принудительно включает backup и использует Telegram как безопасный bootstrap-канал, поэтому пустые `BACKUP_ENCRYPTION_KEY`, `TELEGRAM_BOT_TOKEN` или `TELEGRAM_CHAT_ID` fail-closed остановят первый запуск. Для monitoring profile также задайте независимый случайный `ALERTMANAGER_WEBHOOK_TOKEN` длиной не менее 32 символов. После сохранения runtime-настроек в `/admin/backups` основным каналом может быть S3, а Telegram — отключён. Compose смонтирует чувствительные значения как read-only secrets; в environment контейнеров они не передаются.
5. До публичного запуска проверьте условия использования всех источников и ограничьте admin routes дополнительным firewall/WAF, если панель не должна быть общедоступной.

## Запуск и проверка

Если production использует локальный `docker-compose.server.yml` с file-backed
secrets, каталог должен принадлежать `root`, иметь режим `0700`, а каждый
активно используемый контейнером файл — `0444` (отключённые Telegram bootstrap-файлы
могут оставаться `0440`). Это не делает значения общедоступными на хосте:
закрытый каталог не позволяет другим пользователям пройти к файлам, но read-only
bind mount остаётся читаемым для непривилегированных UID API и Alertmanager.
Перед `docker compose up` обязательный read-only preflight проверяет владельца,
права, отсутствие symlink/hardlink, размеры, обязательные значения и никогда не
печатает содержимое:

```bash
./tools/Check-ProductionSecrets.sh --directory /opt/proxyharbor/.secrets --expected-owner 0
```

```bash
export PROXYHARBOR_SOURCE_REVISION="$(git rev-parse --verify HEAD)"
docker compose -f docker-compose.yml -f docker-compose.production.yml config
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d --build
docker compose -f docker-compose.yml -f docker-compose.production.yml ps
curl --fail https://proxy.example.com/health/ready
curl --fail https://proxy.example.com/api/v1/stats
```

После переключения трафика выполните единый fail-closed аудит публичного запуска. Он проверяет TLS, security headers, canonical/robots/JSON-LD всех индексируемых страниц, sitemap, RFC 9116 `security.txt`, `X-Robots-Tag` закрытых маршрутов, настоящий 404, отсутствие выдачи `.env`/Git/Compose/config/Swagger/metrics, отказ недоверенному CORS origin, отключённый TRACE, readiness и наличие хотя бы одного доступного платёжного провайдера. JSON-отчёт сохраняйте вместе с журналом выпуска:

```powershell
./tools/Audit-PublicLaunch.ps1 `
  -BaseUrl https://proxy.example.com `
  -MinimumTlsDays 14 `
  -ReportPath artifacts/public-launch-audit.json
```

Перед включением реальных продаж отдельно подтвердите, что каждый включённый внешний шлюз работает не только по конфигурации, но уже имеет успешную production-оплату, не оставил pending-заказы и использует HTTPS webhook текущего домена. Передайте ключ через закрытый файл, а не через аргумент процесса:

```powershell
./tools/Audit-PaymentReadiness.ps1 `
  -ApiBaseUrl https://proxy.blagodaty.ru `
  -AdminKeyFile /opt/proxyharbor/.secrets/admin_api_key `
  -ReportPath artifacts/payment-readiness.json
```

Состояния `webhook_attention`, `no_successful_payments`, `pending`, тестовый режим или отсутствие обязательных реквизитов завершают аудит ошибкой. Для первоначальной настройки без приёма денег можно временно передать `-AllowAwaitingFirstPayment`, но это не является production acceptance: перед продажами всё равно нужна реальная минимальная оплата, корректное начисление подписки, чек и проверенный возврат.

Успешный результат подтверждает наблюдаемое техническое состояние сайта, но не заменяет внешние уведомления Роскомнадзора, договоры с обработчиками, чек НПД и юридическую квалификацию сервиса.

Не добавляйте `https://` в `PUBLIC_HOST`. Первый выпуск сертификата требует корректного DNS и доступности портов 80/443. Named volumes `caddy-data` и `caddy-config` содержат сертификаты и ACME-состояние; не удаляйте их при обычном обновлении.

Миграции и синхронизация встроенного source-каталога сериализованы PostgreSQL session advisory lock. Если startup завершается ошибкой, первичная migration/seed-ошибка не заменяется вторичным сбоем unlock/close; сессия с неподтверждённым освобождением немедленно удаляется из Npgsql pool и физически закрывается, поэтому следующая реплика не наследует залипший lock. Диагностируйте исходную startup-ошибку и повторяйте запуск только после устранения её причины.

Проверьте отдельно admin-аутентификацию, создание зашифрованного backup, подтверждённую внешнюю доставку, расшифровку и пробное восстановление в отдельную БД. Команда `./tools/Audit-Backup.ps1 -ApiBaseUrl https://proxy.example.com -AdminKey $ADMIN_KEY -ReportPath artifacts/backup-audit.json` запускает конкретный backup и fail-closed требует канонический непустой PHB3, завершённый persisted audit и подтверждение хотя бы одного внешнего канала (`sentToObjectStorage` либо `sentToTelegram`); `-AllowLocalOnly` предназначен только для явно выбранного локального canary. Настройте внешний мониторинг `/health/ready`, срока TLS-сертификата, свободного диска, PostgreSQL и ключевых Prometheus-метрик.

До аварийной замены данных извлеките безопасную конфигурацию из backup v7 без подключения к БД и сохраните её вне временного restore-контейнера:

```bash
docker compose --profile tools run --rm --no-deps -T restore \
  --input /app/backups/proxyharbor-YYYYMMDD-HHMMSS.phbackup \
  --inspect-settings > recovery-settings.json
jq --exit-status '.manifest.version == 7 and .manifest.secretsIncluded == false' recovery-settings.json
```

Снимок предназначен для операторской сверки и не применяется автоматически. Он содержит collector/backup/runtime, CORS, trusted proxy и logging settings, но никогда не содержит admin password/API key, PostgreSQL connection string, Telegram credentials, data-protection keys или encryption key. Эти значения восстановите из внешнего secret store, затем выполните пробный restore в отдельную БД.

Для замены production-БД сначала остановите все API-реплики, выполните restore и только после успеха верните сервисы:

```bash
docker compose stop web api
docker compose --profile tools run --rm restore \
  --input /app/backups/proxyharbor-YYYYMMDD-HHMMSS.phbackup \
  --replace-existing-data
docker compose up -d api web
curl --fail https://proxy.example.com/health/ready
```

Это требование проверяется самой БД, а не только runbook: каждая API-реплика держит shared PostgreSQL lifetime-lease, restore — exclusive lease на весь migration+replace pipeline. Каждый collector/validator/backup/maintenance/source-mutation дополнительно защищён short-lived shared operation-lease, поэтому серверная потеря lifetime-сессии не позволяет текущей или новой записи пересечься с restore. Owning session проверяется bounded heartbeat каждые пять секунд; её разрыв создаёт critical log и controlled shutdown, после которого `restart: unless-stopped` поднимает новую реплику. Shutdown вызывается в `finally` даже при исключении logging provider; bounded stderr fallback не печатает connection details, а monitor task не скрывает primary host failure. Пока жива хотя бы одна реплика или операция, restore завершится без изменения данных; пока идёт restore, новая API-реплика и новые write pipelines не стартуют. Несколько обычных API-реплик и их операции совместимы друг с другом.

Успешное сообщение restore появляется только после подтверждённого удаления временного plaintext snapshot. Если stderr указывает cleanup-сбой, исходная ошибка или cancellation и её exit code сохранены; удалите названный каталог вручную. Если других ошибок не было, транзакция могла уже завершиться: перед повторным restore сначала проверьте целевую БД, чтобы не выполнять destructive replacement вслепую. Для Compose `run --rm` удаление контейнера дополнительно удаляет анонимный `/restore-temp`, но операторское предупреждение всё равно считается инцидентом обращения с plaintext.

## Обновление и откат

Перед обновлением создайте и проверьте backup. Затем получите новую версию,
обновите `PROXYHARBOR_SOURCE_REVISION` из фактического Git HEAD и повторите
production-команду `up -d --build`; постоянные volumes не заменяются. Не храните
статическую ревизию в `.env`: переменная процесса имеет приоритет и исключает
ложную версию бинарника после следующего `git pull`.

```bash
./tools/Check-ProductionSecrets.sh --directory /opt/proxyharbor/.secrets --expected-owner 0
export PROXYHARBOR_SOURCE_REVISION="$(git rev-parse --verify HEAD)"
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d --build
```

Для диагностики используйте:

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml logs --tail 200 api caddy
```

Создавайте предрелизный PostgreSQL-снимок закрытым инструментом, а не обычным
перенаправлением `pg_dump > file` (при стандартном umask файл может быть доступен
другим пользователям VPS):

```bash
./tools/Create-PredeployBackup.sh --directory /opt/proxyharbor --revision "$(git rev-parse HEAD)"
```

Каталог должен быть абсолютным, каноническим, принадлежать запускающему
пользователю и не разрешать запись группе или остальным. Архив имеет `0600` уже
при записи; после успешного `pg_dump` проверяется непустой файл и каталог архива
через `pg_restore --list`. Только затем он атомарно получает окончательное имя,
без перезаписи существующей копии или символической ссылки. Ошибочная временная
копия удаляется. Проверка каталога архива не заменяет пробное восстановление.
Это незашифрованная локальная копия БД: не публикуйте её и не используйте вместо
штатного зашифрованного экспорта резервных копий.

Ручные PostgreSQL-снимки `predeploy-<commit>-<timestamp>.dump` не должны
накапливаться без ограничения рядом с checkout. Перед удалением сначала
посмотрите точный список кандидатов; инструмент по умолчанию ничего не меняет:

```bash
./tools/Prune-PredeployBackups.sh --directory /opt/proxyharbor --keep-count 7
```

После проверки списка примените тот же расчёт явно. Семь самых свежих управляемых
копий сохраняются; пользовательские `.dump`, неканонические имена, вложенные
каталоги и символические ссылки не затрагиваются. До первого удаления инструмент
обязательно проверяет каталог каждого сохраняемого архива через `pg_restore`:

```bash
./tools/Prune-PredeployBackups.sh --directory /opt/proxyharbor --keep-count 7 --apply
```

На постоянном production-хосте установите поставляемый timer. Он запускается
ежедневно с рандомизированной задержкой, низким CPU/I/O-приоритетом и sandbox,
которому разрешена запись только в `/opt/proxyharbor`:

```bash
install -m 0644 deploy/systemd/proxyharbor-predeploy-retention.service /etc/systemd/system/
install -m 0644 deploy/systemd/proxyharbor-predeploy-retention.timer /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now proxyharbor-predeploy-retention.timer
systemctl list-timers proxyharbor-predeploy-retention.timer
```

Ошибочный архив завершает oneshot с ошибкой до удаления. Проверяйте результат
через `systemctl status proxyharbor-predeploy-retention.service` и журнал
`journalctl -u proxyharbor-predeploy-retention.service`.

Откат выполняйте на предыдущий проверенный Git commit той же командой. Не откатывайте код через уже применённые необратимые миграции без заранее проверенного плана восстановления БД.

Для версионированного GHCR-релиза добавьте `docker-compose.release.yml` между base и production-файлами и задайте `PROXYHARBOR_IMAGE_PREFIX`/`PROXYHARBOR_IMAGE_TAG`. Такой запуск использует опубликованные multi-architecture manifests и не содержит локальных build-секций. Точные digest находятся в приложенном `proxyharbor-release.json`; полный порядок выпуска и attestation-проверки приведён в [RELEASING.md](RELEASING.md).

По умолчанию Caddy должен быть непосредственной публичной точкой входа. `docker-compose.production.yml` передаёт `PUBLIC_HOST` одновременно Caddy и API `AllowedHosts`, разрешает `127.0.0.1` исключительно для внутреннего healthcheck frontend-контейнера и Docker DNS-имя `api` для прямого scrape из Prometheus; API остаётся доступен только внутри backend network. Production startup отклоняет пустой allowlist и `*`, а неизвестный Host получает 400. Container healthcheck Caddy обращается к некэшируемому `/health/ready` через внутренний HTTPS-порт с тем же SNI/Host. Readiness выполняет zero-row проверку пяти operational tables и актуальных колонок, поэтому Docker видит полный работоспособный маршрут TLS → gateway → API → совместимая PostgreSQL-схема, а не только живой процесс или открытый socket. При добавлении CDN/load balancer настройте доверенные proxy ranges одновременно в Caddy и API; иначе rate limiting будет видеть адрес промежуточного узла. Никогда не доверяйте произвольному `X-Forwarded-For` из интернета.

Если 80/443 уже обслуживает системный Nginx на shared-хосте, используйте
`deploy/nginx/proxyharbor.conf`: он публикует только loopback-порты 18080/18081,
не доверяет входящему `X-Forwarded-For`, скрывает версию gateway и добавляет
per-IP connection/request limits с отдельными зонами только для ProxyHarbor.
Перед заменой обязательно сохраните текущий vhost, выполните `nginx -t` и только
после успешной проверки сделайте reload; не применяйте этот файл к другому домену
без замены hostname и путей сертификата. Compose-оверлей `docker-compose.nginx.yml`
обязательно указывайте последним: production-оверлей намеренно удаляет frontend-port
для собственного Caddy, а последний оверлей безопасно возвращает только loopback
18080/18081. Запускайте явный список сервисов, чтобы Caddy не пытался занять уже
используемые 80/443:

```bash
export PROXYHARBOR_SOURCE_REVISION="$(git rev-parse --verify HEAD)"
docker compose \
  -f docker-compose.yml \
  -f docker-compose.production.yml \
  -f docker-compose.server.yml \
  -f docker-compose.nginx.yml \
  up -d --build postgres api web
curl --fail http://127.0.0.1:18080/
curl --fail http://127.0.0.1:18081/health/ready
curl --fail https://proxy.example.com/health/ready
```

При обычном обновлении уже работающей БД используйте тот же порядок и
`up -d --build --no-deps api web`; это не пересоздаёт PostgreSQL.

## Встроенный мониторинг

Opt-in профиль запускает Prometheus с 30-дневным/10-ГБ bounded retention и Alertmanager с Telegram-маршрутом. До запуска задайте bot token и числовой chat ID (для group/channel обычно отрицательный):

`ProxyHarborAdvisoryLockCleanupFailure` срабатывает при любом росте process-local счётчика cleanup-инцидентов. Это означает, что реплика уже исключила неоднозначную PostgreSQL lock-сессию из pool; проверьте состояние БД/сети и перезапустите затронутую реплику после устранения причины.

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile monitoring up -d
curl --fail http://127.0.0.1:9090/-/ready
curl --fail http://127.0.0.1:9093/-/ready
```

Оба порта привязаны только к loopback. Для удалённого просмотра используйте SSH tunnel, а не открывайте UI в интернет. История метрик находится в `prometheus-data`, silences/notification log — в `alertmanager-data`. Token и chat ID монтируются из Compose secrets и в эти volumes не записываются. Runbook и alarms описаны в [MONITORING.md](MONITORING.md).
