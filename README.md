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
| `Collector__BackgroundWorkersEnabled` | Позволяет отключить workers для миграций, CI или отдельной API-реплики |
| `Collector__ValidationConcurrency` | Параллельность сетевых проверок, по умолчанию 200 |
| `Collector__ValidationBatchSize` | Размер одной очереди, по умолчанию 1000 |
| `Collector__PublicFreshnessMinutes` | Максимальный возраст проверки для публичной выдачи, по умолчанию 15 минут |
| `Collector__DeadRetryBaseMinutes` | Начальная пауза перед повторной проверкой нерабочего прокси, по умолчанию 15 минут |
| `Collector__DeadRetryMaxHours` | Верхняя граница экспоненциальной паузы для нерабочих прокси, по умолчанию 24 часа |
| `Collector__SourceConcurrency` | Параллельность загрузки feed'ов, по умолчанию 8 |
| `Collector__SourceRetryCount` | Повторы временных HTTP/сетевых ошибок, по умолчанию 2 |
| `Collector__MaxProxiesPerSource` | Защитный лимит адресов из одного feed, по умолчанию 100 000 |
| `Collector__MaxCandidatesPerRun` | Защитный лимит уникальных кандидатов за цикл, по умолчанию 500 000 |
| `Collector__RunRetentionDays` | Срок хранения истории циклов, по умолчанию 30 дней |
| `Backup__Enabled` | Включает резервное копирование по расписанию |
| `Backup__EncryptionKey` | Пароль шифрования, минимум 16, рекомендуется 32+ случайных символа |
| `Backup__TelegramBotToken` | Токен Telegram-бота |
| `Backup__TelegramChatId` | ID администратора/чата, куда отправлять архив |
| `Backup__MaxTelegramFileSizeMb` | Лимит отправляемого файла, по умолчанию безопасные 49 МБ |

Секреты не коммитьте. В Production административный ключ должен содержать не менее 24 символов. Секреты не включаются и в backup: архив содержит данные БД, каталог источников, безопасные параметры сборщика/backup и manifest. Telegram получает только зашифрованный `.phbackup`. Формат PHB3 потоково шифрует и аутентифицирует содержимое, порядок блоков и завершающий маркер; расшифровщик сохраняет совместимость с PHB2.

## API

```text
GET  /api/v1/proxies?protocol=Socks5&maxLatencyMs=1000&page=1&pageSize=100
GET  /api/v1/export/json
GET  /api/v1/export/xml
GET  /api/v1/export/txt?protocol=Http
GET  /api/v1/export/csv
GET  /api/v1/stats
```

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

Источниками можно управлять и из React-панели: добавлять собственные HTTPS feed'ы, менять их активность и удалять пользовательские записи. Встроенные источники при удалении безопасно отключаются.

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
cd src/proxyharbor-web; npm run build
```

## Резервные копии и восстановление

Backup хранится в volume `backups` и удаляется по истечении `Backup__RetentionDays`. Чтобы расшифровать файл на доверенной машине:

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

Restore сначала проверяет аутентификацию backup и manifest, применяет миграции, затем заменяет три таблицы в одной транзакции. При любой ошибке исходные данные целевой БД сохраняются.

Если архив превышает Telegram-лимит, сервис отправляет нумерованные части. Сначала объедините их, затем расшифруйте:

```powershell
./tools/Join-BackupParts.ps1 -PartsPattern './proxyharbor.phbackup.part*' -OutputFile './proxyharbor.phbackup'
./tools/Decrypt-Backup.ps1 -InputFile './proxyharbor.phbackup' -OutputZip './proxyharbor.zip' -EncryptionKey 'ваш ключ'
```

## Добавление источника

Добавляйте только список, владелец которого разрешает автоматическую загрузку. Источник должен быть публичным HTTPS URL и отдавать строки `host:port` или `scheme://host:port`. Для строк без схемы применяется выбранный протокол. Приватные и loopback IP отбрасываются.

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
