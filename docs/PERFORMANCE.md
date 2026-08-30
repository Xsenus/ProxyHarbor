# Производительность validation pipeline

Последний live-бенчмарк выполнен **9 августа 2026 года** на одной API-реплике, локальной PostgreSQL и последовательных непересекающихся выборках из общей базы 318 596 кандидатов. Для каждого запуска использовался production `ProxyValidator`: health-gate, PostgreSQL lease, реальные HTTP/HTTPS/SOCKS probes, bulk persistence и `ValidationRuns`. Во всех пяти партиях число сохранённых результатов совпало с claimed, `Deferred` отсутствовали.

| Concurrency | Batch | Попыток | Время audit, с | Попыток/с |
|---:|---:|---:|---:|---:|
| 200 | 1 000 | 1 000 | 36,046 | 27,7 |
| 400 | 1 000 | 1 000 | 23,808 | 42,0 |
| 600 | 1 200 | 1 200 | 18,052 | 66,5 |
| 800 | 1 600 | 1 600 | 18,192 | 88,0 |
| 1 000 | 2 000 | 2 000 | 18,602 | 107,5 |

Production default — **800 / 1 600**. Он ускорил контрольную выборку примерно в 3,2 раза относительно прежнего 200 / 1 000 и оставляет 20% запаса до программного hard limit 1 000. Во время точки 800 процесс занимал около 146 МБ working set, 64 МБ private memory, 49 threads и 881 handle. Docker Compose задаёт API-процессу `nofile=8192`; probes используют async I/O и не создают отдельный thread на соединение.

Claim-план отдельно проверен на откатываемой PostgreSQL-таблице из 300 000 строк со смесью `Alive/Pending/Dead`, future `NextCheckAt` и активных lease. Прежний индекс `(NextCheckAt, CheckLeaseUntil)` не соответствовал фактическому `CASE Status → NextCheckAt → LastCheckedAt`: PostgreSQL читал 300 000 строк и выполнял внешний sort с 29 МБ temporary I/O за 364,8 мс. Concurrent `IX_Proxies_ValidationClaimOrder` точно повторяет operational order; тот же `LIMIT 1600 FOR UPDATE SKIP LOCKED` использовал ordered index scan, отфильтровал только 300 строк и завершился за 4,6 мс. Это измерение конкретного хоста, но отсутствие full sort закреплено точной миграцией и PostgreSQL contract test.

Результат зависит от доли мгновенно отклонённых соединений, таймаутов, ОС, сети и control endpoint, поэтому это не универсальная гарантия. Фактическую ёмкость конкретной установки показывают `proxyharbor_validation_checks_per_second` и `proxyharbor_validation_estimated_drain_seconds`. При устойчивых `Deferred`, росте ошибок control endpoint или нехватке дескрипторов уменьшите `VALIDATION_CONCURRENCY`; размер партии рекомендуется держать не меньше двух волн concurrency.

## Публичная выдача

Операторский `tools/Audit-PublicApiPerformance.ps1` превращает latency в воспроизводимый fail-closed gate. Случайная безопасная база уникальных deep legacy-страниц почти исключает повторное попадание в прежний cache key и измеряет cold `OFFSET + COUNT` путь, повторные `/seek` и `/stats` — прогретый пользовательский путь. По умолчанию требуется p95 не выше 1 500 мс для cold и 250 мс для hot-маршрутов; пороги и `ColdPageBase` параметризованы для осознанной калибровки конкретного production-хоста. Любой HTTP error, rate-limit, неверный Content-Type/JSON или превышение SLO завершает аудит ошибкой, а `-ReportPath` сохраняет сравнимые p50/p95/max/average/bytes метрики.

Последний Production API canary выполнен **10 августа 2026 года** на локальной PostgreSQL с 100 000 свежими Alive-записями, deep legacy `page=40000…40009` и десятью измерениями каждого маршрута. Все 32 запроса завершились успешно: cold legacy list p95 — **136,68 мс**, hot seek p95 — **10,24 мс**, hot stats p95 — **2,44 мс**. Это результат конкретного хоста, а не универсальная гарантия; JSON-аудит позволяет сравнивать те же маршруты после релиза на целевой инфраструктуре.

После добавления retry-safe `REPEATABLE READ` для legacy `items+total` и `/stats` тот же 100 000-row canary повторён на отдельной схеме: cold legacy p95 — **173,46 мс** при SLO 1 500 мс, hot seek — **23,69 мс**, hot stats — **2,76 мс** при SLO 250 мс. Гарантия единой эпохи сохранила большой запас по latency; тестовая схема после измерения удалена.

SQL-план проверен на 310 429 сохранённых прокси с моделированием 148 720 свежих Alive-записей внутри откатываемой транзакции. Прежний deep protocol page (`offset=10000`, `limit=100`) выполнял incremental sort за 56,9 мс; итоговый partial deterministic index сократил его до 11,9 мс, одновременно добавив UUID tie-breaker. В write-heavy MVCC-состоянии точный общий `total` занимал 140,0 мс через parallel sequential scan и 36,1 мс через partial Alive index. В steady-state protocol-specific count использовал отдельный index-only scan за 0,85 мс. Первая страница осталась практически бесплатной — 0,28 мс.

Четыре `IX_Proxies_Alive_*` индекса содержат только публикуемые Alive-строки: два точно соответствуют порядку списка/экспорта, ещё два считают freshness window с протоколом и без него. Миграция строит замену через `CREATE INDEX CONCURRENTLY`, оставляет прежние индексы доступными до окончания build и допускает безопасный повтор после оборванного запуска.

