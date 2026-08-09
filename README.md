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

Требуются Docker Engine 26+ и Docker Compose v2.

```bash
cp .env.example .env
# Заполните все обязательные значения в .env
docker compose up -d --build
```

Интерфейс: `http://localhost:8080`. OpenAPI: `http://localhost:8080/openapi/v1.json`. Liveness: `/health/live`, readiness БД: `/health/ready` (совместимый `/healthz` перенаправляет на readiness), Prometheus-метрики: `/metrics`.

Первый полезный список появляется не мгновенно: сервис сначала загружает кандидатов, затем проверяет их пакетами. Скорость регулируется `Collector__ValidationConcurrency`; не повышайте её выше допустимого для вашего лимита файловых дескрипторов и сети.

## Конфигурация

Настройки задаются стандартным способом ASP.NET Core: значения окружения с `__` заменяют секции JSON.

| Переменная | Назначение |
|---|---|
| `ConnectionStrings__Postgres` | Строка подключения PostgreSQL |
| `Security__AdminApiKey` | Ключ заголовка `X-Admin-Key` для `/api/v1/admin/*` |
| `Cors__Origins__0...N` | Явный список доверенных browser origins; в Production по умолчанию пуст |
| `ForwardedHeaders__KnownNetworks__0...N` | CIDR только доверенных reverse proxy; Docker Compose задаёт изолированную `/24` сеть |
| `Collector__BackgroundWorkersEnabled` | Позволяет отключить workers для миграций, CI или отдельной API-реплики |
| `Collector__ValidationConcurrency` | Параллельность сетевых проверок, по умолчанию 200 |
| `Collector__ValidationBatchSize` | Размер одной очереди, по умолчанию 1000 |
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
| `Collector__MaxProxiesPerSource` | Защитный лимит адресов из одного feed, по умолчанию 100 000 |
| `Collector__MaxCandidatesPerRun` | Защитный лимит уникальных кандидатов за цикл, по умолчанию 500 000 |
| `Collector__LastSeenRefreshMinutes` | Минимальный интервал записи повторного обнаружения, по умолчанию 360 минут |
| `Collector__RunRetentionDays` | Срок хранения истории циклов, по умолчанию 30 дней |
| `Backup__Enabled` | Включает резервное копирование по расписанию |
| `Backup__HistoryRetentionDays` | Срок хранения аудита backup-запусков, по умолчанию 365 дней |
| `Backup__EncryptionKey` | Пароль шифрования, минимум 16, рекомендуется 32+ случайных символа |
| `Backup__TelegramBotToken` | Токен Telegram-бота |
| `Backup__TelegramChatId` | ID администратора/чата, куда отправлять архив |
| `Backup__MaxTelegramFileSizeMb` | Лимит отправляемого файла, по умолчанию безопасные 49 МБ |

Секреты не коммитьте. В Production административный ключ должен содержать 24–256 символов без управляющих знаков. CORS-доступ включается только для явно перечисленных origins, а `X-Forwarded-*` принимается только от настроенных proxy-сетей с одним разрешённым переходом. Секреты не включаются и в backup: архив содержит данные БД, каталог источников, безопасные параметры сборщика/backup/runtime, CORS origins, доверенные proxy CIDR и manifest. Для admin key и строки БД сохраняются только флаги наличия, но не значения. Telegram получает только зашифрованный `.phbackup`. Формат PHB3 потоково шифрует и аутентифицирует содержимое, порядок блоков и завершающий маркер; расшифровщик сохраняет совместимость с PHB2.

## API

```text
GET  /api/v1/proxies?protocol=Socks5&maxLatencyMs=1000&page=1&pageSize=100
GET  /api/v1/export/json?maxLatencyMs=1500&minSuccessRate=80
GET  /api/v1/export/xml?protocol=Socks5
GET  /api/v1/export/txt?protocol=Http&maxLatencyMs=1000
GET  /api/v1/export/csv?minSuccessRate=90
GET  /api/v1/stats
```

Экспорт потоково отдаёт до 50 000 свежих проверенных записей, не собирая многомегабайтный ответ в памяти API, и поддерживает те же `protocol`, `maxLatencyMs` и `minSuccessRate`, что публичный список. JSON, XML и CSV содержат стабильные поля protocol/host/port/url/latency/success/exit IP/time; TXT содержит по одному готовому proxy URL на строку. CSV кавычит строки и нейтрализует spreadsheet formula injection. На один IP разрешено 120 обычных публичных запросов, 20 административных и 5 тяжёлых экспортов в минуту; один экземпляр API одновременно формирует не более двух экспортов. При превышении лимита сервис отвечает `429` или `503` и `Retry-After`. Короткий server-side cache сводки и страниц игнорирует неизвестные query-параметры, чтобы ими нельзя было раздувать число cache keys.

