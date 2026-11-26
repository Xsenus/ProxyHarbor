# Документация ProxyHarbor

Начните с [README проекта](../README.md), чтобы посмотреть интерфейс, выполнить примеры API или запустить свой экземпляр. Этот каталог содержит подробные инструкции и описание устройства сервиса.

## Частые задачи

| Задача | Куда перейти |
|---|---|
| Отфильтровать прокси, выгрузить список или получить VPN-конфигурацию | [Справочник API](API.md) |
| Добавить источник, проверить импорт или заменить URL с HTTP 404 | [Подключение и проверка источников](SOURCES.md#подключение-и-проверка-источника) |
| Подключить новый VPS или отключить узел, который перестал отвечать | [Распределённая проверка](DISTRIBUTED_CHECKERS.md) |
| Проверить, что все части одного бэкапа дошли в Telegram | [Проверка доставки](BACKUP_RESTORE.md#проверка-доставки) |
| Развернуть сервис на своём домене или откатить обновление | [Развёртывание](DEPLOYMENT.md) |

## Пользователю сервиса

| Документ | Когда читать |
|---|---|
| [README проекта](../README.md) | Возможности, быстрый старт и основные команды |
| [PROJECT_STATUS.md](PROJECT_STATUS.md) | Карта возможностей и история их проверки |
| [API.md](API.md) | Маршруты, фильтры, pagination, форматы и ошибки |
| [SOURCE_CATALOG.md](SOURCE_CATALOG.md) | Каталог встроенных провайдеров и лент |
| [SOURCES.md](SOURCES.md) | Как собираются и аудируются внешние feed’ы |

## Оператору

| Документ | Назначение |
|---|---|
| [CONFIGURATION.md](CONFIGURATION.md) | Полный справочник environment/options/secrets |
| [DEPLOYMENT.md](DEPLOYMENT.md) | Production HTTPS, запуск, обновление и откат |
| [BACKUP_RESTORE.md](BACKUP_RESTORE.md) | PHB3, Telegram, inspection и disaster recovery |
| [TELEGRAM_BOT.md](TELEGRAM_BOT.md) | Commerce-бот, Stars, CRM, webhook/polling и эксплуатация |
| [MONITORING.md](MONITORING.md) | Метрики, alarms и incident response |
| [PERFORMANCE.md](PERFORMANCE.md) | Нагрузочная модель, измерения и tuning |
| [DISTRIBUTED_CHECKERS.md](DISTRIBUTED_CHECKERS.md) | Подключение VPS, leases, failover и безопасная эксплуатация checker-агентов |
| [RU_LEGAL_COMPLIANCE.md](RU_LEGAL_COMPLIANCE.md) | Техническая реализация и внешние обязательные действия для работы в РФ |
| [RU_HOSTING_AND_PUBLICATION.md](RU_HOSTING_AND_PUBLICATION.md) | Российское размещение, DDoS/WAF, миграция и регистрация в поисковых системах |

## Разработчику и сопровождающему

| Документ | Назначение |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Компоненты, data flow, concurrency и trust boundaries |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | Локальные gates и правила pull request |
| [SECURITY.md](../SECURITY.md) | Политика и технические границы безопасности |
| [GITHUB_SETUP.md](GITHUB_SETUP.md) | Первая публикация и настройки репозитория |
| [RELEASING.md](RELEASING.md) | SemVer release, GHCR, provenance и rollback |
| [CHANGELOG.md](../CHANGELOG.md) | История заметных изменений |

## Рекомендуемый порядок первого production-запуска

1. Прочитайте [SECURITY.md](../SECURITY.md), особенно риски публичных прокси и обращения с секретами.
2. Заполните `.env` по [CONFIGURATION.md](CONFIGURATION.md).
3. Выполните [DEPLOYMENT.md](DEPLOYMENT.md) на отдельном staging-host.
4. Проверьте readiness, OpenAPI, все четыре формата и admin authentication.
5. Выполните ручной backup, подтвердите Telegram delivery и восстановите архив в отдельную БД по [BACKUP_RESTORE.md](BACKUP_RESTORE.md).
6. Подключите monitoring profile и отработайте ключевые alarms по [MONITORING.md](MONITORING.md).
7. Только после успешного staging smoke переключайте DNS/traffic на production.

## Поддержание документации

- Любое заметное изменение добавляется в `CHANGELOG.md`.
- Новый public C# member обязан иметь XML-документацию; `CS1591` является build error production-проектов.
- Изменение environment/options одновременно обновляет `.env.example` и `CONFIGURATION.md`.
- Изменение route/response обновляет OpenAPI contracts и `API.md`.
- Изменение backup schema/restore обновляет `BACKUP_RESTORE.md` и integration round-trip.
- Изменение Docker/release обновляет `DEPLOYMENT.md`, `RELEASING.md` и соответствующие contract scripts.
- `Test-DocumentationLinks.ps1` должен проходить для всех внутренних ссылок.
