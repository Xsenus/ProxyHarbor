# Changelog

Все заметные изменения ProxyHarbor документируются в этом файле. Формат основан на
[Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версии следуют Semantic Versioning.

## [Unreleased]

### Changed

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
- Пятнадцать PostgreSQL CHECK constraints независимо защищают пять операционных таблиц; rollout использует повторяемый `NOT VALID`/`VALIDATE CONSTRAINT` без длительной блокировки writes.
- Collector парсит параллельные feed'ы прямо в общий bounded-набор через компактные IP value-key без per-source materialized списков и удерживаемых строк.
- Источники сохраняют `ETag`/`Last-Modified` и используют conditional HTTP revalidation; подтверждённый `304` обновляет freshness без повторной загрузки и парсинга feed'а.

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
