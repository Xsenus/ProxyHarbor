# API ProxyHarbor

## Общие правила

- Base path: `/api/v1`.
- JSON использует camelCase; enum сериализуются строками (`Http`, `Https`, `Socks4`, `Socks5`).
- Публично возвращаются только свежие объективно проверенные Alive-прокси.
- Ошибки валидации, конфликтов и throttling возвращаются как `application/problem+json`/`ProblemDetails`.
- OpenAPI текущего процесса доступен по `/openapi/v1.json` и является authoritative контрактом generated clients.
- Admin endpoints принимают защищённую browser cookie-сессию или ровно один automation-заголовок `X-Admin-Key` и возвращают `Cache-Control: no-store`.
- Production API следует вызывать только через HTTPS gateway.

## Модель публичного прокси

```json
{
  "host": "203.0.113.10",
  "port": 1080,
  "protocol": "Socks5",
  "url": "socks5://203.0.113.10:1080",
  "latencyMs": 321,
  "successRate": 87.5,
  "exitIp": "198.51.100.25",
  "countryCode": "DE",
  "lastCheckedAt": "2026-08-24T10:00:00Z",
  "firstAliveAt": "2026-08-23T08:00:00Z",
  "lastAliveAt": "2026-08-24T10:00:00Z",
  "activeSince": "2026-08-24T06:00:00Z",
  "activeForSeconds": 14400
}
```

Категория `Https` во free-proxy feed обычно означает HTTP proxy с CONNECT, поэтому готовый transport URL имеет схему `http://`; поле `protocol` при этом остаётся `Https`.

## Фильтры

Одинаковые фильтры поддерживают list, seek и export:

| Параметр | Тип | Значение |
|---|---|---|
| `protocol` | enum | `Http`, `Https`, `Socks4`, `Socks5` |
| `maxLatencyMs` | integer ≥ 1 | Верхняя граница измеренной latency |
| `minSuccessRate` | decimal 0..100 | Минимальная доля успешных объективных проверок |
| `country` | повторяемый ISO alpha-2 | Одна или несколько стран: `country=DE&country=NL`; также принимается `DE,NL` |

Filter fingerprint включён в cursor. Cursor нельзя использовать с другим набором фильтров.

## GET `/api/v1/proxies`

Страница с точным `total`; этот контракт использует React-интерфейс для серверной пагинации.

Дополнительные параметры:

| Параметр | Default | Ограничение |
|---|---:|---:|
| `page` | `1` | Минимум 1 |
| `pageSize` | `100` | Clamp 1..1000 |

Смещение больше 5 000 000 отклоняется с `400`; для большого обхода используйте seek.

```bash
curl 'https://proxy.example.com/api/v1/proxies?protocol=Http&page=1&pageSize=100'
```

```json
{
  "items": [],
  "page": 1,
  "pageSize": 100,
  "total": 0
}
```

`items` и `total` принадлежат одному PostgreSQL repeatable-read snapshot.

## GET `/api/v1/proxies/countries`

Возвращает доступные страны среди свежих Alive-прокси и количество адресов в каждой стране. Список отсортирован по убыванию количества, затем по ISO-коду.

```json
[
  { "code": "DE", "count": 142 },
  { "code": "NL", "count": 87 }
]
```

Страна определяется локально по подтверждённому `exitIp`; неизвестные адреса остаются с `countryCode: null` и показываются только при отсутствии country-фильтра.

## GET `/api/v1/proxies/seek`

Keyset pagination без точного `COUNT` и растущего `OFFSET`.

| Параметр | Default | Ограничение |
|---|---:|---:|
| `pageSize` | `100` | Clamp 1..1000 |
| `after` | null | Непрозрачный cursor из `nextCursor` |

```json
{
  "items": [],
  "pageSize": 100,
  "hasMore": false,
  "nextCursor": null
}
```

Алгоритм клиента:

1. вызовите endpoint без `after`;
2. обработайте `items`;
3. если `hasMore=true`, передайте `nextCursor` как `after` с теми же фильтрами;
4. завершите при `hasMore=false`.

