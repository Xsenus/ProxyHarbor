# ProxyHarbor

Высокопроизводительный сервис на ASP.NET Core 10 + React 19 для агрегации, проверки и публикации бесплатных публичных HTTP(S), SOCKS4 и SOCKS5 прокси.

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
- CI для backend, frontend, тестов и проверки Docker Compose.

## Быстрый запуск в Docker

Требуются Docker Engine 26+ и Docker Compose v2. Build context формируется по allowlist: `.env`, локальная PostgreSQL, backup, Git-метаданные и dependency/build-каталоги никогда не отправляются Docker daemon и не попадают в build cache. Compose ограничивает размер/ротацию логов и PID, запускает API без Linux capabilities и даёт процессу до двух минут на корректную отмену операций, очистку partial backup и запись итогового аудита при остановке.

```bash
cp .env.example .env
# Заполните все обязательные значения в .env
docker compose up -d --build
```

Интерфейс: `http://localhost:8080`. OpenAPI: `http://localhost:8080/openapi/v1.json`. Liveness: `/health/live`, readiness БД: `/health/ready` (совместимый `/healthz` перенаправляет на readiness), Prometheus-метрики: `/metrics`.

API автоматически применяет EF Core migrations и синхронизирует встроенный каталог при старте. PostgreSQL advisory lock сериализует этот этап между одновременно запускаемыми репликами: только одна выполняет migrations/seed, остальные ожидают и затем проверяют уже обновлённую схему. Это позволяет безопасный rolling restart без гонки DDL и дублирования источников.

Первый полезный список появляется не мгновенно: сервис сначала загружает кандидатов, затем непрерывно проверяет их пакетами. Скорость регулируется `Collector__ValidationConcurrency`; Docker-профиль гарантирует `nofile=8192` для настроенных 800 параллельных probes, но при ручном запуске лимит файловых дескрипторов и пропускную способность сети контролирует оператор. Методика и результаты live-тюнинга приведены в [docs/PERFORMANCE.md](docs/PERFORMANCE.md).

## Конфигурация

Настройки задаются стандартным способом ASP.NET Core: значения окружения с `__` заменяют секции JSON.

| Переменная | Назначение |
|---|---|
| `ConnectionStrings__Postgres` | Строка подключения PostgreSQL |
| `Security__AdminApiKey` | Ключ заголовка `X-Admin-Key` для `/api/v1/admin/*` |
| `Cors__Origins__0...N` | Явный список доверенных browser origins; в Production по умолчанию пуст |
| `ForwardedHeaders__KnownNetworks__0...N` | CIDR только доверенных reverse proxy; Docker Compose задаёт изолированную `/24` сеть |
| `Collector__BackgroundWorkersEnabled` | Позволяет отключить workers для миграций, CI или отдельной API-реплики |
| `VALIDATION_CONCURRENCY` / `Collector__ValidationConcurrency` | Параллельность сетевых проверок, по умолчанию 800 |
| `VALIDATION_BATCH_SIZE` / `Collector__ValidationBatchSize` | Размер одной очереди, по умолчанию 1600 |
| `Collector__PublicFreshnessMinutes` | Максимальный возраст проверки для публичной выдачи, по умолчанию 15 минут |
| `Collector__ProbeHost` | DNS-имя доверенного HTTPS endpoint, возвращающего JSON `{ "ip": "..." }` |
| `Collector__ProbePort` | TCP-порт контрольного endpoint, по умолчанию 443 |
| `Collector__ProbePath` | HTTP path и query контрольного endpoint, по умолчанию `/?format=json` |
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
| `Backup__EncryptionKey` | Пароль шифрования, минимум 16, рекомендуется 32+ случайных символа |
| `Backup__TelegramBotToken` | Токен Telegram-бота |
| `Backup__TelegramChatId` | ID администратора/чата, куда отправлять архив |
| `Backup__MaxTelegramFileSizeMb` | Лимит отправляемого файла, по умолчанию безопасные 49 МБ |

