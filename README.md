# ProxyHarbor

Высокопроизводительный сервис на ASP.NET Core 10 + React 19 для агрегации, проверки и публикации бесплатных публичных HTTP(S), SOCKS4 и SOCKS5 прокси.

История заметных изменений ведётся в [CHANGELOG.md](CHANGELOG.md); GitHub Release публикует проверенный раздел соответствующей SemVer-версии.

> Публичные прокси контролируются третьими лицами. Не передавайте через них пароли, платёжные данные и иную конфиденциальную информацию. Используйте сервис только законно и с соблюдением правил источников.

## Возможности

- параллельная загрузка 81 встроенного HTTPS feed'а от 50 независимых провайдеров с retry, jitter и ограничениями размера/времени;
- нормализация, защита от приватных адресов/DNS-rebinding и дедупликация с бинарным bulk-upsert PostgreSQL;
- реальная проверка HTTP CONNECT, SOCKS4a и SOCKS5 через TLS до контрольного сервера;
- измерение полной задержки, накопительная статистика успешности и адаптивная перепроверка без перегрузки сети;
- публичный API, фильтрация и экспорт JSON, XML, TXT, CSV;
- адаптивная React-панель и административные действия по ключу;
- ограничение частоты запросов, SSRF-защита источников, контейнеры без root и с read-only ФС;
- потоковые зашифрованные AES-256-GCM снимки БД и настроек с отправкой в Telegram;
- постоянный аудит создания, размера и подтверждённой Telegram-доставки каждого backup;
- opt-in Prometheus + Alertmanager с bounded retention, проверяемыми alert rules и Telegram-уведомлениями;
- CI для backend, frontend, тестов и проверки Docker Compose.

## Быстрый запуск в Docker

Требуются Docker Engine 26+ и Docker Compose 2.24.4+. Все сторонние build/runtime/service images записаны как читаемый `tag@sha256:manifest-digest`: тег сообщает ожидаемую версию, а multi-architecture digest запрещает реестру незаметно подменить фактические байты. CI отклоняет новый mutable image reference. Dependabot обновляет поддерживаемые Dockerfile/Compose-ссылки; service containers внутри GitHub Actions он не обновляет, поэтому один PostgreSQL tag/digest необходимо синхронно менять в `docker-compose.yml`, `ci.yml`, `release.yml` и `source-audit.yml`. Build context формируется по allowlist: `.env`, локальная PostgreSQL, backup, Git-метаданные и dependency/build-каталоги никогда не отправляются Docker daemon и не попадают в build cache. Compose ограничивает размер/ротацию логов и PID, запускает API без Linux capabilities и даёт процессу до двух минут на корректную отмену операций, очистку partial backup и запись итогового аудита при остановке.

```bash
cp .env.example .env
# Заполните все обязательные значения в .env
docker compose up -d --build
```

Интерфейс: `http://localhost:8080`. OpenAPI: `http://localhost:8080/openapi/v1.json`. Liveness: `/health/live`, readiness БД: `/health/ready` (совместимый `/healthz` перенаправляет на readiness), локальные Prometheus-метрики: `/metrics` (production gateway их не публикует).

API автоматически применяет EF Core migrations и синхронизирует встроенный каталог при старте. PostgreSQL advisory lock сериализует этот этап между одновременно запускаемыми репликами: только одна выполняет migrations/seed, остальные ожидают и затем проверяют уже обновлённую схему. Ожидание использует короткий `pg_try_advisory_lock`, а не блокирующий statement snapshot, поэтому не образует deadlock с `CREATE INDEX CONCURRENTLY`. Фоновый collector сохраняет настроенный start-to-start cadence для быстрых циклов, делает обязательный cooldown после цикла дольше интервала, повторяет общий сбой через минуту и ждёт полный интервал, если cluster lock принадлежит другой реплике; медленный или отказавший внешний feed поэтому не создаёт немедленный тяжёлый restart/retry-storm. Критические port/enum/timeline/lease/counter/run/Telegram-инварианты дополнительно закреплены 15 PostgreSQL CHECK constraints: migration сначала публикует их как `NOT VALID`, а затем проверяет исторические строки через менее блокирующий `VALIDATE CONSTRAINT`. Это позволяет безопасный rolling restart без гонки DDL, дублирования источников и длительной остановки обычных writes.

Первый полезный список появляется не мгновенно: сервис сначала загружает кандидатов, затем непрерывно проверяет их пакетами. После bulk-upsert collector отправляет bounded wake-сигнал validator'у: он немедленно прерывает 30-секундное idle-ожидание, а несколько завершений/ручных запусков объединяются в одно событие без роста памяти. Поэтому первая проверка не зависит от случайной фазы polling-цикла. Скорость регулируется `Collector__ValidationConcurrency`; Docker-профиль гарантирует `nofile=8192` для настроенных 800 параллельных probes, но при ручном запуске лимит файловых дескрипторов и пропускную способность сети контролирует оператор. Методика и результаты live-тюнинга приведены в [docs/PERFORMANCE.md](docs/PERFORMANCE.md).

