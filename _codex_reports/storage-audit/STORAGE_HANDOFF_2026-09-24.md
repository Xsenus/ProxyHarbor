# Передача работ по надёжности хранения — 2026-09-24

## Короткий итог

Кодовая база `main@b5bfffc4bae47a766049a00dc19345dc89534dad` прошла полный CI для последнего изменения: PR #320, CI run `35950987029` (попытка 2). Новая архитектура backup/restore **реализована частично и не включена в production**. Нельзя считать систему готовой к аварийному восстановлению, пока реальный архив не восстановлен вне VPS с независимо сохранёнными ключами и не приняты RPO/RTO. Эта передача завершает текущий этап разработки, а не acceptance STG-07.

Показатель «7%» не вычисляется этим roadmap и не является мерой написанного кода. По грубой карте этапов STG-00–02 завершены, STG-03–05 частичны, STG-06–07 не закрыты. Строгий процент production-готовности не следует выводить из числа этапов: обязательные DR/rollout gates ещё открыты. [Подробный roadmap](STORAGE_IMPLEMENTATION_ROADMAP.md) и [критерии приёмки](STORAGE_IMPLEMENTATION_TASK.md) остаются источником истины.

## Что реализовано и чем подтверждено

| Область | Состояние | Доказательство и ограничение |
|---|---|---|
| Формат PHB3 и restore | v8 покрывает классифицированные durable-таблицы; v9 добавляет destination/copy/job; legacy readers сохранены | PostgreSQL round-trip и CI; это не проверка существующих production v7 архивов с реальными ключами. |
| Назначения и доставка | Additive schema, S3/Telegram adapters, durable jobs, quorum/debt, ограниченный fallback и UNKNOWN reconciliation | Unit/integration/CI; новый routing выключен по умолчанию. Нет доказательства устойчивости всех провайдерных отказов в production. |
| Repair/failback | Verified-copy READ, bounded catch-up и pre-PUT rearm, durable PUT/VERIFY outcomes, health-window приоритет; две смены drain/reactivation после claim проверены | EVID-100 и PR #320: один итоговый PUT; полный restart/flapping/backfill matrix и real-provider failback не закрыты. |
| Независимый catalog/restore | Подписанный offline catalog, sidecar publisher (выключен по умолчанию), materialize из verified copy, изолированные синтетические restore/fault tests | Не доказано восстановление реального архива без production-БД и VPS, равно как независимый escrow ключей. |
| Admin и наблюдаемость | Просмотр копий/маршрутов, регистрация S3, custom pool, drain/reactivation; quorum/debt, destination outcomes, staging alerts | UI/backend/CI; ввод destination не равен успешному provider canary или включению routing. |
| Тестовый S3 | HOSTKEY NL protocol canary: синтетический PHB3 и signed catalog PUT/HEAD/GET/verify/адресное удаление | [Отчёт canary](PROVIDER_CANARY_2026-09-24.md). Не было production-архива, второй независимой площадки или полного DR. |

Локально для последнего PR: Release build без предупреждений, PostgreSQL-backed backend 1673/1673, `dotnet format --verify-no-changes`, backup/changelog/S3-canary contracts прошли. CI PR #320 прошёл `verify` (включая backend, frontend, container smoke и security/doc gates), `postgres-integration`, C#/JS analysis и CodeQL. Первый `verify` был сорван `Connection reset by peer` в независимом платёжном HTTP-контракте; его локальный повтор и полный CI attempt 2 прошли без изменения тестов. Это проверяет регрессии кода, но не доказывает доступность production-инфраструктуры.

## Фактический production-срез и запреты

Последняя read-only сверка VPS (EVID-091, 2026-09-24): checkout `981c1ca02` с writer PHB3 v7, пять контейнеров работали. Семь последних завершённых backup совпадали с локальными файлами по имени и размеру; это **не** проверка ciphertext/restore. Они были отмечены как Telegram-delivered, S3 не был настроен. В БД обнаружены 18 строк в durable-таблицах, не входящих в v7. Последний обнаруженный predeploy dump датирован 2026-09-21. На VPS ничего не менялось.