Секреты не коммитьте. В Production административный ключ должен содержать 24–256 символов без управляющих знаков. Middleware принимает ровно одно bounded значение `X-Admin-Key`, сравнивает SHA-256 в constant time и помечает все admin-ответы `no-store`; React хранит ключ только в памяти/sessionStorage текущей вкладки, продолжает работать при заблокированном Storage API и предоставляет явный выход. CORS-доступ включается только для явно перечисленных origins, а `X-Forwarded-*` принимается только от настроенных proxy-сетей с одним разрешённым переходом. API и nginx выставляют CSP, HSTS, frame/content-type/referrer/permissions и cross-origin isolation headers. Секреты не включаются и в backup: архив содержит данные БД, каталог источников, безопасные параметры сборщика/backup/runtime, CORS origins, доверенные proxy CIDR и manifest. Для admin key и строки БД сохраняются только флаги наличия, но не значения. ZIP создаётся непосредственно в bounded pipe (пауза producer при 4 МиБ, возобновление при 2 МиБ) и одновременно шифруется в `.phbackup`, поэтому plaintext-архив не записывается в backup volume. Telegram получает только зашифрованный файл. Формат PHB3 потоково шифрует и аутентифицирует содержимое, порядок блоков и завершающий маркер; расшифровщик сохраняет совместимость с PHB2.

## API

```text
GET  /api/v1/proxies?protocol=Socks5&maxLatencyMs=1000&page=1&pageSize=100
GET  /api/v1/export/json?maxLatencyMs=1500&minSuccessRate=80&limit=50000&offset=0
GET  /api/v1/export/xml?protocol=Socks5
GET  /api/v1/export/txt?protocol=Http&maxLatencyMs=1000
GET  /api/v1/export/csv?minSuccessRate=90
GET  /api/v1/stats
```

Экспорт потоково отдаёт до 50 000 свежих проверенных записей за запрос, не собирая многомегабайтный ответ в памяти API, и поддерживает те же `protocol`, `maxLatencyMs` и `minSuccessRate`, что публичный список. Размер страницы задаётся `limit=1..50000`, продолжение — неотрицательным `offset`. Заголовки `X-Export-Limit`, `X-Export-Offset` и `X-Export-Truncated` описывают страницу; при наличии продолжения `X-Next-Offset` содержит значение следующего запроса. Эти заголовки доступны browser-клиентам через CORS. JSON, XML и CSV содержат стабильные поля protocol/host/port/url/latency/success/exit IP/time; TXT содержит по одному готовому proxy URL на строку. CSV кавычит строки и нейтрализует spreadsheet formula injection. На один IP разрешено 120 обычных публичных запросов, 20 административных и 5 тяжёлых экспортов в минуту; один экземпляр API одновременно формирует не более двух экспортов. При превышении лимита сервис отвечает `429` или `503` и `Retry-After`. Короткий server-side cache сводки и страниц игнорирует неизвестные query-параметры, чтобы ими нельзя было раздувать число cache keys.

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

Повторный запуск `collect` или `backup`, пока операция уже выполняется этой или другой репликой, немедленно получает HTTP `409`. Для `validate` тот же ответ действует внутри одной реплики; разные реплики безопасно арендуют непересекающиеся пакеты PostgreSQL. Validation-lease ограничен 2–5 минутами и продлевается heartbeat независимо от размера пакета: после штатной отмены он освобождается немедленно, а после аварии процесса очередь автоматически возвращается в работу за bounded-время. Продление, cleanup и запись результата используют точный UUID владельца и не могут затронуть новую аренду другой реплики. Долгие административные запросы поэтому не накапливаются в локальной очереди.

После получения распределённой collection-блокировки новый цикл переводит оставшиеся после kill/power loss строки `Runs.status=running` в `failed` с временем и диагностикой аварийного завершения. Ошибка или отмена текущего цикла записывается отдельным DbContext с 15-секундным пределом: вторичный сбой аудита не скрывает исходную причину, а следующая попытка гарантированно восстановит незавершённую строку.

