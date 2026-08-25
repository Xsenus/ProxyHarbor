# Конфигурация ProxyHarbor

ProxyHarbor использует стандартную конфигурацию .NET. Ключ `Collector:ProbeHost` можно передать как `Collector__ProbeHost`; переменные среды имеют приоритет над `appsettings.json`. В Docker секреты читаются из файлов и перекрывают обычные значения.

## Быстрый старт Docker Compose

Скопируйте `.env.example` в `.env` и обязательно замените пароли и ключи:

```powershell
Copy-Item .env.example .env
docker compose config --quiet
docker compose up -d --build
```

`.env` исключён из Git. Не храните реальные секреты в документации, shell history, Actions variables или Compose-файлах.

## Переменные Compose

| Переменная | Назначение |
|---|---|
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | База, пользователь и пароль PostgreSQL |
| `ADMIN_USERNAME` | Логин web-администратора; 3–64 символа `A-Z`, `a-z`, `0-9`, `.`, `_`, `-` |
| `ADMIN_EMAIL` | Bootstrap-email администратора; после первого seed профиль хранится в PostgreSQL |
| `ADMIN_PASSWORD` | Bootstrap-пароль первого администратора; в Production 24–256 символов, обязательно upper/lowercase, цифра и специальный знак |
| `ADMIN_API_KEY` | Независимый ключ automation-заголовка `X-Admin-Key`; в Production 24–256 значимых символов |
| `BACKGROUND_WORKERS_ENABLED` | Запуск collector, validator, maintenance и backup workers в API-реплике |
| `VALIDATION_CONCURRENCY` | Параллельные сетевые проверки, `1..1000` |
| `VALIDATION_BATCH_SIZE` | Размер распределённой validation lease, `1..100000` |
| `BACKUP_ENABLED` | Плановый backup; production overlay всё равно принудительно задаёт `true` |
| `BACKUP_HISTORY_RETENTION_DAYS` | Хранение строк аудита backup, `1..3650` |
| `BACKUP_ENCRYPTION_KEY` | Ключ новых PHB3, 32–1024 символа без control characters |
| `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID` | Обязательная пара для включённого backup |
| `BACKEND_SUBNET` | Единственная доверенная container-сеть reverse proxy |
| `PUBLIC_HOST`, `ACME_EMAIL` | Публичное DNS-имя и контакт ACME для production Caddy |
| `PUBLIC_BASE_URL` | Публичный HTTPS origin для email, платёжных webhook и commerce-бота; Compose передаёт его как `TelegramBot__PublicBaseUrl` |
| `*_MEMORY_LIMIT`, `*_CPU_LIMIT` | Ограничения ресурсов контейнеров |
| `PROMETHEUS_*`, `ALERTMANAGER_*` | Loopback-порты и bounded retention monitoring profile |
| `PROXYHARBOR_IMAGE_PREFIX`, `PROXYHARBOR_IMAGE_TAG` | GHCR namespace и версия для release overlay |

## Платежи и подписки

Telegram Stars и commerce-бот настраиваются в `/admin/telegram`; token хранится в БД только как Data Protection ciphertext и не использует `TELEGRAM_BOT_TOKEN`, зарезервированный для доставки backup. Полный runbook: [TELEGRAM_BOT.md](TELEGRAM_BOT.md).

Биллинг по умолчанию выключен (`PAYMENTS_ENABLED=false`). Сначала заключите договор с провайдером,
получите merchant-реквизиты и проверьте тестовый платёж. ProxyHarbor использует hosted checkout:
номер карты и CVC не поступают в API и не сохраняются в PostgreSQL. В БД остаются только заказ,
сумма, валюта, провайдер, внешний идентификатор и статус.

Поддержаны пять шлюзов:

| Провайдер | Переменные | URL уведомления |
|---|---|---|
| ЮKassa | `YOOKASSA_ENABLED`, `YOOKASSA_SHOP_ID`, `YOOKASSA_SECRET_KEY` | `/api/v1/payments/webhooks/yookassa` |
| CloudPayments | `CLOUDPAYMENTS_ENABLED`, `CLOUDPAYMENTS_PUBLIC_ID`, `CLOUDPAYMENTS_API_SECRET` | `/api/v1/payments/webhooks/cloudpayments` (Pay, POST) |
| Robokassa | `ROBOKASSA_ENABLED`, `ROBOKASSA_MERCHANT_LOGIN`, `ROBOKASSA_PASSWORD1`, `ROBOKASSA_PASSWORD2`, `ROBOKASSA_TEST_MODE` | `/api/v1/payments/webhooks/robokassa` (ResultURL; SHA-256) |
| Т-Банк | `TBANK_ENABLED`, `TBANK_TERMINAL_KEY`, `TBANK_PASSWORD` | URL передаётся в `Init` автоматически |
| Stripe | `STRIPE_ENABLED`, `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET` | `/api/v1/payments/webhooks/stripe` (`checkout.session.completed`) |

