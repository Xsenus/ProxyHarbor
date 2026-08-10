# API ProxyHarbor

## Общие правила

- Base path: `/api/v1`.
- JSON использует camelCase; enum сериализуются строками (`Http`, `Https`, `Socks4`, `Socks5`).
- Публично возвращаются только свежие объективно проверенные Alive-прокси.
- Ошибки валидации, конфликтов и throttling возвращаются как `application/problem+json`/`ProblemDetails`.
- OpenAPI текущего процесса доступен по `/openapi/v1.json` и является authoritative контрактом generated clients.
- Admin endpoints требуют ровно один `X-Admin-Key` и возвращают `Cache-Control: no-store`.
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
  "lastCheckedAt": "2026-08-10T16:30:00Z"
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

Filter fingerprint включён в cursor. Cursor нельзя использовать с другим набором фильтров.

## GET `/api/v1/proxies`

Legacy offset page с точным `total`.

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
| `json` | `application/json` | Массив `ProxyDto` |
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

## GET `/api/v1/export/{format}/seek`

Предпочтительный экспорт больших наборов.

| Параметр | Default | Ограничение |
|---|---:|---:|
| `limit` | `50000` | 1..50000 |
| `after` | null | Cursor из `X-Next-Cursor` |

Ответ содержит `X-Export-Cursor` и, если есть следующая страница, `X-Next-Cursor`.

Boundary metadata и тело одного export читаются из одного PostgreSQL `REPEATABLE READ` snapshot. Один процесс одновременно формирует не более двух экспортов. Полная lifetime одного export — не более пяти минут; медленный клиент не может бесконечно удерживать DB snapshot и slot.

CSV нейтрализует spreadsheet formula injection. JSON/XML/TXT/CSV используют одинаковый порядок и множество строк для одинаковых фильтров/snapshot.

## GET `/api/v1/sources`

Публичный неизменяемый каталог без runtime errors и внутренних идентификаторов.

```json
{
  "lastAuditedOn": "2026-08-10",
  "feedCount": 81,
  "providerCount": 50,
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

## Admin authentication

```powershell
$adminHeaders = @{ 'X-Admin-Key' = $env:ADMIN_API_KEY }
Invoke-RestMethod https://proxy.example.com/api/v1/admin/diagnostics -Headers $adminHeaders
```

Отсутствующий, пустой, oversized или многозначный header возвращает `401`, `WWW-Authenticate: ApiKey realm="ProxyHarbor"` и `ProblemDetails`. Значение ключа никогда не выводится в log.

## Управление источниками

### GET `/api/v1/admin/sources`

Возвращает runtime state, conditional validators evidence, errors, completeness flags и built-in metadata.

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

## Rate limits

На один remote IP:

| Policy | Limit |
|---|---|
| Public | 120 запросов/мин, sliding window |
| Admin | 20 запросов/мин, fixed window |
| Export | 5 token/мин, token bucket |

`/health/live` не ограничивается. При throttling возвращаются `429`, `Retry-After` и `ProblemDetails`. Если заняты оба global export slots, возвращается `503` и `Retry-After: 1`.

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
| `401` | Нет корректного `X-Admin-Key` |
| `404` | Admin source не найден |
| `409` | Operation lock/restore conflict или duplicate/immutable source |
| `429` | Rate limit |
| `503` | Export capacity/timeout до начала response или неготовая БД |

После начала streaming response server-side timeout отменяет соединение; сформировать новый JSON error в уже отправленном body невозможно.