## GET `/api/v1/export/{format}`

Legacy streaming export с `offset`.

Поддерживаемые `format`:

| Format | Content-Type | Представление |
|---|---|---|
| `json` | `application/json` | Для платного доступа — массив `ProxyDto`; для free — объект `access` + массив `proxies` |
| `xml` | `application/xml` | `<proxies><proxy>…` |
| `txt` | `text/plain` | Один канонический URL на строку |
| `csv` | `text/csv` | Header и quoted fields |

Дополнительные параметры:

| Параметр | Default | Ограничение |
|---|---:|---:|
| `limit` | `50000` | 1..50000 |
| `offset` | `0` | 0..5000000 |

```bash
curl -OJ 'https://proxy.example.com/api/v1/export/txt?protocol=Socks5&limit=10000'
```

Ответ содержит:

- `Content-Disposition` с безопасным именем файла;
- `X-Export-Limit`;
- `X-Export-Offset`;
- `X-Export-Truncated`;
- `X-Next-Offset`, если вероятно продолжение.

### Бесплатная и платная выдача

Сервер проверяет активную подписку при каждом export-запросе. `Pro`, `Unlimited`, их
действующий trial и администратор сохраняют запрошенный `limit` до 50 000. Аккаунт
на `free` получает 10 записей из центральной части отфильтрованного рейтинга — не
premium-верхушку и не худший хвост. Для гостя тот же лимит привязан к IP. Следующая
бесплатная выгрузка доступна ровно через 600 секунд; состояние хранится в PostgreSQL
и не сбрасывается при рестарте API.

Успешный free JSON имеет форму:

```json
{
  "access": {
    "tier": "free",
    "limited": true,
    "limit": 10,
    "cooldownSeconds": 600,
    "nextAllowedAt": "2026-08-26T12:10:00Z",
    "message": "Бесплатный доступ: 10 прокси среднего качества раз в 10 минут. Для неограниченного доступа купите подписку.",
    "upgradeUrl": "/account"
  },
  "proxies": []
}
```

TXT, CSV и XML сохраняют прежнюю машинно-читаемую структуру без рекламных строк.
Во всех free-форматах сервер добавляет `X-Access-Tier: free`,
`X-Free-Cooldown: 600` и `Link: </account>; rel="upgrade"`. Повтор до истечения
интервала возвращает `429`, `Retry-After` и `ProblemDetails` с `nextAllowedAt`,
`limit`, `cooldownSeconds` и `upgradeUrl`. Параметры `limit`, `offset` и `after` не
позволяют увеличить или сместить бесплатную выборку.

## GET `/api/v1/export/{format}/seek`

Предпочтительный экспорт больших наборов.

| Параметр | Default | Ограничение |
|---|---:|---:|
| `limit` | `50000` | 1..50000 |
| `after` | null | Cursor из `X-Next-Cursor` |

Ответ содержит `X-Export-Cursor` и, если есть следующая страница, `X-Next-Cursor`.

Boundary metadata и тело одного export читаются из одного PostgreSQL `REPEATABLE READ` snapshot. Один процесс одновременно формирует не более двух экспортов. Полная lifetime одного export — не более пяти минут; медленный клиент не может бесконечно удерживать DB snapshot и slot.

CSV нейтрализует spreadsheet formula injection и содержит `countryCode`. JSON/XML/TXT/CSV используют одинаковый порядок и множество строк для одинаковых фильтров/snapshot.

## GET `/api/v1/sources`

Публичный неизменяемый каталог без runtime errors и внутренних идентификаторов.

```json
{
  "lastAuditedOn": "2026-08-26",
  "feedCount": 198,
  "providerCount": 75,
  "providers": [
    {
      "rank": 1,
      "name": "ProxyScrape",
      "protocols": ["Http"],
      "feeds": [
        {
          "rank": 1,
          "name": "ProxyScrape V4 Mixed",
          "url": "https://example.invalid/feed",
          "protocol": "Http"
        }
      ]
    }
  ]
}
```