Перед арендой validation-пакета сервис напрямую проверяет control endpoint и кэширует результат на короткий срок. Direct-клиент запрещает redirect и private/service DNS, повторно проверяет адрес перед TCP connect и ограничивает распакованный JSON 16 КБ. Если endpoint недоступен, очередь не арендуется. Внутри proxy-туннеля HTTP-reader ограничен 64 КБ и завершает замер сразу после полного `Content-Length` или chunked body, не добавляя ожидание keep-alive EOF к latency. Если уже установленный TLS-туннель получил от control endpoint ошибочный HTTP/JSON-ответ, результат помечается `deferred`: lease освобождается, повтор назначается через минуту, но Status, latency, счётчики успехов/ошибок и failure streak прокси не изменяются. При этом `LastValidationAttemptAt` и `LastValidationDeferred` сохраняют сам факт попытки, а `ValidationRuns` аудирует каждую непустую партию, её lease, claimed/checked/alive/deferred, длительность и ошибку. Crash recovery переводит старый running-аудит в failed только после исчезновения связанной активной lease другой реплики. Эти данные входят в backup/restore и очищаются общей retention-политикой завершённых run'ов. IPv4/IPv6 exit IP канонизируются перед сравнением анонимности. `POST /api/v1/admin/validate` возвращает числа `checked`, `alive` и `deferred`; diagnostics и React показывают точные attempts/alive/deferred за последние пять минут, проверки в секунду и прогноз опустошения due-очереди. Prometheus публикует те же rolling-сигналы, `proxyharbor_validation_checks_per_second`, `proxyharbor_validation_estimated_drain_seconds`, failed/active batches и здоровье control endpoint. Еженедельный workflow после полного аудита всех feed'ов запускает реальную validation-партию и требует, чтобы её Alive-набор без расхождений разбирался как JSON, XML, TXT и CSV; машиночитаемые отчёты обоих этапов сохраняются как CI artifacts.

Background collector применяет bounded exponential backoff только к feed’ам с последовательными ошибками; `NextFetchAt` виден в admin API и панели. Ручной `POST /api/v1/admin/collect` всегда принудительно проверяет все включённые источники, поэтому используется для полного аудита 81 endpoint. Пользовательский feed требует непустое имя, известный protocol и публичный HTTPS URL без fragment; path/query сравниваются с учётом регистра, поскольку `/Feed` и `/feed` могут быть разными ресурсами. Из встроенного каталога разрешено менять только `Enabled`, все канонические метаданные неизменяемы. HTTP 404 и другие постоянные 4xx не повторяются, а ответ 2xx без единого распознаваемого прокси считается сбоем, а не ложным успехом.

Каждый ответ источника ограничен 10 МБ, а parser использует линейный non-backtracking regex и хранит не более `MaxProxiesPerSource` уникальных публичных IP. После заполнения лимита он ищет только первый следующий новый адрес: его наличие выставляет точный `lastResultTruncated`, после чего разбор немедленно прекращается; хвост из одних дубликатов не создаёт ложную тревогу. Общий `MaxCandidatesPerRun` независимо фиксируется в истории цикла. Доменные и служебные/private адреса отбрасываются до сохранения. Это удерживает память и CPU в предсказуемых пределах, а полноту результата делает наблюдаемой даже для недоверенного или аномально большого feed'а.

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

Backup хранится в volume `backups` и удаляется по истечении `Backup__RetentionDays`. Локальная retention-политика применяется сразу после атомарной публикации нового архива и не зависит от доступности Telegram, поэтому продолжительный внешний сбой не вызывает неограниченный рост volume. История попыток, размер созданного файла и факт успешной Telegram-доставки сохраняются в `BackupRuns`, доступны через admin diagnostics и Prometheus-метрики и очищаются по `Backup__HistoryRetentionDays`. Чтобы расшифровать файл на доверенной машине:

```powershell
./tools/Decrypt-Backup.ps1 -InputFile ./proxyharbor.phbackup -OutputZip ./proxyharbor.zip -EncryptionKey 'ваш ключ'
```

Результат — обычный ZIP с JSON по таблицам и параметрами сборщика. Проверяйте восстановление на отдельной БД перед аварийной ситуацией. Потеря ключа делает корректно зашифрованный архив невосстановимым.

Для полного восстановления PostgreSQL задайте секреты через окружение и явно подтвердите замену данных:

```powershell
$env:ConnectionStrings__Postgres='Host=localhost;Database=proxyharbor_restore;Username=proxyharbor;Password=...'
$env:Backup__EncryptionKey='ваш ключ'
dotnet run -c Release --project src/ProxyHarbor.Restore -- --input ./proxyharbor.phbackup --replace-existing-data
```

