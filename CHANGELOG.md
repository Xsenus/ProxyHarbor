# Changelog

Все заметные изменения ProxyHarbor документируются в этом файле. Формат основан на
[Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версии следуют Semantic Versioning.

## [Unreleased]

### Changed

- Следующие изменения продукта добавляются сюда до выбора новой версии.

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
