# Что реализовано в ProxyHarbor

Документ фиксирует текущее содержимое продукта перед первой публичной публикацией. История отдельных изменений ведётся в `CHANGELOG.md`.

## Продуктовый контур

| Область | Реализовано |
|---|---|
| Источники | 310 встроенных proxy-feed от 80 providers и 149 VPN-feed от 23 providers, seed/synchronization и admin CRUD пользовательских feed |
| Сбор | Bounded parallel downloads, retry/backoff, conditional HTTP, лимиты размера/полноты, parser и глобальная дедупликация |
| Хранение | PostgreSQL, EF Core migrations, constraints, индексы, COPY-ingestion и audit runs |
| Проверка | HTTP CONNECT, SOCKS4a и SOCKS5 tunnel, TLS validation, exit IP, latency, success rate и Alive/Dead/Deferred evidence |
| Масштабирование | Distributed validation leases, `SKIP LOCKED`, cluster operation locks, orphan recovery и API lifetime lease |
| Публикация | REST list/seek/stats/sources, streaming JSON/XML/TXT/CSV, OpenAPI, rate limits и bounded cache |
| Интерфейс | React/TypeScript dashboard, серверная пагинация, фильтры и экспорт; общая регистрация/login/recovery, личный профиль и адаптивная admin-панель с RBAC |
| Операции | Liveness/readiness, Prometheus, Alertmanager, Telegram alerts, diagnostics и maintenance |
| Backup | Repeatable-read snapshot proxy/audit/Identity/subscription tables и безопасных настроек, diskless PHB3, Telegram multipart delivery и audit |
| Restore | Offline settings inspection, archive/semantic validation, migrations, transactional replacement и rollback |
| Поставка | Hardened Docker/Compose, production Caddy HTTPS, GHCR multi-architecture workflow, SBOM/provenance/attestation |
| Качество | Backend/frontend tests, coverage floors, format/lint/build, CodeQL, dependency audit, Gitleaks и contract scripts |

## Текущие проверенные свидетельства

- Каталог release: 310 proxy-feed/80 providers и 149 VPN-feed/23 providers; все endpoint проверены live 28.08.2026, полный сетевой CI-аудит обязателен перед production.
- Collection audit: 888 116 разобранных строк и 290 217 уникальных кандидатов за 4,965 секунды.
- Validation sample: 1 600/1 600 объективных результатов без `Deferred`; Alive-множество совпало во всех export formats.
- Backend: 1042 tests; обязательные unit- и PostgreSQL coverage-gates выполняются в CI и release workflow.
- Frontend: 58 tests, ESLint, TypeScript/Vite production build и axe-core accessibility checks.
- Release build: warnings-as-errors, XML documentation и OpenAPI contracts.
- Полная Git history проверялась Gitleaks; CI повторяет scan с закреплённым scanner archive hash.

Точная дата и hash audit-множества находятся в [SOURCE_CATALOG.md](SOURCE_CATALOG.md). Бесплатные feed и прокси нестабильны: числа являются воспроизводимым снимком, а не обещанием постоянного результата.

## Что происходит после старта

1. API читает секреты, валидирует настройки, применяет migrations и синхронизирует built-in sources.
2. Collector получает feed, нормализует public IP/port/protocol и сохраняет новые candidates.
3. Validator арендует партии, строит настоящий proxy tunnel и сохраняет объективное evidence.
4. Public API выбирает только свежие Alive rows; React использует те же endpoint.
5. Maintenance удаляет устаревшие Dead/Pending и audit history по retention.
6. Backup worker создаёт PHB3 по расписанию и требует Telegram delivery.
7. Readiness, metrics и alerts отражают БД, workers, source freshness, validation и backup.

## Границы первой версии

- Агрегируются публично доступные feed; приватные/платные API с credentials не включены.
- Сервис не гарантирует безопасность, законность или стабильность чужого proxy endpoint.
- Геолокация и ASN не заявлены как authoritative характеристики.
- Бесплатный API не содержит аккаунтов, биллинга, персональных quotas или commercial SLA.
- Production acceptance требует реального Docker smoke, Telegram backup и restore drill на инфраструктуре владельца.
- Repository settings, DNS, GitHub owner, production secrets и remote выполняются владельцем по [GITHUB_SETUP.md](GITHUB_SETUP.md).

## Следующий этап

После первой публикации совместимыми итерациями можно добавить пользовательские API keys/quotas, distributed cache, отдельные worker deployments, лицензированные геоданные, historical scoring, commercial plans и status page. Эти функции намеренно не объявлены реализованными сейчас.