## Версионированные контейнерные релизы

После подключения репозитория к GitHub push строгого SemVer-тега `vX.Y.Z` запускает отдельный release workflow. Сначала tagged commit повторно проходит locked restore, проверку EF model, все backend-тесты на настоящей PostgreSQL, frontend-тесты/audits и Compose/contracts; write-permissions отсутствуют до успеха этого gate. Затем workflow параллельно публикует `proxyharbor-api`, `proxyharbor-web` и `proxyharbor-restore` для `linux/amd64` и `linux/arm64` в GHCR namespace владельца репозитория. Каждый manifest получает OCI labels, встроенную версию, BuildKit SBOM/provenance и точный digest в `proxyharbor-release.json`; для публичного репозитория дополнительно создаётся подписанная GitHub/Sigstore provenance-attestation. Все внешние actions закреплены полными commit SHA и проверяются отдельным supply-chain gate.

GitHub Release прикладывает base, production и release Compose-файлы. Поэтому проверенную версию можно запустить без локальной сборки:

```bash
cp .env.example .env
export PROXYHARBOR_IMAGE_PREFIX=ghcr.io/your-github-owner
export PROXYHARBOR_IMAGE_TAG=1.2.3
docker compose -f docker-compose.yml -f docker-compose.release.yml -f docker-compose.production.yml up -d
```

Для prerelease используется полный нормализованный image tag из release manifest; разделитель `+` SemVer build metadata кодируется как `_build_`, что исключает столкновение с допустимым prerelease-именем. Тег `latest` и плавающий `major.minor` обновляются только стабильными релизами. Compose CI доказывает, что release overlay полностью удаляет локальные `build`-секции. Процедура выпуска и проверка attestations описаны в [docs/RELEASING.md](docs/RELEASING.md).

## Production HTTPS

Создайте DNS A/AAAA-запись на сервер, откройте входящие TCP 80/443 и UDP 443, затем задайте в `.env` bare hostname `PUBLIC_HOST` (без `https://` и пути) и контактный `ACME_EMAIL`:

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d --build
curl https://proxy.example.com/health/ready
```

Production override убирает прямой порт `8080` у frontend и публикует только Caddy на 80/443. Caddy автоматически получает и продлевает сертификат, перенаправляет HTTP на HTTPS, сохраняет ACME-состояние в volumes и работает без root, capabilities и writable root filesystem. Базовый `docker compose up` предназначен для локальной проверки по HTTP, а не для публичного сервера. Подробный checklist: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

Для локально доступной operational history и готовых предупреждений добавьте профиль мониторинга:

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile monitoring up -d --build
```

Prometheus слушает только `127.0.0.1:9090`, Alertmanager — `127.0.0.1:9093`; production gateway намеренно не публикует `/metrics`. Профиль требует `TELEGRAM_BOT_TOKEN` и числовой `TELEGRAM_CHAT_ID`, передаёт их Alertmanager как Compose secrets и отправляет сгруппированные firing/resolved уведомления. Полный список alarms и действия оператора: [docs/MONITORING.md](docs/MONITORING.md).

## Конфигурация

Настройки задаются стандартным способом ASP.NET Core: значения окружения с `__` заменяют секции JSON.