Фактические URL перечислены в [SOURCE_CATALOG.md](SOURCE_CATALOG.md).

## GET `/api/v1/stats`

Один согласованный snapshot публичной статистики:

- `alive`, `staleAlive`, `pending`, `dead`;
- `dueForCheck`, `checksInProgress`, `scheduledChecks`;
- `averageLatencyMs`;
- source health counters;
- `byProtocol`;
- безопасная публичная часть последнего collection run.

Внутренние UUID и тексты ошибок намеренно отсутствуют.

## Health и служебные endpoint

| Endpoint | Назначение |
|---|---|
| `GET /health/live` | Процесс способен отвечать; не зависит от БД |
| `GET /health/ready` | PostgreSQL доступна и содержит актуальную рабочую schema |
| `GET /healthz` | Redirect на readiness для совместимости |
| `GET /metrics` | Prometheus exposition; production gateway не публикует его наружу |
| `GET /openapi/v1.json` | OpenAPI v1 |

Readiness не кэшируется и выполняет zero-row probe всех operational tables/columns.

## Account authentication

Браузер создаёт сессию, отправляя JSON на `POST /api/v1/auth/login`:

```json
{ "username": "admin или admin@example.com", "password": "..." }
```

Успешный ответ устанавливает непостоянную `HttpOnly`, `Secure`, `SameSite=Strict` cookie `ProxyHarbor.Session`. `GET /api/v1/auth/session` возвращает профиль, роли, подписку и entitlement, `POST /api/v1/auth/logout` завершает сессию. Login rate limit — 5 попыток за 5 минут на IP; после пяти неверных паролей Identity блокирует аккаунт на 15 минут.

| Endpoint | Назначение |
|---|---|
| `POST /api/v1/auth/register` | Создать аккаунт с ролью `User` и тарифом `free` |
| `POST /api/v1/auth/forgot-password` | Отправить нейтральный reset-response без account enumeration |
| `POST /api/v1/auth/reset-password` | Применить одноразовый Identity token |
| `GET /api/v1/account/profile` | Получить профиль, роли и подписку |
| `PUT /api/v1/account/profile` | Изменить отображаемое имя |
| `POST /api/v1/account/change-password` | Сменить пароль с проверкой текущего |

Администратор управляет ролями аккаунта через `GET /api/v1/admin/users` и `PUT /api/v1/admin/users/{id}`. Реестр пользователей всегда постраничный (`page`, `pageSize`, максимум 100 записей) и поддерживает серверные фильтры `search` (имя, логин или почта), `activity=active|disabled` и `plan=free|pro|unlimited`. Роли всех строк страницы загружаются пакетно, без N+1 запросов. Удаление роли у последнего администратора запрещено. Коммерческий доступ и защита выдачи вынесены в отдельные реестры:

| Endpoint | Назначение |
|---|---|
| `GET /api/v1/admin/payments/orders` | Страница счетов; фильтры `status`, `provider`, `query` и сводка по статусам |
| `GET /api/v1/admin/subscriptions` | Подписки с фильтрами и показателями active/trial/suspended/expiring |
| `PUT /api/v1/admin/subscriptions/{id}` | Изменить тариф/статус/дату либо продлить на `extensionDays`; действие аудируется |
| `GET /api/v1/admin/access` | Агрегаты выдачи за 30 дней, активные блокировки и самые нагружающие IP |
| `GET /api/v1/admin/access/visitors` | Постраничные IP/аккаунты посетителей, просмотры и сводка за 30 дней |
| `POST /api/v1/admin/access/rules` | Заблокировать точный IP, CIDR или UUID пользователя с причиной и сроком |
| `PUT /api/v1/admin/access/rules/{id}` | Включить, выключить либо изменить срок правила |
| `GET/PUT /api/v1/admin/telegram` | Статистика и настройка commerce-бота без выдачи сохранённых секретов |
| `POST /api/v1/admin/telegram/provision` | Повторно применить профиль, изображение, команды и webhook/polling |
| `GET /api/v1/admin/telegram/chats` | Постраничный CRM-каталог Telegram-диалогов |
| `GET /api/v1/admin/telegram/chats/{id}/messages` | Последние сообщения CRM-диалога |
| `PUT /api/v1/admin/telegram/chats/{id}` | Настроить уведомления или блокировку чата |
| `POST /api/v1/admin/telegram/messages` | Поставить личный ответ или bounded broadcast в очередь |