Для полного обхода добавлены keyset-маршруты `/api/v1/proxies/seek` и `/api/v1/export/{format}/seek`. Cursor хранит последнюю тройку `(latency, successfulChecks, UUID)` и fingerprint фильтров; следующий SQL-запрос использует лексикографический предикат и тот же partial index вместо линейно дорожающего `OFFSET`. JSON-страница читает только `pageSize + 1` строку и не выполняет точный `COUNT`; потоковый экспорт делает отдельный bounded boundary lookup, чтобы выставить continuation-заголовки до начала body. Legacy page/offset API остаётся совместимым. Внутри одного legacy page `items` и точный `total` используют общий `REPEATABLE READ` snapshot; `/stats`, `/metrics` и `/admin/diagnostics` тем же способом согласуют все собственные database-derived агрегаты и history reads. Поскольку эти ответы полностью буферизуются до отправки, transaction обёрнута в настроенную EF execution strategy и целиком повторяется при transient database failure. Cursor не фиксирует MVCC snapshot: при обновлении прокси между отдельными запросами возможны обычные изменения живого набора.

Стартовая `/proxies/seek`-страница кэшируется в памяти API на 10 секунд с разделением по рабочим фильтрам. Страницы с `after` намеренно обходят output cache: cursor продолжения почти всегда уникален, поэтому их кэширование расходовало бы ограниченные 32 МБ на одноразовые ответы и вытесняло горячую первую страницу. Locking output cache объединяет одновременные запросы одного стартового набора в один SQL-запрос.

Streaming export дополнительно имеет общий пятиминутный lifetime от открытия `REPEATABLE READ` до финального commit. Лимит охватывает boundary query, перечисление EF и каждый async response write; клиентский backpressure поэтому не может бесконечно удерживать MVCC snapshot и один из двух process-wide export slots.

## Сбор feed'ов

Внутренний parser представляет канонический IP двумя `ulong`, а port/protocol/family — value-полями; performance-contract проверяет, что ключ не содержит managed references и занимает не более 32 байт. Уникальные адреса сразу передаются в общий concurrent bounded-набор. Поэтому каждый из восьми параллельных feed'ов больше не удерживает собственный список кортежей и канонические IP-строки до 500 000 элементов, а общий набор не хранит строки до PostgreSQL COPY.

Детерминированный allocation-contract на 20 000 endpoint'ов требует экономии не менее 32 байт на адрес относительно materialized API парсера. Это соответствует минимум 16 МБ устранённых allocations на один feed при предельных 500 000 адресах и минимум 128 МБ для восьми одновременно разобранных feed'ов; фактическая экономия выше за счёт строк общего набора. Проверка не использует wall-clock timing и поэтому остаётся стабильной на CI разной производительности.

## VPN-каталог

Production baseline 30 августа 2026 года выявил write amplification: при 97 189 VPN endpoint и 149 204 provenance-связях PostgreSQL накопил 28,8 млн и 49,4 млн обновлений соответственно. API после очередного сбора удерживал около 630 MiB. Причиной был EF-путь, который перед каждым импортом загружал и track'ал целиком обе растущие таблицы, а затем каждые пять минут создавал новую MVCC-версию для каждого повторно встреченного адреса.

VPN collection теперь передаёт только текущую bounded-партию через PostgreSQL binary `COPY` во временную таблицу. Одна staging-проекция дедуплицирует endpoint для последующих set-based INSERT/UPDATE, а provenance обновляется отдельной операцией; полный каталог не материализуется в процессе API. Повтор идентичного feed внутри `LastSeenRefreshMinutes` не обновляет endpoint и provenance, поэтому точность `LastSeenAt` остаётся явно bounded настройкой, а WAL, vacuum-нагрузка и индексные перезаписи не зависят от пятиминутного расписания. PostgreSQL integration-contract сравнивает `xmin` обеих строк до и после повторного импорта, запрещает возврат no-op updates и проверяет детерминированный выбор источника по настроенному priority.

Первый production-проход новой схемы обработал 148 доступных источников и 67 204 кандидата примерно за 40 секунд; API после завершения занимал около 270 MiB вместо baseline около 630 MiB. Повторный проход не добавил endpoint/provenance и не изменил `VpnEndpointSources.n_tup_upd`; наблюдавшиеся обновления `VpnEndpoints` относились к параллельному validator. Повреждённые URI с `U+0000`, запрещённым для PostgreSQL text, отбрасываются parser'ом отдельно и больше не откатывают здоровую часть feed.

## Telegram outbound queue

Production baseline при 37 сохранённых заданиях показывал 354 167 sequential scan таблицы `TelegramOutboundMessages`: idle-worker каждые две секунды выполнял восстановление legacy failed, возврат просроченных lease и попытку claim, даже когда очередь была пуста. Объём данных пока мал, но частота запросов росла со временем работы процесса, а не с полезной нагрузкой.

После enqueue локальный bounded wake-signal немедленно будит worker; несколько записей broadcast объединяются в один pulse, а persistent PostgreSQL queue остаётся единственным источником истины. Пятсекундная fallback-проверка сохраняет доставку scheduled-заданий и сообщений другой реплики. Legacy recovery и lease maintenance выполняются не чаще раза в минуту. Таким образом idle-нагрузка снижена примерно с трёх запросов за две секунды до одного claim за пять секунд плюс двух maintenance-запросов в минуту, без увеличения обычной локальной задержки доставки.
