# Changelog

Все заметные изменения ProxyHarbor документируются в этом файле. Формат основан на
[Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версии следуют Semantic Versioning.

## [Unreleased]

### Changed

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