Публичный `POST /api/v1/telemetry/visit` принимает только pathname текущей SPA-страницы и сводит его к фиксированному коду. Query/fragment не сохраняются, рекламные cookies не создаются, `Sec-GPC: 1` отключает запись, а IP-агрегаты удаляются через 90 дней. Публичный `POST /api/v1/telegram/webhook/{username}` принимает только update выбранного бота, ограничен 1 MiB/rate limit и требует секретный заголовок Telegram. Полная эксплуатация и Stars flow описаны в [TELEGRAM_BOT.md](TELEGRAM_BOT.md).

Для CLI и automation сохранён API key:

```powershell
$adminHeaders = @{ 'X-Admin-Key' = $env:ADMIN_API_KEY }
Invoke-RestMethod https://proxy.example.com/api/v1/admin/diagnostics -Headers $adminHeaders
```

Отсутствующая сессия и отсутствующий, пустой, oversized или многозначный header возвращают `401`, challenge `Cookie, ApiKey` и `ProblemDetails`. Аутентифицированный пользователь без роли `Administrator` получает `403`. Credentials никогда не выводятся в log.

## Административный реестр прокси

### GET `/api/v1/admin/proxies`

Защищённый реестр всех когда-либо обнаруженных прокси. Поддерживает серверную пагинацию
`page`, `pageSize` (`10..100`), фильтры `status` (`Pending`, `Alive`, `Dead`), `protocol`,
двухбуквенный `country`, поиск `query` по адресу/выходному IP и сортировку `sort`:
`lastChecked`, `active`, `latency` или `lastSeen`.

Ответ содержит `items`, `page`, `pageSize`, `total`, глобальную `summary` и список
`countries`. Для каждого адреса возвращаются текущее состояние, задержка, страна,
счётчики и процент успешных проверок, первое/последнее обнаружение, первое/последнее
успешное подключение и `activeForSeconds` — длительность текущей непрерывной Alive-серии.
Сводка включает свежие и устаревшие Alive, Pending, Dead, ever-alive, среднюю задержку,
число стран и самый длинный текущий uptime. Lease-токены валидатора в API не выдаются.

## Управление источниками

### GET `/api/v1/admin/sources`

Параметры: `page` (от 1), `pageSize` (`10..100`, по умолчанию `10`) и необязательный
`search` (до 200 символов, поиск без учёта регистра по названию, URL и провайдеру). Возвращает
`PagedResult<SourceResponse>` с полями `items`, `page`, `pageSize`, `total`.
Строки стабильно упорядочены по priority, имени и идентификатору; каждая содержит
runtime state, conditional validators evidence, errors, completeness flags и built-in metadata.

### GET `/api/v1/admin/sources/{id}`

`200` либо `404`.

### POST `/api/v1/admin/sources`

```json
{
  "name": "My provider",
  "url": "https://provider.example/proxies.txt",
  "protocol": "Socks5",
  "priority": 100,
  "enabled": true
}
```

Требования:

- name после trim: 2..120;
- URL: до 2048 символов, публичный HTTPS, standard port, без credentials/fragment;
- protocol определён enum;
- priority: -10000..10000;
- URL уникален.

DNS проверяется до сохранения. Collection/source mutation conflict возвращает `409`.

### PUT `/api/v1/admin/sources/{id}`

Пользовательский feed изменяется полностью. У built-in разрешено менять только `enabled`; name/URL/protocol/rank immutable. Смена пользовательского endpoint сбрасывает stale conditional/error state.

### DELETE `/api/v1/admin/sources/{id}`

Пользовательский feed удаляется. Built-in устойчиво переводится в `enabled=false`, потому что startup seed восстановил бы физически удалённую строку.

