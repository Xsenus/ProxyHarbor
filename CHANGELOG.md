# Changelog

Все заметные изменения ProxyHarbor документируются в этом файле. Формат основан на
[Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версии следуют Semantic Versioning.

## [Unreleased]

### Added

- Локальный cross-platform transport-canary проверяет полный HTTP/HTTPS/SOCKS4a/SOCKS5 proxy probe `handshake → TLS 1.2/1.3 → bounded HTTP framing → canonical exit IP → anonymity`; keep-alive остаётся открытым до завершения проверки, недоверенный сертификат отклоняется системной TLS-валидацией, зависший CONNECT становится bounded `timeout`, а caller cancellation пробрасывается без ложного Dead.
- Backup pipeline получил симметричные bounded failure-canary: сбой ZIP producer обязан отменить PHB3 encryptor без потери исходного исключения и partial-файла, а мгновенный сбой encryptor — отменить зависший DB producer без deadlock.
- PostgreSQL restore cancellation-canary останавливает процесс как после первой реально записанной binary `COPY` row, так и после завершения всех пяти `COPY` непосредственно перед `COMMIT`; оба сценария доказывают полный rollback исходной БД, отсутствие частично импортированных данных, стандартный exit code 130 и удаление расшифрованного временного ZIP.
- PostgreSQL backup shutdown-canary отменяет процесс строго во время Telegram upload, затем требует закрытые file handles, отсутствие `.partial/.part*`, криптографически пригодный локальный PHB3, завершённый `failed` audit и отсутствие Telegram secrets в error.
- PostgreSQL status-evidence trust boundary гарантирует, что публичный `Alive` имеет `LastCheckedAt`, измеренную latency и хотя бы одну успешную проверку, а `Dead` — дату и неуспешную проверку; rollout возвращает неподтверждённые legacy-строки в немедленную `Pending`-очередь до неблокирующей валидации constraint.
- PostgreSQL backup integration-gate теперь коммитит source из второй сессии строго между чтением `proxies.json` и `sources.json`; архив обязан сохранить единую старую эпоху, тогда как живая БД подтверждает поздний commit, напрямую доказывая multi-table `REPEATABLE READ` snapshot.
- Публичная React-панель раскрывает в каждой из 50 provider-карточек полный список конкретных feed'ов: все 81 HTTPS URL, имя и заявленный HTTP/HTTPS/SOCKS4/SOCKS5 protocol доступны как keyboard/touch-friendly ссылки вместо прежней ссылки только на первый feed.
- OpenAPI admin-контракт теперь явно описывает общий `401 ProblemDetails` и реальные success/400/404/409 responses, включая cluster-wide конфликт source mutation с collection, поэтому сгенерированные клиенты больше не предполагают только happy path.
- Admin diagnostics получил типизированные `DiagnosticsResponse`/`ValidationQueueResponse`, поэтому OpenAPI и generated clients видят полный operator snapshot вместо schema-less `200`.
- PostgreSQL CI process-smoke fail-closed проверяет фактически сгенерированный `/openapi/v1.json`: `AdminApiKey`, operation security, точные response-коды source CRUD и `ProblemDetails` schema для collection conflicts.
- Воспроизводимый `Test-BuiltInSourceEndpoints.ps1` выполняет bounded parallel live-аудит 81 feed/50 технических владельцев без системного proxy и публикует JSON failures; network-free `-CatalogOnly` contract включён в CI/release. Четыре последовательных live-run 10 августа подтвердили 81/81 endpoint с `IP:port` и нулём ошибок; последний прогон завершился с worst-case 1,130 мс, максимум серии — 3,110 мс.
- Production Caddy получил end-to-end Docker healthcheck: pinned image `curl` обращается к API `/health/ready` через внутренний TLS listener с `PUBLIC_HOST` одновременно как SNI и Host; проверка охватывает TLS → gateway → API → PostgreSQL, Compose contract фиксирует bounded timing, а container smoke требует фактический статус `healthy`.
- Публичный `GET /api/v1/sources` и React-панель раскрывают полный встроенный каталог как 50 независимых провайдеров и 81 feed без административных ошибок, backoff и других эксплуатационных полей.

### Changed

- Ручные admin `validate` и `backup` больше не публикуют schema-less anonymous `200`: именованные `ValidationTriggerResponse`/`BackupTriggerResponse` дают generated clients точные поля, reflection contract и два Release process-smoke проверяют реальные OpenAPI `$ref` и primitive types.
- Test/coverage toolchain обновлён до согласованной для modern VSTest связки `Microsoft.NET.Test.Sdk` 18.8.1 и `coverlet.collector` 10.0.1; locked restore, 489 Release-тестов, Cobertura attachment, внутренний coverage-floor и повторный NuGet vulnerability audit проходят без исключений.
- Dependabot теперь обновляет `Microsoft.NET.Test.Sdk` и `coverlet.*` одним атомарным `dotnet-tests` PR вместо несовместимых runtime/test PR; supply-chain gate и негативный fixture fail-closed защищают эту группировку.
- Финальный source connect ограничивает один DNS-ответ максимум 32 публичными адресами до открытия socket; mixed private/public и oversized fan-out отклоняются fail-closed, а детерминированный gate доказывает public-only fallback и немедленную caller cancellation.
- `/health/ready` больше не кэширует успешный ответ и выполняет zero-row schema probe всех пяти operational tables и актуальных колонок; доступный PostgreSQL с удалённой/устаревшей схемой теперь немедленно возвращает `503`, а не ложный `healthy`.
- PHB3 writer после финального аутентифицированного маркера выполняет единственный `Flush(true)` до self-verification и atomic publish: completed audit, retention и Telegram больше не могут опередить durable запись ciphertext при power-loss, при этом каждый мегабайтный блок не переводится в дорогой `WriteThrough`.
- Frontend lockfile обновлён до совместимых `@testing-library/jest-dom` 7.0.1 и `lucide-react` 1.31.0; повторный NPM/NuGet audit не обнаружил известных уязвимостей.
- Сборка Telegram backup-частей теперь fail-closed проверяет единый base-name/total, непрерывность `1..N`, размеры и атомарное создание output; CI отклоняет missing, mixed и malformed наборы.
- Source-feed audit artifact теперь сохраняет длительность сбора, processed/skipped, unique candidates и new proxies; forced-аудит fail-closed отклоняет любой skip, пустую очередь кандидатов и несогласованные счётчики.
- Backup encryption/decryption и PowerShell restore теперь отклоняют unpaired UTF-16 surrogate, строго кодируют ключ в UTF-8 перед PBKDF2 и очищают временные password bytes, исключая коллизии через replacement characters без потери корректных 16-символьных legacy-ключей.
- Публичный React-каталог позволяет повторить временно неудачную загрузку, отменяет и инвалидирует устаревшие ответы, а справочные 50/81 больше не маскируют предупреждение об усечённых source feed'ах.
- Документация теперь содержит просматриваемый топ-50 всех встроенных proxy-провайдеров с прямыми ссылками, протоколами, числом feed'ов и явным разделением source-аудита от проверки живости отдельных адресов.
- React polling теперь планирует следующий public/admin refresh только после завершения текущего, отменяет устаревшие fetch при смене фильтра, ключа или закрытии диалога и не позволяет старому admin-ответу восстановить удалённую сессию.
- Production admin-key policy теперь едина со middleware, отклоняет whitespace-only и некорректный UTF-16, а strict UTF-8 hashing исключает коллизии через replacement characters.
- Source HTTP validators больше не передаются redirect-target и не сохраняются для перенаправленной representation; неожиданный `304` после redirect отклоняется, исключая cross-origin ETag leakage и ложную неизменность при смене `Location`.
- Validator повторно проверяет каноничность и публичность proxy IP непосредственно перед открытием TCP socket, поэтому повреждённая или вручную вставленная строка БД не может превратить сервис в сканер внутренних сетей.
- Collector больше не записывает ETag, fetch timeline и health старого endpoint поверх источника, который администратор перенастроил во время HTTP-загрузки.
- CI и release теперь одинаково запускают закреплённый `actionlint` с проверкой официального SHA-256 до распаковки; отдельный contract test защищает version/hash pins и подключение gate в обоих workflow.
- Conditional source fetch теперь периодически выполняет обязательный full-body refresh и сохраняет `LastContentFetchedAt`, поэтому сочетание вечного `304` и dead retention не может навсегда удалить кандидата из неизменившегося feed.
- Origin IP cache теперь атомарно публикует immutable value+expiry snapshot для lock-free fast path при сотнях параллельных proxy probes.
- JSON/XML/TXT/CSV exports теперь гарантированно привязывают каждую запись и финальный writer flush к request cancellation, поэтому оборванный медленный клиент сразу освобождает ограниченный export slot.
- Legacy export fail-fast отклоняет OFFSET свыше 5 млн, а seek вычисляет hasMore и следующий cursor одним index-only boundary-запросом вместо двух.
- Исходящие source, origin и Telegram HTTP-клиенты принудительно работают без системного proxy, поэтому DNS-rebinding connect-gate нельзя обойти переносом разрешения target на посредника.
- Потоковое PHB2/PHB3 шифрование и restore переиспользуют bounded buffers на протяжении всего архива, устраняя по две Large Object Heap allocation на каждый блок с сохранением криптографической очистки plaintext.
- Создание backup теперь fail-closed требует bounded 32–1024-символьный ключ и абсолютный не-корневой каталог; restore сохраняет 16-символьную legacy-совместимость.
- Новый backup полностью self-verifies все PHB3 AEAD-блоки до атомарной публикации, retention и Telegram; повреждённый partial никогда не становится готовой копией.
- Полный production live-аудит 10 августа повторно подтвердил 81/81 feed от 50 провайдеров без ошибок/усечения и согласованную реальную выдачу JSON/XML/TXT/CSV после validation.
- Все сторонние Docker build/runtime/service images, включая PostgreSQL jobs в CI, release и live source audit, закреплены на точных multi-architecture registry digest; протестированный supply-chain gate отклоняет mutable container references в Dockerfile, Compose и GitHub Actions.
- React-каталог использует keyset/cursor-пагинацию, позволяет дозагружать весь живой набор и сохраняет расширенный список при фоновом обновлении статистики.
- Стартовая cursor-страница кэшируется на API с request collapsing, а уникальные continuation-страницы не засоряют ограниченный output cache.
- Frontend Nginx запускается непривилегированным пользователем на порту 8080 с read-only root filesystem и без Linux capabilities.
- Prometheus заранее предупреждает, когда ETA validation-очереди превышает допустимое окно свежести публичного каталога.
- Transient HTTP-ответы proxy-feed освобождают соединение до retry backoff и не блокируют source connection pool во время ожидания.
- Telegram retry закрывает response, multipart и backup file handle до ожидания `retry_after`.
- Backup worker после ошибки повторяет полный цикл через 15 минут, а count/age retention не позволяет recovery-снимкам заполнить volume.
- Collector отклоняет HTTP 200 HTML/WAF/error страницы, даже если они содержат похожий на прокси `IP:port`.
- React test gate использует стабильный thread pool и fail-closed отклоняет запуск без обнаруженных тестов.
- SSRF test gate проверяет как URL-policy, так и финальный TCP connect для IPv4/IPv6 loopback до открытия socket.
- Публичные list/seek/export endpoint'ы возвращают 400 для числовых enum-значений неизвестного proxy protocol.
- Diagnostics, React и Prometheus публикуют каноническую дату последнего полного release-аудита каталога.
- Restore до изменения БД отклоняет ZIP-bomb, database entry крупнее 16 ГиБ и backup распакованным размером более 32 ГиБ.
- Restore валидирует semantic invariants каждой JSON-строки до её записи через PostgreSQL binary COPY.
- Семнадцать PostgreSQL CHECK constraints независимо защищают пять операционных таблиц; rollout использует `NOT VALID`/`VALIDATE CONSTRAINT` без длительной блокировки writes.
- Collector парсит параллельные feed'ы прямо в общий bounded-набор через компактные IP value-key без per-source materialized списков и удерживаемых строк.
- Источники сохраняют `ETag`/`Last-Modified` и используют conditional HTTP revalidation; подтверждённый `304` обновляет freshness без повторной загрузки и парсинга feed'а.

### Fixed

- Stats, admin diagnostics и Prometheus теперь считают `due` только среди доступных для claim строк и отдельно показывают активные leases; in-flight batch больше не завышает backlog/ETA и не создаёт ложный `ValidationBacklogAtRisk`, а lease с expiry ровно `now` согласована с claim ownership.
- Dead-retention больше не удаляет строку с активной validation lease посреди сетевой проверки и не превращает корректный batch в ownership failure; просроченная lease по-прежнему допускает очистку.
- Source CRUD теперь берёт cluster-wide shared advisory-lock, а collection — exclusive-lock того же ключа: прямой REST-клиент на любой реплике получает `409` вместо изменения enabled-каталога посреди snapshot и ложного `completed` полного аудита; параллельные CRUD по-прежнему разрешены.
- Backup-worker после занятого cluster-lock больше не засыпает на полный production-интервал: через bounded 15 минут он перечитывает persistent `completed` audit, восстанавливает остаток расписания после успеха peer или повторяет просроченный backup после его аварии.
- React admin-мutations теперь принадлежат точному поколению сессии, отменяются при logout, смене ключа, закрытии диалога и unmount; поздний ответ, игнорирующий `AbortSignal`, не может восстановить старый ключ или данные. Вход оформлен нативной формой и работает по Enter.
- React admin-dialog помечает весь фон нативным `inert` и возвращает программно утёкший focus внутрь modal при прямом и обратном Tab, сохраняя keyboard-trap даже при вмешательстве расширения или стороннего скрипта.
- Публичный proxy-каталог публикует screen-reader semantics `table/row/columnheader/cell`, loading/empty status и `aria-pressed` для protocol-фильтров без изменения визуального layout.
- React admin-console сериализует тяжёлые collect/validate/backup и source mutations единым busy-gate, поэтому состав feed'ов нельзя изменить посреди ручного полного аудита, а повторные клики не создают перекрывающиеся операции.
- Core Docker services больше не могут безгранично вытеснять хост: PostgreSQL/API/restore получают настраиваемые defaults 2 GiB/2 CPU, web/Caddy — 256 MiB/0.5 CPU; `.env.example` раскрывает tuning, а supply-chain gate fail-closed проверяет точные `mem_limit`/`cpus` expressions.
- Telegram trust boundary теперь sanitizes все escaping exceptions: caller cancellation сохраняет `OperationCanceledException`, локальный status rejection — HTTP status, но handler message/inner/Data с bot-token URI, chat ID или multipart никогда не попадают в backup audit, логи и следующий архив.
- Source feed semantic gate теперь отклоняет mislabelled HTML/WAF body, начинающийся с HTML comment, `meta`, `title` или `script`, даже если внутри есть похожий на прокси `IP:port`; mixed feeds отдельно фиксируют per-record HTTP/HTTPS/SOCKS4/SOCKS5 scheme и fallback.
- SOCKS transport теперь согласован с разрешёнными control-host настройками: публичные IPv4 кодируются нативно в SOCKS4/SOCKS5, IPv6 — через SOCKS5 `ATYP=4`, а DNS по-прежнему использует SOCKS4a/`ATYP=3`; не представимый в SOCKS4 IPv6 target становится `deferred`, а не ложным Dead.
- HTTP CONNECT handshake теперь читает bounded header блоками вместо allocation и async-read на каждый байт, строго требует HTTP/1.0 или 1.1, ASCII/control-safe headers и отсутствие неожиданных post-header bytes; SOCKS5 отклоняет запрещённый zero-length BND domain.
- Proxy-tunnel control response теперь сохраняет byte-oriented HTTP framing до dechunking, затем использует strict UTF-8 и безопасную проверку JSON shape: chunk-size корректно считается при Unicode и split code point, а повреждённые bytes, non-object root и нестроковый `ip` становятся deferred control failure.
- Прямой ответ validation control endpoint теперь разбирается из исходных UTF-8 bytes и требует object со строковым `ip`: невалидная кодировка или JSON shape fail-closed отклоняются вместо replacement characters либо необработанного server error.
- Validation worker больше не принимает непустой deferred-only пакет за пустую очередь: после временной ошибки control endpoint он продолжает осушать остальные due-записи с короткой паузой вместо 30-секундной остановки.
- Telegram backup sender теперь запрашивает `ResponseHeadersRead`: недоверенный Bot API body больше не буферизуется `HttpClient` целиком до 64-КиБ parser limit. Oversized streaming response bounded-читается, закрывается и никогда не подтверждает доставку; регрессия запрещает возврат к `ResponseContentRead`.
- Weekly validation-audit больше не принимает пустую публикацию или разные наборы одинакового размера: требуется хотя бы один Alive proxy и точное ordered URL equality JSON↔XML↔TXT↔CSV; artifact/summary публикуют SHA-256 набора, новый HTTP-mock contract отклоняет format mismatch и zero-Alive, а CI/release обязаны запускать этот contract.
- Forced source-audit теперь требует двустороннее доказательство текущего запуска: `StartedAt ≤ LastFetchedAt ≤ FinishedAt` для каждого enabled feed. Повреждённая или сохранённая дата из будущего больше не проходит freshness-проверку; JSON artifact публикует `futureEvidence`, а mock-contract воспроизводит отклонение.
- Export boundary query и потоковое тело теперь выполняются в одной PostgreSQL `REPEATABLE READ` транзакции: `X-Next-Cursor`/`X-Next-Offset`, `X-Export-Truncated` и JSON/XML/TXT/CSV больше не расходятся при concurrent validation update; race-регрессия изменяет статус первой строки строго между двумя SQL-командами.
- Validation persistence больше не считает частично отвергнутый lease batch успешным: owned-результаты атомарно сохраняются, строки с чужим UUID остаются нетронутыми, но несовпадение submitted/persisted fail-closed завершает audit как `failed`; PostgreSQL-регрессия доказывает обе стороны этого инварианта.
- Validation heartbeat после единичного transient-сбоя PostgreSQL больше не завершается навсегда: ошибка логируется, следующий период снова пытается продлить точный lease token, а детерминированный unit-тест доказывает retry без многоминутного ожидания.
- Финализация collection-run теперь проверяемо переводит только собственную строку `running → completed`, а error-path допускает только `running → failed`: параллельный administrative/restore результат не перезаписывается tracked EF update; PostgreSQL-регрессия меняет audit во время feed-запроса и доказывает fail-closed ответ при сохранении уже собранного кандидата.
- Финализация backup теперь атомарно переводит ровно свою audit-строку из `running` в `completed` и проверяет affected-row count: удалённая или параллельно изменённая запись приводит к fail-closed ошибке даже после успешной публикации и Telegram-доставки, а error-path не перезаписывает чужой `completed`/`failed`; PostgreSQL-регрессия воспроизводит потерю ownership во время внешнего вызова.
- Публичный `/health/ready`, который обращается к PostgreSQL и не кэширует отрицательные ответы, теперь использует общую per-IP public rate policy; `/health/live` остаётся независимым дешёвым liveness-сигналом, а container smoke доказывает bounded 429 JSON с `Retry-After` после исчерпания readiness budget.
- Cross-field validation интервалов collector больше не создаёт `TimeSpan` из ещё не проверенных значений: экстремальные `DeadRetryMaxHours`, `SourceFailureBackoffMaxHours` и `DeadRetentionDays` теперь детерминированно дают `OptionsValidationException` с полным списком ошибок вместо необработанного arithmetic exception на startup.
- `Collector__ProbeHost` теперь использует общий строгий DNS-label parser вместо широкой `Uri.CheckHostName`: underscore, terminal dot, пустые labels, дефис по краям, неоднозначный dotted numeric host и ненормализованные IP literals fail-fast отклоняются до запуска validation workers; production `AllowedHosts` повторно использует тот же DNS-контракт и требует canonical IP literals.
- Production больше не наследует `AllowedHosts="*"`: startup требует bounded explicit/scoped ASCII host allowlist, Compose связывает его с TLS `PUBLIC_HOST`, а PostgreSQL/API smoke и source audit задают loopback hosts явно; CI прямым запросом с поддельным Host доказывает ответ Kestrel 400.
- Admin source create/update и `SourceRequest` используют единый bounded non-throwing HTTPS parser до DNS и обращения к БД; malformed значение вроде `https://[` и Unicode URL, разрастающийся после normalization свыше DB-лимита, теперь стабильно возвращают 400 вместо необработанного исключения/500 и не изменяют существующий источник.
- Control endpoint теперь fail-fast требует canonical ASCII `ProbeHost` и уже escaped origin-form `ProbePath`: Unicode/space, network-path `//`, fragment и невалидные percent escapes больше не проходят startup и не превращают всю validation-очередь в массовые ошибки либо `Deferred`; публичные canonical IPv4/IPv6 остаются допустимыми.
- Telegram backup-конфигурация теперь fail-fast проверяет bounded path-safe ASCII token и совместимый с Alertmanager ненулевой signed 64-bit chat ID; при включённом scheduler доставка обязательна, URI/control-character ошибки и local-only misconfiguration больше не откладываются до первого production backup, а CI использует синтаксически валидный заведомо фиктивный token.
- PowerShell decryptor больше не имеет TOCTOU между проверкой и созданием `OutputZip`: plaintext пишется в уникальный sibling `.partial`, после `Flush(true)` публикуется атомарным move без overwrite, существующий файл сохраняется, partial очищается при отказе аутентификации, а ошибка его удаления больше не замалчивается.
- PowerShell decryptor поддерживает отдельный `-EncryptionKeyFile` parameter set, через единый descriptor читает абсолютный неизменившийся обычный файл не больше 16 КиБ, строго декодирует UTF-8/BOM, удаляет ровно один терминальный CRLF/LF и обнуляет raw/char/PBKDF2 buffers; README больше не помещает backup-ключ в command history/process arguments.
- Restore CLI прокидывает Ctrl+C/container SIGTERM во все decrypt/COPY/transaction операции, возвращает exit code 130 после rollback и удаления временного plaintext; новый `--encryption-key-file` читает абсолютный bounded UTF-8 secret без process-argument leakage и отвергает неоднозначную пару с inline-ключом.
- BackupWorker восстанавливает расписание из последнего persistent `completed`-аудита: restart/deploy и ручной backup больше не создают преждевременный повторный архив/Telegram-спам, overdue или отсутствующая копия запускается сразу, future timestamp bounded одним интервалом, длинное ожидание делится на суточные chunks, а временный сбой чтения БД повторяется через 15 минут без остановки host.
- Collector после bounded bulk-upsert будит ValidatorWorker через coalescing channel: новые кандидаты больше не ждут случайно до 30 секунд idle polling, повторные сигналы занимают ровно один slot, а cancellation shutdown не маскируется.
- Background collector сохраняет настроенный start-to-start cadence, но после overrun делает обязательный cooldown, после общего сбоя повторяет цикл через минуту, а при занятом cluster lock ждёт полный интервал; медленные feed'ы и несколько API-реплик больше не могут вызвать немедленный тяжёлый restart/retry-storm.
- Mobile React UI увеличивает logo/admin, protocol filters, latency slider, 50 source-link targets и все элементы admin dialog до 44 px без horizontal overflow; после входа keyboard focus переходит к первой admin-команде, а встроенные источники больше не показывают вводящее в заблуждение действие удаления. Stylesheet и component-контракты защищают accessibility в CI.
- Container smoke больше не пытается отправить тестовый backup в настоящий Telegram с фиктивным token: API пересоздаётся с пустыми Telegram secret-файлами, а ответ и `BackupRun` обязаны подтвердить локальный completed-архив.

## [1.0.0] - 2026-08-10

### Added

- Каталог из 81 HTTPS feed-endpoint от 50 канонически независимых провайдеров.
- Реальная проверка HTTP CONNECT, SOCKS4a и SOCKS5 с измерением задержки и успешности.
- Публичный React-интерфейс, JSON/XML/TXT/CSV exports и keyset pagination.
- PostgreSQL queue leases, горизонтальное масштабирование, metrics и production alerts.
- Потоковые зашифрованные PHB3 backup с Telegram-доставкой и транзакционным restore.
- Multi-architecture Docker-релизы с SBOM, provenance, digest manifest и GitHub attestation.

### Security

- SSRF/DNS-rebinding защита, bounded parsers, non-root read-only containers и Docker secrets.
- Fail-closed manifest v5 проверяет полную типизированную схему настроек без секретов.