Переменные и read-only Docker secrets задают безопасный начальный снимок. После первого запуска
администратор может открыть `/admin/payments`: изменить тарифы, идентификаторы, режимы и заменить
секреты без перезапуска контейнера. Runtime-снимок из PostgreSQL имеет приоритет над bootstrap-
переменными. Секретные поля API никогда не возвращает: интерфейс видит только признаки
«настроен / не настроен». В БД секреты защищены ASP.NET Core Data Protection, поэтому для аварийного
восстановления вместе с `.phbackup` сохраните key-ring volume `data-protection` во внешнем secret store.

Включайте приём платежей только после настройки хотя бы одного шлюза. Stripe применим только к
merchant, зарегистрированному в поддерживаемой Stripe стране.

Каталог цен находится в `Payments:Products` (`appsettings.json`). Значения `499 ₽` и `999 ₽` —
предварительные defaults, а не утверждённая публичная оферта; перед включением оплаты задайте итоговые
цены, срок доступа, налоговую ставку и требования онлайн-кассы вместе с бухгалтером. Клиент не может
подменить цену: checkout всегда получает сумму и срок из серверного каталога.

Успешный подписанный webhook переводит заказ в `paid`, продлевает доступ от более поздней даты
(`сейчас` или текущее окончание подписки) и добавляет роль `Subscriber`. Повторная доставка одного
уведомления идемпотентна и не продлевает тариф второй раз. История последних 50 платежей доступна
в личном кабинете. Заказы и зашифрованный runtime-снимок настроек включаются в `.phbackup`;
открытых merchant-секретов в архиве нет.

`POSTGRES_PASSWORD`, `ADMIN_PASSWORD`, `ADMIN_API_KEY`, backup key и Telegram credentials Compose монтирует как bounded secret files. SMTP-пароль подключается отдельным `SecretFiles__SmtpPassword`, когда почтовый relay настроен. Приложение собирает строку PostgreSQL после чтения password file, поэтому пароль не появляется в process environment как часть connection string.

### Восстановление пароля по SMTP

| Переменная | Назначение |
|---|---|
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_USE_SSL` | SMTP relay и TLS-режим |
| `SMTP_USERNAME` | Имя пользователя relay |
| `SMTP_PASSWORD_FILE` | Абсолютный путь внутри container к read-only secret-файлу |
| `SMTP_FROM_ADDRESS`, `SMTP_FROM_NAME` | Проверенный отправитель |
| `PUBLIC_BASE_URL` | HTTPS origin для reset-ссылки, например `https://proxy.example.com` |

Если SMTP не настроен полностью, сервис продолжает работать, а recovery endpoint возвращает `503` без раскрытия наличия аккаунта.

Ключи подписи административной cookie-сессии сохраняются в volume `data-protection` по пути `/app/data-protection`. Благодаря этому активные сессии переживают пересоздание API-контейнера; volume не включается в прикладной backup и должен оставаться доступным только API-процессу.

## Collector

Defaults ниже соответствуют `src/ProxyHarbor.Api/appsettings.json`. Невалидное значение останавливает startup.