| Переменная | Назначение |
|---|---|
| `ConnectionStrings__Postgres` | Строка подключения PostgreSQL |
| `Security__AdminApiKey` | Значимый корректный Unicode-ключ длиной 24–256 символов для заголовка `X-Admin-Key` |
| `Cors__Origins__0...N` | Явный список доверенных browser origins; в Production по умолчанию пуст |
| `ForwardedHeaders__KnownNetworks__0...N` | CIDR только доверенных reverse proxy; Docker Compose задаёт изолированную `/24` сеть |
| `AllowedHosts` | Разделённые `;` явные Host names без порта; Production запрещает пустое значение и allow-all `*`, Compose использует `PUBLIC_HOST` |
| `PUBLIC_HOST` | Публичное DNS-имя production без схемы и пути |
| `ACME_EMAIL` | Контакт для автоматического выпуска TLS-сертификата |
| `PROMETHEUS_PORT` | Loopback-порт opt-in Prometheus, по умолчанию 9090 |
| `PROMETHEUS_RETENTION_TIME` | Максимальный возраст метрик, по умолчанию 30d |
| `PROMETHEUS_RETENTION_SIZE` | Максимальный объём TSDB, по умолчанию 10GB |
| `ALERTMANAGER_PORT` | Loopback-порт Alertmanager, по умолчанию 9093 |
| `ALERTMANAGER_RETENTION_TIME` | Срок notification log и silences, по умолчанию 120h |
| `SecretFiles__*` | Абсолютные пути secret manager; Docker Compose задаёт их автоматически |
| `Collector__BackgroundWorkersEnabled` | Позволяет отключить workers для миграций, CI или отдельной API-реплики |
| `VALIDATION_CONCURRENCY` / `Collector__ValidationConcurrency` | Параллельность сетевых проверок, по умолчанию 800 |
| `VALIDATION_BATCH_SIZE` / `Collector__ValidationBatchSize` | Размер одной очереди, по умолчанию 1600 |
| `Collector__PublicFreshnessMinutes` | Максимальный возраст проверки для публичной выдачи, по умолчанию 15 минут |
| `Collector__ProbeHost` | Публичный control endpoint, возвращающий JSON `{ "ip": "..." }`: canonical ASCII DNS/IP без схемы и порта; DNS labels содержат только буквы, цифры и внутренний дефис, IP literal уже нормализован; по умолчанию `api.ipify.org` |
| `Collector__ProbePort` | TLS-порт control endpoint, по умолчанию 443 |
| `Collector__ProbePath` | Уже escaped printable ASCII origin-form (`/path?query`) без fragment, пробелов и `//` в начале; по умолчанию `/?format=json` |
| `Collector__DeadRetryBaseMinutes` | Начальная пауза перед повторной проверкой нерабочего прокси, по умолчанию 15 минут |
| `Collector__DeadRetryMaxHours` | Верхняя граница экспоненциальной паузы для нерабочих прокси, по умолчанию 24 часа |
| `Collector__SourceConcurrency` | Параллельность загрузки feed'ов, по умолчанию 8 |
| `Collector__SourceRetryCount` | Повторы временных HTTP/сетевых ошибок, по умолчанию 2 |
| `Collector__SourceFailureBackoffBaseMinutes` | Начальная пауза после ошибки feed, по умолчанию 15 минут |
| `Collector__SourceFailureBackoffMaxHours` | Верхняя граница exponential backoff feed, по умолчанию 24 часа |
| `Collector__MaxProxiesPerSource` | Защитный лимит уникальных адресов из одного feed, по умолчанию 500 000; значение подтверждено полным live-аудитом каталога |
| `Collector__MaxCandidatesPerRun` | Защитный лимит уникальных кандидатов за цикл, по умолчанию 500 000 |
| `Collector__LastSeenRefreshMinutes` | Минимальный интервал записи повторного обнаружения, по умолчанию 360 минут |
| `Collector__RunRetentionDays` | Срок хранения истории циклов, по умолчанию 30 дней |
| `Backup__Enabled` | Включает резервное копирование по расписанию |
| `Backup__HistoryRetentionDays` | Срок хранения аудита backup-запусков, по умолчанию 365 дней |
| `Backup__EncryptionKey` | Ключ создания новых копий: 32–1024 случайных символа без управляющих знаков |
| `Backup__TelegramBotToken` | Token от BotFather: 20–256 printable ASCII символов без URI-разделителей `/`, `\\`, `?`, `#`, `%` |
| `Backup__TelegramChatId` | Ненулевой signed 64-bit числовой ID администратора/group/channel, совместимый с Alertmanager |
| `Backup__MaxTelegramFileSizeMb` | Лимит отправляемого файла, по умолчанию безопасные 49 МБ |