Нельзя автоматически переключать routing, удалять legacy копии/объекты, запускать production restore, переносить Data Protection key ring или считать старые v7 архивы полными. Синтетическая миграция от развернутой схемы прошла (EVID-092), но не заменяет свежий predeploy dump и разрешённое обновление с rollback point. Реквизиты тестового S3 однажды были раскрыты в переписке: **перевыпустить access/secret key и не повторять их в чате**. Старый ключ для дальнейших действий не использовать. Тестовый bucket — один ресурс, не второй независимый failure domain.

## Что осталось — безопасная очередность

1. **Владелец/оператор:** подтвердить RPO, RTO, retention, стоимость и размещение; предоставить второй независимый destination или явно принять риск одной площадки; перевыпустить раскрытый S3 ключ и подготовить безопасную передачу нового. Зафиксировать независимый escrow PHB3 encryption key, Data Protection key ring и signed catalog, не в той же БД/VPS.
2. **Перед production upgrade:** согласовать окно и отдельное разрешение; получить новый полный predeploy PostgreSQL dump, проверить его восстановление на disposable target, сохранить точку отката и точную версию образов. Не считать существующий v7 backup достаточным rollback point.
3. **Flag-off upgrade:** развернуть additive migrations и новый writer без включения destination routing; проверить readiness, legacy backup/restore, пользовательские функции и диагностические метрики. Создать новый полный PHB3 v9 и подтвердить manifest/шифрование/состав таблиц. Утверждение о production исправлении возможно только после этого шага.
4. **Изолированный DR-дрилл:** с отдельной PostgreSQL и независимо полученными архивом, catalog и ключами выполнить [runbook](../../docs/ISOLATED_DR_DRILL.md), включая expected host/port/database guard, offline materialize, restore, sentinel/логин/credential проверки, RPO/RTO и безопасную очистку. Передачу production-данных и ключей согласовать отдельно. Синтетический S3 canary не закрывает этот пункт.
5. **После успешного DR:** провести provider failure/permission/quota/collision canary, инвентаризацию и dry-run исторических копий, bounded backfill, полный restart/flapping/failback matrix. B-only copies должны оставаться читаемыми, UNKNOWN не переигрывать вслепую. Не удалять старые копии.
6. **Отдельный routing cutover:** принять policy и бюджет, включить маршрут с canary и rollback/roll-forward планом; наблюдать не менее двух scheduled backup cycles и реальные protected/quorum/alert показатели. Затем закрыть AC-001–014, риски и STG-07 подписью владельца. Нужен отдельный approval; текущая передача его не даёт.

Локально можно продолжить TASK-033/034/040–052 и тесты без доступа к production, но это не должно опережать пункты 2–4 как основание для cutover. Детализация по карточкам: [roadmap](STORAGE_IMPLEMENTATION_ROADMAP.md), [evidence](STORAGE_EVIDENCE.md), [risk register](STORAGE_RISK_REGISTER.md), [backup/restore](../../docs/BACKUP_RESTORE.md), [deployment](../../docs/DEPLOYMENT.md).

## Точка возобновления

1. Начать с чистого `main` после PR #320; проверить `git status`, head, незакрытые PR и свежий CI. Не повторять уже пройденный полный набор без изменения входных файлов.
2. Сверить production только read-only заново: версия checkout/images, последние backup, состояние контейнеров/логов и наличие predeploy dump. Срез выше устаревает со временем.
3. Получить конкретные решения владельца по пунктам 1–4. Без них TASK-062/063 остаются blocked. Для локального продолжения выбирать отдельный bounded TASK и фиксировать PR, тесты и EVID-ID, не повышая статус real DR на основании mocks.
4. Перед любым push/PR/deployment выполнить профильный gate из `AGENTS.md`/`CONTRIBUTING.md`; CI обязателен. Если он упал, разбирать точный шаг, не обходить его.

Здесь намеренно нет секретов, production-команд на запись или заявления о полной отказоустойчивости.