Административные запросы требуют `X-Admin-Key`:

```text
GET    /api/v1/admin/sources
GET    /api/v1/admin/diagnostics
POST   /api/v1/admin/sources
PUT    /api/v1/admin/sources/{id}
DELETE /api/v1/admin/sources/{id}
POST   /api/v1/admin/collect
POST   /api/v1/admin/validate
POST   /api/v1/admin/backup
```

`diagnostics` возвращает очередь проверки, состояние встроенного каталога, последние циклы сбора и последние backup-запуски, включая итоговый статус, размер файла и подтверждённый факт Telegram-доставки. Prometheus отдельно публикует completeness и фактическое здоровье каталога через `proxyharbor_source_catalog_complete`, `proxyharbor_source_catalog_healthy`, счётчики встроенных feed'ов/провайдеров, а также backup-сигналы `proxyharbor_last_backup_success`, `proxyharbor_last_backup_sent_to_telegram`, `proxyharbor_last_backup_timestamp_seconds` и `proxyharbor_backup_runs_active`.

Повторный запуск `collect` или `backup`, пока операция уже выполняется этой или другой репликой, немедленно получает HTTP `409`. Для `validate` тот же ответ действует внутри одной реплики; разные реплики безопасно арендуют непересекающиеся пакеты PostgreSQL. Долгие административные запросы поэтому не накапливаются в локальной очереди.

Перед арендой validation-пакета сервис напрямую проверяет control endpoint и кэширует результат на короткий срок. Если endpoint недоступен, очередь не арендуется. Если уже установленный TLS-туннель получил от control endpoint ошибочный HTTP/JSON-ответ, результат помечается `deferred`: lease освобождается, повтор назначается через минуту, но Status, latency, счётчики успехов/ошибок и failure streak прокси не изменяются. `POST /api/v1/admin/validate` возвращает числа `checked`, `alive` и `deferred`; Prometheus публикует `proxyharbor_probe_control_available` (`-1` до первой проверки, `0` при сбое, `1` при успехе) и время последней проверки.

Background collector применяет bounded exponential backoff только к feed’ам с последовательными ошибками; `NextFetchAt` виден в admin API и панели. Ручной `POST /api/v1/admin/collect` всегда принудительно проверяет все включённые источники, поэтому используется для полного аудита 81 endpoint. HTTP 404 и другие постоянные 4xx не повторяются, а ответ 2xx без единого распознаваемого прокси считается сбоем, а не ложным успехом.

Источниками можно управлять и из React-панели: добавлять собственные HTTPS feed'ы, менять их активность и удалять пользовательские записи. Встроенные источники помечены провайдером и рангом; их канонические URL и протоколы неизменяемы, а удаление безопасно переводит запись на паузу. Административная панель также показывает размер PostgreSQL, очередь проверки, последние циклы сбора и историю backup с подтверждённым состоянием Telegram-доставки; данные автоматически обновляются каждые 15 секунд и после административных действий.

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
dotnet build ProxyHarbor.slnx -c Release
dotnet test ProxyHarbor.slnx -c Release
cd src/proxyharbor-web
npm run lint
npm test
npm run build
```

## Резервные копии и восстановление

Backup хранится в volume `backups` и удаляется по истечении `Backup__RetentionDays`. История попыток, размер созданного файла и факт успешной Telegram-доставки сохраняются в `BackupRuns`, доступны через admin diagnostics и Prometheus-метрики и очищаются по `Backup__HistoryRetentionDays`. Чтобы расшифровать файл на доверенной машине:

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

Restore сначала проверяет аутентификацию backup, manifest, отсутствие секретов и дублирующихся ZIP-записей, применяет миграции, затем заменяет таблицы прокси, источников, циклов сбора и аудита backup в одной транзакции. Формат manifest v3 сохраняет историю backup, а restore продолжает принимать прежние архивы v2. Потоковый PostgreSQL binary COPY ускоряет импорт больших снимков; при любой ошибке исходные данные целевой БД сохраняются. Готовый `.phbackup` публикуется в каталоге атомарно, поэтому внешние задачи не увидят недописанный архив.

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