| Ключ `Collector:*` | Default | Допустимо | Назначение |
|---|---:|---:|---|
| `BackgroundWorkersEnabled` | `true` | bool | Фоновые циклы этой реплики |
| `CollectionIntervalMinutes` | 5 | 1..10080 | Период полного сбора |
| `ValidationIntervalMinutes` | 2 | 1..1440 | Интервал повторной проверки Alive-прокси |
| `PublicFreshnessMinutes` | 15 | 2..2880 | Максимальный возраст Alive-проверки; не меньше validation interval |
| `DeadRetryBaseMinutes` | 15 | 1..1440 | Начало exponential backoff Dead-прокси |
| `DeadRetryMaxHours` | 24 | 1..720 | Максимальный Dead backoff |
| `ValidationConcurrency` | 800 | 1..1000 | Одновременные пробы |
| `ValidationBatchSize` | 1600 | 1..100000 | Endpoint-ы в одной lease |
| `ProbeTimeoutSeconds` | 8 | 1..120 | Полный timeout пробы |
| `SourceTimeoutSeconds` | 20 | 2..300 | Timeout одной загрузки feed |
| `SourceConcurrency` | 8 | 1..32 | Одновременные feed downloads |
| `SourceRetryCount` | 2 | 0..5 | Повторы transient failure |
| `SourceFailureBackoffBaseMinutes` | 15 | 1..1440 | Начальный source backoff |
| `SourceFailureBackoffMaxHours` | 24 | 1..720 | Максимальный source backoff |
| `MaxProxiesPerSource` | 500000 | 1..1000000 | Защита от слишком большого feed |
| `MaxCandidatesPerRun` | 500000 | 1..5000000 | Лимит объединённого цикла |
| `LastSeenRefreshMinutes` | 360 | 1..10080 | Ограничение write amplification |
| `DeadRetentionDays` | 3 | 1..365 | Хранение старых Pending/Dead, которые ни разу не были Alive; исторически рабочие строки не удаляются |
| `RunRetentionDays` | 30 | 1..3650 | Хранение collection/validation audit |
| `ProbeHost` | `api.ipify.org` | canonical public host/IP | Контрольный TLS endpoint |
| `ProbePort` | 443 | 1..65535 | Порт контрольного endpoint |
| `ProbePath` | `/?format=json` | canonical origin-form | Path/query контрольного endpoint |

Увеличение `ValidationConcurrency` требует проверки лимитов файловых дескрипторов, NAT, CPU, PostgreSQL и внешнего probe endpoint. Рекомендуемый процесс измерения описан в [PERFORMANCE.md](PERFORMANCE.md).

## Backup

| Ключ `Backup:*` | Default | Допустимо |
|---|---:|---:|
| `Enabled` | `false` в appsettings | bool; production требует `true` |
| `IntervalHours` | 24 | 1..8760 |
| `Directory` | `/app/backups` в контейнере | абсолютный путь, не filesystem root |
| `RetentionDays` | 7 | 1..3650 |
| `HistoryRetentionDays` | 365 в Compose | 1..3650 |
| `EncryptionKey` | пусто | при Enabled: 32..1024 Unicode characters, без control/surrogate ошибок |
| `TelegramBotToken` | пусто | 20..256 path-safe printable ASCII |
| `TelegramChatId` | пусто | ненулевой signed 64-bit integer |
| `MaxTelegramFileSizeMb` | 49 | 1..49; максимум 20 частей |

Если backup включён, ключ и оба Telegram-параметра обязательны: приложение fail-closed не стартует с неполной конфигурацией. Полный runbook — [BACKUP_RESTORE.md](BACKUP_RESTORE.md).

## Web, CORS и reverse proxy

- `AllowedHosts` в Production должен содержать 1–32 явных ASCII host pattern; `*` запрещён. Production Compose связывает его с `PUBLIC_HOST`, добавляет `127.0.0.1` для внутреннего frontend healthcheck и Docker DNS-имя `api` для scrape из Prometheus; API при этом не публикуется из backend network.
- `Cors__Origins__N` принимает максимум 32 точных origin без credentials/path/query/fragment; HTTP разрешён только в Development.
- `ForwardedHeaders__KnownNetworks__N` принимает максимум 32 канонических CIDR, IPv4 не шире `/8`, IPv6 не шире `/24`.
- Forwarded headers принимаются только от настроенной сети и только на один hop.

Если перед Caddy добавляется CDN или load balancer, его реальную узкую сеть следует одновременно доверить gateway и API. Не доверяйте всей private-сети и произвольному `X-Forwarded-For`.

## Собственный запуск без Compose

Минимальный development-набор:

```powershell
$env:ConnectionStrings__Postgres='Host=localhost;Port=5432;Database=proxyharbor;Username=proxyharbor;Password=development-only'
$env:Security__AdminApiKey='development-admin-key'
$env:Security__AdminPassword='development-admin-password'
$env:Backup__Enabled='false'
dotnet run --project src/ProxyHarbor.Api
```

Для Production передавайте секреты через secret manager/files, задавайте `ASPNETCORE_ENVIRONMENT=Production`, explicit `AllowedHosts`, trusted networks и HTTPS gateway.

## Приоритет и диагностика

Приоритет типичного container deployment:

1. `appsettings.json`;
2. environment variables;
3. значения из `SecretFiles:*` для поддерживаемых секретов.

Проверить итоговый Compose без запуска:

```powershell
docker compose config --quiet
docker compose -f docker-compose.yml -f docker-compose.production.yml config --quiet
```

Не публикуйте вывод полной конфигурации, если Docker подставил в него значения `.env`.