Числовой `chat_id` выбран намеренно: Bot API допускает также `@username`, но общий secret одновременно используется [Telegram receiver Alertmanager](https://prometheus.io/docs/alerting/latest/configuration/#telegram_config), где поле имеет тип integer; отправка документа соответствует официальному [`sendDocument`](https://core.telegram.org/bots/api#senddocument).

Секреты не коммитьте. Docker Compose преобразует значения `.env` для PostgreSQL password, admin key, backup encryption key и Telegram в отдельные read-only `/run/secrets/*`; значений нет в container environment и `docker inspect`. Bounded UTF-8 loader ограничивает каждый файл 16 КиБ, удаляет только один терминальный CRLF/LF и fail-closed отклоняет relative path, invalid UTF-8 и внутренние control characters. При обычном `dotnet run` стандартные environment-настройки остаются совместимыми. В Production административный ключ должен содержать 24–256 символов без управляющих знаков. Middleware принимает ровно одно bounded значение `X-Admin-Key`, сравнивает SHA-256 в constant time и помечает все admin-ответы `no-store`; React хранит ключ только в памяти/sessionStorage текущей вкладки, продолжает работать при заблокированном Storage API и предоставляет явный выход. CORS-доступ включается только для явно перечисленных origins, а `X-Forwarded-*` принимается только от настроенных proxy-сетей с одним разрешённым переходом. API и nginx выставляют CSP, HSTS, frame/content-type/referrer/permissions и cross-origin isolation headers. При `Backup__Enabled=true` startup требует одновременно сильный ключ и валидные Telegram token/chat ID: плановый backup не может молча перейти в локальный-only режим и не доставить архив администратору. Секреты не включаются и в backup: архив содержит данные БД, каталог источников, безопасные параметры сборщика/backup/runtime, CORS origins, доверенные proxy CIDR и manifest. Для admin key и строки БД сохраняются только флаги наличия, но не значения. Новый manifest v5 требует точную типизированную схему всех трёх settings-файлов; каждое свойство `BackupOptions` fail-closed классифицировано как безопасное или секретное, поэтому новый параметр нельзя молча потерять либо отправить в архив. BackupWorker при старте и после каждого cooldown читает время последнего `completed`-аудита: restart/deploy или ручной backup сохраняет оставшуюся часть настроенного интервала и не отправляет лишний архив в Telegram; при отсутствии либо просрочке успешной копии backup запускается сразу, а недоступность БД повторно проверяется через bounded 15 минут без остановки host. Разрешённые интервалы длиннее суток выполняются переносимыми суточными timer chunks с повторным чтением аудита, поэтому ручная копия и clock shift не остаются незамеченными на месяцы. ZIP создаётся непосредственно в bounded pipe (пауза producer при 4 МиБ, возобновление при 2 МиБ) и одновременно шифруется в `.phbackup`, поэтому plaintext-архив не записывается в backup volume. После финального PHB3-маркера ciphertext один раз синхронизируется с диском, полностью перечитывается и аутентифицируется без записи plaintext; только после успеха durable partial атомарно публикуется, участвует в retention и отправляется в Telegram. Формат PHB3 потоково шифрует и аутентифицирует содержимое, порядок блоков и завершающий маркер; расшифровщик сохраняет совместимость с PHB2.

Production требует 1–32 явных ASCII pattern в `AllowedHosts`; пустое значение и framework allow-all `*` останавливают startup. Compose передаёт API тот же `PUBLIC_HOST`, который завершает TLS в Caddy, поэтому неизвестный Host отклоняется Kestrel с HTTP 400 до маршрутизации. Это соответствует официальной модели [ASP.NET Core Host Filtering](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0).

## API

```text
GET  /api/v1/proxies?protocol=Socks5&maxLatencyMs=1000&page=1&pageSize=100
GET  /api/v1/proxies/seek?protocol=Socks5&pageSize=100&after={nextCursor}
GET  /api/v1/export/json?maxLatencyMs=1500&minSuccessRate=80&limit=50000&offset=0
GET  /api/v1/export/json/seek?minSuccessRate=80&limit=50000&after={nextCursor}
GET  /api/v1/export/xml?protocol=Socks5
GET  /api/v1/export/txt?protocol=Http&maxLatencyMs=1000
GET  /api/v1/export/csv?minSuccessRate=90
GET  /api/v1/sources
GET  /api/v1/stats
```

`/api/v1/sources` без административного ключа возвращает публичный read-only снимок: дату последнего release-аудита, 50 независимых провайдеров, их протоколы и все 81 канонический feed. Текущее состояние, ошибки и расписание источников остаются только в admin diagnostics и Prometheus.

Экспорт потоково отдаёт до 50 000 свежих проверенных записей за запрос, не собирая многомегабайтный ответ в памяти API, и поддерживает те же `protocol`, `maxLatencyMs` и `minSuccessRate`, что публичный список. Для последовательного обхода большого каталога используйте `/proxies/seek` и `/export/{format}/seek`: они не выполняют полный `COUNT` и не сканируют растущий `OFFSET`. Передайте возвращённый `nextCursor` как `after`; у экспорта продолжение находится в `X-Next-Cursor`, а текущая позиция — в `X-Export-Cursor`. Непрозрачный 44-символьный cursor привязан к фильтрам, поэтому повреждённое значение или его повторное использование с другими фильтрами даёт `400`. Старые `page`/`offset` маршруты сохранены для совместимости, возвращают `X-Next-Offset` и fail-fast отклоняют смещение свыше 5 000 000; для более глубокого обхода обязателен seek.

Список и все экспорты используют единый полный порядок latency → число успешных проверок по убыванию → UUID, поэтому записи с одинаковой скоростью имеют однозначный порядок. Внутри одного export-ответа boundary-запрос continuation-заголовков и потоковое тело читаются из единого PostgreSQL `REPEATABLE READ` snapshot, поэтому concurrent validation не может выдать cursor от одной страницы и body от другой. Между отдельными запросами фоновые проверки всё ещё могут обновить порядок или freshness; cursor намеренно не является долгоживущим снимком БД. Все continuation-заголовки доступны browser-клиентам через CORS. JSON, XML и CSV содержат стабильные поля protocol/host/port/url/latency/success/exit IP/time; TXT содержит по одному готовому proxy URL на строку. CSV кавычит строки и нейтрализует spreadsheet formula injection. Все четыре streaming-формата прокидывают отключение клиента до чтения PostgreSQL и записи HTTP body; занятый export-слот и read-snapshot освобождаются через `finally`/async disposal. На один IP разрешено 120 обычных публичных запросов, включая DB readiness, 20 административных и 5 тяжёлых экспортов в минуту; дешёвый `/health/live` не ограничивается, чтобы оставаться независимым liveness-сигналом. Один экземпляр API одновременно формирует не более двух экспортов. При превышении лимита сервис отвечает `429` или `503` и `Retry-After`. Короткий server-side cache сводки и legacy-страниц игнорирует неизвестные query-параметры, чтобы ими нельзя было раздувать число cache keys; у cursor-выдачи кэшируется только общая первая страница, а уникальные продолжения обходят cache.

Административные запросы требуют `X-Admin-Key`:

```text
GET    /api/v1/admin/sources
GET    /api/v1/admin/sources/{id}
GET    /api/v1/admin/diagnostics
POST   /api/v1/admin/sources
PUT    /api/v1/admin/sources/{id}
DELETE /api/v1/admin/sources/{id}
POST   /api/v1/admin/collect
POST   /api/v1/admin/validate
POST   /api/v1/admin/backup
```

`diagnostics` возвращает очередь проверки, точную скорость и ETA её обработки, состояние встроенного каталога, последние циклы сбора и последние backup-запуски, включая итоговый статус, размер файла и подтверждённый факт Telegram-доставки. Для каждого источника сохраняется `lastResultTruncated`, а цикл содержит `sourcesTruncated` и `candidateLimitReached`, поэтому защитные лимиты не маскируются обычным успешным статусом. Prometheus отдельно публикует completeness и фактическое здоровье каталога через `proxyharbor_source_catalog_complete`, `proxyharbor_source_catalog_healthy`, счётчики встроенных feed'ов/провайдеров, `proxyharbor_builtin_sources_stale`, `proxyharbor_builtin_sources_truncated` и `proxyharbor_last_collection_candidate_limit_reached`, а также backup-сигналы `proxyharbor_last_backup_success`, `proxyharbor_last_backup_sent_to_telegram`, `proxyharbor_last_backup_timestamp_seconds` и `proxyharbor_backup_runs_active`. Успешный feed считается здоровым, только если его свежий результат не был усечён; остановка collector и неполная выборка больше не маскируются исторически зелёным статусом.

Повторный запуск `collect` или `backup`, пока операция уже выполняется этой или другой репликой, немедленно получает HTTP `409`. Для `validate` тот же ответ действует внутри одной реплики; разные реплики безопасно арендуют непересекающиеся пакеты PostgreSQL. Validation-lease ограничен 2–5 минутами и продлевается heartbeat независимо от размера пакета: единичный transient-сбой продления логируется и повторяется на следующем периоде, после штатной отмены lease освобождается немедленно, а после аварии процесса очередь автоматически возвращается в работу за bounded-время. Продление, cleanup и запись результата используют точный UUID владельца и не могут затронуть новую аренду другой реплики. Долгие административные запросы поэтому не накапливаются в локальной очереди.

После получения распределённой collection-блокировки новый цикл переводит оставшиеся после kill/power loss строки `Runs.status=running` в `failed` с временем и диагностикой аварийного завершения. Успешная финализация атомарно переводит только принадлежащую циклу строку `running → completed`; потеря ownership даёт fail-closed ошибку и не перезаписывает параллельный administrative/restore результат. Ошибка или отмена текущего цикла записывается отдельным DbContext с 15-секундным пределом и только переходом `running → failed`: вторичный сбой аудита не скрывает исходную причину, а следующая попытка гарантированно восстановит незавершённую строку.

Перед арендой validation-пакета сервис напрямую проверяет control endpoint и кэширует результат на короткий срок. Direct-клиент запрещает redirect и private/service DNS, повторно проверяет адрес перед TCP connect и ограничивает распакованный JSON 16 КБ. Если endpoint недоступен, очередь не арендуется. Внутри proxy-туннеля HTTP-reader ограничен 64 КБ и завершает замер сразу после полного `Content-Length` или chunked body, не добавляя ожидание keep-alive EOF к latency. Если уже установленный TLS-туннель получил от control endpoint ошибочный HTTP/JSON-ответ, результат помечается `deferred`: lease освобождается, повтор назначается через минуту, но Status, latency, счётчики успехов/ошибок и failure streak прокси не изменяются. При этом `LastValidationAttemptAt` и `LastValidationDeferred` сохраняют сам факт попытки, а `ValidationRuns` аудирует каждую непустую партию, её lease, claimed/checked/alive/deferred, длительность и ошибку. Если часть результатов потеряла точный lease token, ещё принадлежащие результаты сохраняются, чужие строки не изменяются, а партия fail-closed получает статус `failed` вместо ложного `completed`. Crash recovery переводит старый running-аудит в failed только после исчезновения связанной активной lease другой реплики. Эти данные входят в backup/restore и очищаются общей retention-политикой завершённых run'ов. IPv4/IPv6 exit IP канонизируются перед сравнением анонимности. `POST /api/v1/admin/validate` возвращает числа `checked`, `alive` и `deferred`; diagnostics и React показывают точные attempts/alive/deferred за последние пять минут, проверки в секунду и прогноз опустошения due-очереди. Prometheus публикует те же rolling-сигналы, `proxyharbor_validation_checks_per_second`, `proxyharbor_validation_estimated_drain_seconds`, failed/active batches и здоровье control endpoint. Еженедельный workflow после полного аудита всех feed'ов запускает реальную validation-партию, требует хотя бы один Alive proxy и точное совпадение упорядоченных URL в JSON, XML, TXT и CSV. SHA-256 опубликованного набора вместе с машиночитаемыми отчётами обоих этапов сохраняется в CI summary/artifacts.

Background collector применяет bounded exponential backoff только к feed’ам с последовательными ошибками; `NextFetchAt` виден в admin API и панели. Ручной `POST /api/v1/admin/collect` всегда принудительно проверяет все включённые источники, поэтому используется для полного аудита 81 endpoint. Audit-gate требует для каждого enabled feed временное доказательство именно этого запуска — `StartedAt ≤ LastFetchedAt ≤ FinishedAt`; stale и future evidence раздельно попадают в JSON artifact и одинаково fail-closed отклоняются. Пользовательский feed требует непустое имя, известный protocol и публичный HTTPS URL без fragment; единый non-throwing parser ограничивает 2048 символами исходный и нормализованный URL и отклоняет malformed input с HTTP 400 до DNS и обращения к БД. Path/query сравниваются с учётом регистра, поскольку `/Feed` и `/feed` могут быть разными ресурсами. Из встроенного каталога разрешено менять только `Enabled`, все канонические метаданные неизменяемы. HTTP 404 и другие постоянные 4xx не повторяются, а ответ 2xx без единого распознаваемого прокси считается сбоем, а не ложным успехом. После успешного ответа сохраняются bounded `ETag`/`Last-Modified`; следующий цикл отправляет `If-None-Match`/`If-Modified-Since`, а подтверждённый `304` обновляет freshness и аудит без повторного скачивания и парсинга неизменившегося feed'а. Не реже раза в сутки, а при однодневном dead retention — раз в 12 часов, validators намеренно не отправляются: полный body обновляет membership и возвращает кандидатов, которые могли быть удалены retention при неизменном ETag. Время такого ответа хранится отдельно как `LastContentFetchedAt` и видно в admin API/панели. Unsolicited `304`, malformed ETag и `304` без прежнего непустого результата считаются сбоем.

Каждый ответ источника ограничен 10 МБ, а parser использует линейный non-backtracking regex и хранит не более `MaxProxiesPerSource` уникальных публичных IP. После заполнения лимита он ищет только первый следующий новый адрес: его наличие выставляет точный `lastResultTruncated`, после чего разбор немедленно прекращается; хвост из одних дубликатов не создаёт ложную тревогу. Общий конкурентный bounded-набор независимо удерживает не более `MaxCandidatesPerRun` уникальных endpoint'ов и выставляет `candidateLimitReached` только после фактического отбрасывания следующего нового адреса: точное заполнение лимита и последующие дубликаты не создают ложную тревогу. Горячий collector-path передаёт результаты прямо в общий набор и хранит IP/port/protocol как компактный value-key без managed references; отдельные строки и materialized список до 500 000 элементов для каждого из восьми параллельных feed'ов не создаются. Каноническая IP-строка появляется только при последовательной PostgreSQL COPY. Доменные и служебные/private адреса отбрасываются до сохранения. Это удерживает память и CPU в предсказуемых пределах, а полноту результата делает наблюдаемой даже для недоверенного или аномально большого feed'а.

Источниками можно управлять и из React-панели: добавлять собственные HTTPS feed'ы, менять их активность и удалять пользовательские записи. Встроенные источники помечены провайдером и рангом; их канонические URL и протоколы неизменяемы, а удаление безопасно переводит запись на паузу. Административная панель также показывает размер PostgreSQL, очередь проверки, точную историю validation-партий, последние циклы сбора и историю backup с подтверждённым состоянием Telegram-доставки; данные автоматически обновляются каждые 15 секунд и после административных действий.

## Локальная разработка

Запустите PostgreSQL и задайте строку подключения, затем:

```powershell
dotnet run --project src/ProxyHarbor.Api
cd src/proxyharbor-web
npm ci
npm run dev
```

Vite проксирует API на `http://localhost:8080`. Для проверки репозитория:

```powershell
dotnet restore ProxyHarbor.slnx --locked-mode
dotnet build ProxyHarbor.slnx -c Release --no-restore
dotnet test ProxyHarbor.slnx -c Release --no-build
cd src/proxyharbor-web
npm ci
npm run lint
npm test
npm run build
```

Репозиторий ограничивает разработку совместимыми feature band .NET 10 через `global.json`, а release CI и Docker закреплены на security SDK `10.0.302` и runtime `10.0.10`. NuGet restore разрешён только с официального `nuget.org`, полный transitive graph хранится в `packages.lock.json` каждого проекта. При намеренном обновлении пакетов выполните `dotnet restore ProxyHarbor.slnx --force-evaluate`, проверьте изменения lock-файлов и повторите vulnerability-аудит; обычные CI/Docker-сборки используют `--locked-mode`.

CI собирает Cobertura coverage и через `tools/Assert-Coverage.ps1` запрещает опускаться ниже 55% уникальных строк и ветвей рукописного кода. Suite выполняется как без внешних зависимостей, так и повторно с настоящей PostgreSQL, поэтому в отдельный gate входят SQL bulk-upsert, lease, backup/restore и транзакционные ветви. Generated `obj` и EF migrations в знаменатель не входят; порог является только regression-floor и должен повышаться вместе с тестами критических orchestration-ветвей.

## Резервные копии и восстановление

Backup хранится в volume `backups` и удаляется по истечении `Backup__RetentionDays`. Локальная retention-политика применяется сразу после атомарной публикации нового архива, ограничивает набор и возрастом, и ожидаемым числом плановых снимков с двумя recovery-слотами и не зависит от доступности Telegram. Поэтому продолжительный внешний сбой не вызывает неограниченный рост volume даже при 15-минутных повторах. История попыток, размер созданного файла и факт успешной Telegram-доставки сохраняются в `BackupRuns`, доступны через admin diagnostics и Prometheus-метрики и очищаются по `Backup__HistoryRetentionDays`. Чтобы расшифровать файл на доверенной машине:

Backup считается завершённым только после атомарного перевода принадлежащей ему audit-строки из `running` в `completed`. Если строку удалили или изменили параллельно, операция завершается fail-closed ошибкой даже при уже опубликованном архиве и успешном ответе Telegram; локальный зашифрованный файл остаётся пригодным для восстановления, а scheduler повторит цикл.

После успешного запуска scheduler ждёт настроенный `Backup__IntervalHours`. После ошибки БД, шифрования или Telegram он автоматически повторяет полный backup через 15 минут; cluster-lock конфликт с другой репликой считается уже обслуживаемым циклом и не создаёт retry-storm.

```powershell
$keyFile = (Resolve-Path ./backup-key.secret).Path
./tools/Decrypt-Backup.ps1 -InputFile ./proxyharbor.phbackup -OutputZip ./proxyharbor.zip -EncryptionKeyFile $keyFile
```

Результат — обычный ZIP с JSON по таблицам и параметрами сборщика. PowerShell-инструмент сначала пишет его в уникальный sibling `.partial`, синхронизирует содержимое и только после полной AEAD-проверки атомарно публикует через move без overwrite: существующий операторский файл не изменяется даже при гонке, а partial удаляется при обрабатываемой ошибке. Проверяйте восстановление на отдельной БД перед аварийной ситуацией. Потеря ключа делает корректно зашифрованный архив невосстановимым.
Файл ключа должен быть доступен только оператору восстановления; передавайте абсолютный путь через `-EncryptionKeyFile` и удаляйте временную копию безопасным способом после завершения. Inline-параметр оставлен только для обратной совместимости и может раскрыть ключ в истории команд или списке процессов.

Для восстановления ранее созданных PHB2/PHB3 сохранена совместимость с корректными legacy-ключами длиной от 16 символов; новые backup всегда требуют минимум 32. Ключ обязан иметь корректную Unicode-кодировку без unpaired surrogate: сервер, restore CLI и PowerShell-инструмент строго и однозначно кодируют его в UTF-8 перед PBKDF2. Корневой, относительный, пустой или содержащий управляющие символы `Backup__Directory` отклоняется, чтобы retention не работал вне явно выделенного каталога.

Для полного восстановления PostgreSQL задайте секреты через окружение и явно подтвердите замену данных:

```powershell
$env:ConnectionStrings__Postgres='Host=localhost;Database=proxyharbor_restore;Username=proxyharbor;Password=...'
$env:Backup__EncryptionKey='ваш ключ'
dotnet run -c Release --project src/ProxyHarbor.Restore -- --input ./proxyharbor.phbackup --replace-existing-data
```

Вместо environment можно передать абсолютный путь к bounded UTF-8 secret-файлу через `--encryption-key-file`; inline `--encryption-key` оставлен для совместимости, но не рекомендуется, поскольку значение видно в process arguments. Ctrl+C и container `SIGTERM` отменяют расшифровку/COPY, откатывают транзакцию, удаляют временный plaintext ZIP и завершаются кодом 130.

На опубликованном Docker Compose-хосте локальный .NET SDK не требуется. Restore вынесен в opt-in profile `tools`, запускается без root с read-only root filesystem и по умолчанию только показывает справку. Сначала остановите процессы, которые пишут в БД, затем запустите одноразовый контейнер с точным именем backup из volume и после успеха верните сервисы:

```powershell
docker compose stop web api
docker compose --profile tools run --rm restore --input /app/backups/proxyharbor-YYYYMMDD-HHMMSS.phbackup --replace-existing-data
docker compose up -d api web
```

Команда `run --rm` удаляет контейнер и его анонимный `/restore-temp`; постоянный volume `backups` подключён только для чтения. Расшифрованный ZIP никогда не записывается в `backups`. Перед заменой production-данных всё равно выполните пробное восстановление в отдельную БД, переопределив `ConnectionStrings__Postgres` для restore-контейнера.

Restore сначала проверяет аутентификацию backup, manifest, точный allowlist ZIP-записей, отсутствие секретов, дубликатов, oversized settings/database-файлов, общий распакованный размер 32 ГиБ и безопасную степень ZIP-сжатия, под общей startup-блокировкой применяет миграции, затем заменяет таблицы прокси, источников, циклов сбора, validation-аудита и backup-аудита в одной транзакции. Перед каждой streaming COPY row отдельно проверяются публичность и каноничность IP, port/enum/URL, длины, неотрицательные счётчики, согласованность run totals/status/finishedAt, lease и Telegram delivery; семантически повреждённый snapshot откатывает всю транзакцию с понятной ошибкой. Manifest v5 требует `validation-runs.json`, полный snapshot collector/backup/runtime-настроек, точный набор полей и обязательные отрицательные secret-флаги; подмена settings на пустой или частичный JSON отклоняется до изменения БД. Restore продолжает принимать архивы v2–v4. Потоковый PostgreSQL binary COPY ускоряет импорт больших снимков; при любой ошибке исходные данные целевой БД сохраняются. Готовый `.phbackup` публикуется в каталоге атомарно; plaintext ZIP в актуальной версии не создаётся, а новый запуск под cluster lock всё ещё удаляет legacy ZIP, `.partial` и временные Telegram-части после аварийного завершения. PostgreSQL integration-gate создаёт настоящий зашифрованный снимок, удаляет исходный marker и сравнивает после restore все сохраняемые поля пяти таблиц на отдельных схемах; отдельный повреждённый снимок доказывает rollback без потери прежней target-БД.

Успешная Telegram-доставка записывается в аудит только после HTTP 2xx и обязательного Bot API подтверждения `ok=true` для файла либо каждой нумерованной части. Ответы 429/5xx и временные сетевые ошибки повторяются до трёх раз; `retry_after` принимается как из HTTP-заголовка, так и из JSON `parameters`. Bot API body читается непосредственно после headers с жёстким пределом 64 КиБ и никогда предварительно не буферизуется `HttpClient`; некорректный/слишком большой ответ не считается успехом, bot token исключён из HTTP-логирования, а redirect запрещён.

Если архив превышает Telegram-лимит, сервис отправляет нумерованные части. Сначала объедините их, затем расшифруйте:

```powershell
./tools/Join-BackupParts.ps1 -PartsPattern './proxyharbor.phbackup.part*' -OutputFile './proxyharbor.phbackup'
$keyFile = (Resolve-Path './backup-key.secret').Path
./tools/Decrypt-Backup.ps1 -InputFile './proxyharbor.phbackup' -OutputZip './proxyharbor.zip' -EncryptionKeyFile $keyFile
```

Join-инструмент fail-closed принимает только один полный набор с общим base-name и `of-N`, требует непрерывные номера `1..N` и ожидаемые размеры частей. Слишком широкий wildcard, смешавший разные backup, пропущенный/пустой фрагмент или уже существующий output отклоняются до восстановления; целостность собранного ciphertext затем независимо подтверждается PHB3 AEAD при расшифровании.

## Добавление источника

Добавляйте только список, владелец которого разрешает автоматическую загрузку. Источник должен быть публичным HTTPS URL и отдавать строки `host:port` или `scheme://host:port`. Для строк без схемы применяется выбранный протокол. Приватные и loopback IP отбрасываются.

В каталогах бесплатных прокси категория `HTTPS` означает обычный HTTP proxy с поддержкой метода CONNECT к TLS-назначению. Поэтому поле `protocol` остаётся `Https`, но готовый `url` и TXT-экспорт используют корректный транспорт `http://host:port`; SOCKS4 и SOCKS5 сохраняют собственные URI-схемы.

Удобная таблица [всех 50 провайдеров](docs/SOURCE_CATALOG.md), полный встроенный каталог и [команда живого аудита](docs/SOURCES.md) находятся в документации проекта.

## Архитектура

- `ProxyHarbor.Domain` — сущности и контракты без инфраструктурных зависимостей;
- `ProxyHarbor.Infrastructure` — PostgreSQL, источники, парсер, протокольные probes, распределённые workers и backup;
- `ProxyHarbor.Api` — публичные/административные HTTP-контракты и защитный middleware;
- `proxyharbor-web` — React 19 + TypeScript + Vite;
- `ProxyHarbor.Tests` — быстрые автоматические проверки критичной нормализации;
- GitHub Actions дополнительно поднимает настоящий PostgreSQL, применяет миграции и проверяет HTTP-контракты.

Валидаторы можно масштабировать горизонтально: строки очереди резервируются через `FOR UPDATE SKIP LOCKED`, а результаты одного пакета записываются через PostgreSQL binary COPY и один bulk-update. Новые адреса проверяются сразу, живые — через стандартный интервал, а повторно нерабочие — с экспоненциальной задержкой до настроенного максимума.

## Перед публикацией

1. Проверьте лицензии и условия каждого включённого источника.
2. Ограничьте внешний доступ к административным маршрутам на reverse proxy/firewall.
3. Запустите production override, проверьте TLS-сертификат, внешнюю наблюдаемость и оповещения.
4. Выполните тестовый backup, Telegram-доставку и расшифровку.

Лицензия: MIT. Встроенные списки принадлежат их авторам и загружаются во время работы; данные прокси в репозиторий не включены.