## GET `/api/v1/admin/diagnostics`

Один repeatable-read operator snapshot:

- server time и размер БД;
- total/leased/due/scheduled validation queue;
- attempts/checks/alive/deferred за пять минут;
- checks per second и estimated drain;
- source catalog completeness/health/staleness/truncation;
- последние collection, validation и backup run’ы;
- `TelegramConfigured`/`SentToTelegram` для backup.

## POST `/api/v1/admin/collect`

Принудительный полный collection. Игнорирует source backoff и conditional validators, требует новый body каждого включённого feed’а. Возвращает `CollectionRun`; concurrent collection/restore даёт `409`.

## POST `/api/v1/admin/validate`

Проверяет одну доступную lease-партию:

```json
{
  "checked": 1600,
  "alive": 4,
  "deferred": 0
}
```

## POST `/api/v1/admin/backup`

Создаёт и отправляет один backup:

```json
{
  "created": "proxyharbor-20260810-235000-0000.phbackup",
  "sentToTelegram": true
}
```

Повторный локальный/cluster-wide запуск даёт `409`. Ошибка Telegram не возвращает ложный success; локальный encrypted file и failed audit сохраняются.

## GET `/api/v1/admin/backups`

Возвращает постраничную историю резервного копирования (`page`, `pageSize`, `total`). У каждой записи есть вычисляемый флаг `available`: он равен `true`, когда опубликованный зашифрованный файл ещё находится в локальном volume. Это включает корректный локальный файл при отдельной ошибке Telegram-доставки. Старые audit-записи могут сохраняться дольше файлов согласно отдельным срокам хранения.

## GET `/api/v1/admin/backups/{id}/download`

Потоково скачивает исходный зашифрованный `.phbackup` с поддержкой range-запросов. API не расшифровывает архив и не раскрывает путь на сервере. Если файл уже удалён политикой хранения, возвращается `404`.

## DELETE `/api/v1/admin/backups/{id}`

Удаляет локальный файл и его строку истории. Выполняющийся backup удалить нельзя (`409`). Идентификатор всегда разрешается через audit-запись, а имя дополнительно проверяется на точное соответствие namespace файлов ProxyHarbor — произвольные пути не принимаются.

## Rate limits

На один remote IP:

| Policy | Limit |
|---|---|
| Public | 120 запросов/мин, sliding window |
| Admin | 20 запросов/мин, fixed window |
| Export | 5 token/мин, token bucket |

`/health/live` не ограничивается. При throttling возвращаются `429`, `Retry-After` и `ProblemDetails`. Если заняты оба global export slots, возвращается `503` и `Retry-After: 1`.

Каталог и экспорт дополнительно проходят через локальный снимок административных block rules — без запроса к БД на каждой выдаче. Счётчики накапливаются в памяти и раз в 15 секунд сохраняются пятиминутными PostgreSQL-агрегатами. Сырые IP-агрегаты автоматически удаляются через 90 дней, а UI показывает рабочее 30-дневное окно. В горизонтальном deployment каждая реплика перечитывает правила не реже раза в минуту; для общего распределённого quota следующей стадией предусмотрен Redis-backed limiter.

## Кэширование

- legacy public list: 10 секунд, vary только по известным query fields;
- первая seek page: 10 секунд; cursor continuations не создают одноразовые cache keys;
- stats/source catalog/OpenAPI: 15 секунд;
- admin/health readiness: не кэшируются.

Неизвестный query parameter не создаёт новый cache key.

## Ошибки клиента

Типовые ответы:

| Status | Причина |
|---:|---|
| `400` | Невалидный enum/filter/cursor/format/URL или слишком глубокий offset |
| `401` | Нет корректной admin cookie-сессии или `X-Admin-Key` |
| `404` | Admin source не найден |
| `409` | Operation lock/restore conflict или duplicate/immutable source |
| `429` | Rate limit |
| `503` | Export capacity/timeout до начала response или неготовая БД |

После начала streaming response server-side timeout отменяет соединение; сформировать новый JSON error в уже отправленном body невозможно.