На опубликованном Docker Compose-хосте локальный .NET SDK не требуется. Restore вынесен в opt-in profile `tools`, запускается без root с read-only root filesystem и по умолчанию только показывает справку. Сначала остановите процессы, которые пишут в БД, затем запустите одноразовый контейнер с точным именем backup из volume и после успеха верните сервисы:

```powershell
docker compose stop web api
docker compose --profile tools run --rm restore --input /app/backups/proxyharbor-YYYYMMDD-HHMMSS.phbackup --replace-existing-data
docker compose up -d api web
```

Команда `run --rm` удаляет контейнер и его анонимный `/restore-temp`; постоянный volume `backups` подключён только для чтения. Расшифрованный ZIP никогда не записывается в `backups`. Перед заменой production-данных всё равно выполните пробное восстановление в отдельную БД, переопределив `ConnectionStrings__Postgres` для restore-контейнера.

Restore сначала проверяет аутентификацию backup, manifest, точный allowlist ZIP-записей, отсутствие секретов, дубликатов и oversized settings-файлов, под общей startup-блокировкой применяет миграции, затем заменяет таблицы прокси, источников, циклов сбора, validation-аудита и backup-аудита в одной транзакции. Manifest v4 требует `validation-runs.json` и полный snapshot collector/backup/runtime-настроек, включая AllowedHosts и уровни логирования, но не секретные значения; restore продолжает принимать архивы v2/v3. Потоковый PostgreSQL binary COPY ускоряет импорт больших снимков; при любой ошибке исходные данные целевой БД сохраняются. Готовый `.phbackup` публикуется в каталоге атомарно; plaintext ZIP в актуальной версии не создаётся, а новый запуск под cluster lock всё ещё удаляет legacy ZIP, `.partial` и временные Telegram-части после аварийного завершения. PostgreSQL integration-gate создаёт настоящий зашифрованный снимок, удаляет исходный marker и сравнивает после restore все сохраняемые поля пяти таблиц на отдельных схемах; отдельный повреждённый снимок доказывает rollback без потери прежней target-БД.

Успешная Telegram-доставка записывается в аудит только после HTTP 2xx и обязательного Bot API подтверждения `ok=true` для файла либо каждой нумерованной части. Ответы 429/5xx и временные сетевые ошибки повторяются до трёх раз; `retry_after` принимается как из HTTP-заголовка, так и из JSON `parameters`. Некорректный/слишком большой ответ не считается успехом, bot token исключён из HTTP-логирования, а redirect запрещён.

Если архив превышает Telegram-лимит, сервис отправляет нумерованные части. Сначала объедините их, затем расшифруйте:

```powershell
./tools/Join-BackupParts.ps1 -PartsPattern './proxyharbor.phbackup.part*' -OutputFile './proxyharbor.phbackup'
./tools/Decrypt-Backup.ps1 -InputFile './proxyharbor.phbackup' -OutputZip './proxyharbor.zip' -EncryptionKey 'ваш ключ'
```

## Добавление источника

Добавляйте только список, владелец которого разрешает автоматическую загрузку. Источник должен быть публичным HTTPS URL и отдавать строки `host:port` или `scheme://host:port`. Для строк без схемы применяется выбранный протокол. Приватные и loopback IP отбрасываются.

В каталогах бесплатных прокси категория `HTTPS` означает обычный HTTP proxy с поддержкой метода CONNECT к TLS-назначению. Поэтому поле `protocol` остаётся `Https`, но готовый `url` и TXT-экспорт используют корректный транспорт `http://host:port`; SOCKS4 и SOCKS5 сохраняют собственные URI-схемы.

Полный встроенный каталог, состав провайдеров и команда живого аудита описаны в `docs/SOURCES.md`.

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
3. Подключите HTTPS, внешнюю наблюдаемость и оповещения для production.
4. Выполните тестовый backup, Telegram-доставку и расшифровку.

Лицензия: MIT. Встроенные списки принадлежат их авторам и загружаются во время работы; данные прокси в репозиторий не включены.
