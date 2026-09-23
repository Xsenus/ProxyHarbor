# Backup и восстановление

Backup ProxyHarbor — это переносимый зашифрованный `.phbackup`, а не копия PostgreSQL volume. Он предназначен для восстановления данных приложения и безопасной сверки настроек на другой установке.

## Что входит в архив

Manifest v9 содержит согласованный repeatable-read snapshot всех durable-таблиц EF-модели. Формат v8 остаётся замороженным и читается по своему исходному inventory; v9 дополнительно переносит destination/copy/job metadata:

- `Proxies`, `Sources`, защищённые credentials платных источников, collection runs, validation runs и завершённые предыдущие backup runs;
- настройки Collector и Backup;
- безопасные runtime-настройки CORS, trusted networks, hosts и logging;
- счета со способом оплаты, подписки, одноразовые уведомления, аудит ручных продлений, агрегаты выдачи по IP и правила блокировки;
- конфигурацию commerce-бота, Telegram CRM, очередь доставки и обработанные update;
- управляемые публичные реквизиты, публикацию разделов, cookie-тексты и идентификаторы аналитики;
- внешние checker-узлы, их несекретные SSH-реквизиты, fingerprint, состояние текущей партии и счётчики;
- Identity users/roles вместе с claims, внешними login и identity token records, персональными API-токенами (только SHA-256 hash) и аудитом запросов;
- реферальные связи/начисления и последний рассчитанный operational metrics snapshot;
- назначения и failure domains, политики pool, физические копии, delivery jobs и историю restore verification;
- UTC-время, версии manifest/settings schema и `secretsIncluded=false`.

В архив никогда не входят PostgreSQL connection string/password, admin password, admin API key, открытые API-секреты, credentials Telegram/S3-доставки backup, data-protection keys или encryption key. Token commerce-бота и ключ платного proxy-источника сохраняются только как Data Protection ciphertext. Персональные API-токены представлены только необратимым SHA-256 hash. Без независимо сохранённого volume Data Protection keys защищённые значения после переноса не расшифруются: восстановите тот же key ring либо повторно введите исходные secrets из внешнего secret manager.

`ProxyValidationLeases` — эфемерное operational ownership и в архив не входит. Legacy-архив может содержать прежние `CheckLeaseId/CheckLeaseUntil` внутри `Proxies`; restore проверяет целостность пары, но намеренно очищает её перед импортом. После запуска незавершённые проверки безопасно возвращаются в общую очередь, а durable `ValidationRuns` сохраняют историю и будут закрыты штатным recovery.

При старте legacy S3/Telegram-конфигурация идемпотентно проецируется в зарезервированные destination/pool rows под PostgreSQL advisory lock. Credentials повторно защищаются отдельным Data Protection purpose; plaintext не попадает в settings/capabilities. Эта проекция не создаёт `BackupCopies` или `BackupDeliveryJobs` и не меняет текущий delivery path, пока `BackupRouting__Enabled=false` (значение по умолчанию).

## Создание и доставка

Snapshot сериализуется в ZIP-поток и сразу шифруется в PHB3: plaintext ZIP не записывается в backup volume. Результат проверяется и атомарно публикуется. При выключенном routing сохраняется прежняя последовательная S3/Telegram-доставка. При включённом routing completed snapshot и отдельные destination copies/jobs фиксируются одной PostgreSQL-транзакцией, а bounded worker повторно использует те же immutable bytes. S3 становится `verified` только после `PUT`+`HEAD` с совпавшими размером/SHA-256; Telegram delivery без independent verify не удовлетворяет protection quorum.

Production запуск требует backup key и хотя бы один внешний канал: S3-совместимое хранилище либо активного получателя из CRM основного Telegram-бота. Для больших архивов S3 является основным каналом. Endpoint обязан быть HTTPS; bucket должен быть непубличным, с versioning и по возможности Object Lock. Access/secret key защищаются ASP.NET Core Data Protection и никогда не возвращаются в браузер. В legacy-режиме ошибка любого включённого канала завершает audit неуспешно. В routing-режиме отказ одного destination не блокирует jobs остальных; protection определяется verified independent copies, а не успехом всех каналов.

### Настройка S3-совместимого хранилища

1. Создайте отдельный приватный bucket в российском регионе второго провайдера, включите versioning и retention/Object Lock.
2. Создайте service account с минимальными правами `PutObject`, `HeadObject` и `GetObject` только на этот bucket/prefix. Чтение нужно для materialize и побайтной проверки подписанного sidecar; `DeleteObject` не требуется.
3. В `/admin/backups` включите S3, укажите HTTPS endpoint, region, bucket, prefix и пару access/secret key. Для Yandex Object Storage endpoint — `https://storage.yandexcloud.net`, регион подписи — `ru-central1`.
4. Создайте ручной backup и убедитесь, что в истории указано `доставлен: S3`; затем скачайте объект и выполните пробное восстановление в отдельную БД.
5. Храните PHB3 encryption key и S3 credentials в независимом secret manager. Потеря PHB3-ключа делает внешний объект невосстановимым.

Ручной запуск:

```bash
curl --fail --request POST \
  --header "X-Admin-Key: $ADMIN_KEY" \
  https://proxy.example.com/api/v1/admin/backup
```

По умолчанию destination routing выключен, поэтому ручной запуск сохраняет прежний `200`-контракт. После контролируемого включения `BackupRouting__Enabled=true` код ответа означает доказанную защиту: `200` только для `protected` или `degraded` (required quorum достигнут), `202` для durable `pending`, `503` для `unavailable`. Локальный staging не является внешней копией и сам по себе не делает backup защищённым. Поля `backupRunId`, `protectionState`, verified/required/desired и copy debt позволяют автоматизации отличить созданный ciphertext от подтверждённой внешней защиты.

Worker арендует due jobs через PostgreSQL `FOR UPDATE SKIP LOCKED`, выполняет одну потоковую delivery за раз, применяет bounded exponential backoff с jitter и ограничивает provider call меньшим из policy deadline и остатка lease. Истёкший `processing` lease переводится в `UNKNOWN/reconciling` без слепого повторного PUT. Отдельный worker-цикл арендует reconciliation job и проверяет детерминированный S3 locator через HEAD: совпавшие размер и SHA-256 дают `verified`, расхождение — `quarantined`, временная ошибка повторяет только HEAD, а отсутствие или неподдерживаемая проверка требуют `manual_review`. Telegram без независимой проверки никогда не получает `verified`. При S3 PUT отправляется `If-None-Match: *`; семантика конкретного S3-compatible provider должна подтверждаться отдельным canary, поэтому автоматический повтор PUT после UNKNOWN не разрешён даже при HEAD `NotFound`. Staging старше `BackupRouting__StagingTtlHours` больше не используется как локальный источник: worker может восстановить те же immutable bytes из разрешённой verified-копии в пределах lease/deadline. Если источника нет, PUT не выполняется. Переполнение `BackupRouting__MaximumStagingBytes` остаётся fail-closed. Routing остаётся выключенным по умолчанию.

При включённом routing catch-up просматривает immutable completed runs с текущей версией pool policy, verified-источником с разрешённым `read` и новым разрешённым PUT-route. Он создаёт не более одной недостающей copy/job за проход под PostgreSQL row lock; последние незакрытые jobs ограничивают темп. По умолчанию первыми идут недозащищённые новые runs, периодически просматриваются старые. Отдельный редкий проход может повторно запланировать одну `permanent_failed` copy только при durable-доказательстве отсутствия любого начатого PUT (`LastAttemptAt=null`, `AttemptCount=0`, нет locator/version/checksum/UNKNOWN и все предыдущие jobs имеют допустимый pre-PUT error). Требуются текущая policy, verified читаемый источник, разрешённый PUT-route, 15 минут после последней failed job; максимум две новые jobs на copy. Старые failed jobs остаются в аудите. UNKNOWN, любой начатый PUT и неоднозначный error не перезапускаются автоматически. Это частичный repair: recovery gate управляет приоритетом новых pending jobs, но не перезапускает уже failed PUT, не сериализует разные replicas и не восстанавливает историю копий без отдельного backfill. Для production необходимы canary provider, полный DR-дрилл и наблюдение запланированных циклов.

Отдельный recovery-probe worker работает только при `BackupRouting__Enabled=true` и не задерживает delivery worker. Он выбирает одну текущую verified S3-копию с разрешённым `verify`, выполняет только HEAD/metadata verification в пределах 20 секунд, затем фиксирует точный outcome; на replica запускается не чаще раза в минуту, а недавняя запись откладывает повтор для destination минимум на пять минут. Другие replicas могут одновременно сделать безопасный дублирующий HEAD. При подтверждённом отсутствии копия становится `missing`, при несовпадении — `quarantined`, и обе перестают учитываться как verified. Provider PUT/DELETE и автоматический repair такого объекта не выполняются. Совпадение metadata не доказывает полное чтение ciphertext, возможность нового PUT или здоровое окно для failback.

При включённом routing бюджет одной PUT-попытки не превышает оставшееся время от создания durable job до `OverallDeadlineSeconds` и равную долю для пригодных PUT-маршрутов pool. Это оставляет время следующему назначению, если preferred route завис. Отмена начавшегося PUT считается `UNKNOWN`, а не основанием для немедленной повторной загрузки. Breaker разделён по destination и операции; его короткие cooldown (1 минута для повторных временных ошибок, 5 минут для auth/quota) и half-open окно — предлагаемые значения для тестов, не подтверждённые production canary. Каждый начатый PUT сохраняет отдельный типизированный outcome в той же fenced transaction, что и исход delivery; непроверенный результат не считается успешным. Результаты attempted VERIFY probes сохраняются аналогично при reconciliation. Новая replica учитывает до трёх последних наблюдений за 20 минут; для старых PUT без outcome временно используется состояние durable job. Наблюдения старше восьми суток удаляются раз в час, кроме последнего unsafe-маркера на destination/операцию: длительный сбой не забывается по таймеру. При выборе due job primary с таким маркером уступает fallback того же run, пока после последнего сбоя не появятся успешный PUT и ряд точных `matching` VERIFY: от первого совпадения прошло не менее `FailbackHealthyForSeconds`, последнее не старше шести минут, промежутки между ними не превышают шести минут. `missing`, `mismatching`, inconclusive и legacy null сбрасывают это право. Это только приоритет новых jobs, не право слепого повтора UNKNOWN PUT. Краткоживущая таблица не входит в backup и очищается в транзакции restore; после восстановления health определяется новыми проверками. Непредпринятая/неподдерживаемая проверка не считается отказом provider. Отказ optional destination не меняет глобальную readiness и не расширяет pool allowlist.

Внутренний S3 adapter умеет читать существующий object key в новый локальный файл через private partial и проверяет размер и SHA-256 до атомарной публикации. Adapter требует точного совпадения locator с настроенным prefix и именем backup; route должен явно разрешать `read`. Draining route запрещает новый `put`, но сохраняет разрешённые `verify/read` для уже созданных copies. `BackupCopyMaterializer` выбирает только verified-копии с точным hash/size/policy snapshot, пробует разрешённые маршруты в пределах общего дедлайна, публикует конечный файл только после повторной проверки байтов и изолирует копию при доказанном расхождении. Подписанный каталог и выключенная по умолчанию автоматическая публикация доступны, но единый автоматический remote restore и real-provider drill пока не реализованы. Локальный `--input` restore не меняется.

Оператор может отдельно получить ciphertext через `dotnet run --project src/ProxyHarbor.Restore -- materialize --backup-run-id <uuid> --output <absolute-new-file.phbackup> --data-protection-keys-directory <isolated-key-ring-copy>`. Команда требует доступной БД с copy inventory и `ConnectionStrings__Postgres` (пароль можно дать через `SecretFiles__PostgresPassword`), читает только S3 copies с разрешённым `read`, не принимает inline secrets и не перезаписывает файл. Она не заменяет данные БД, но при доказанной порче источника может перевести copy в `quarantined`; локальный сбой candidate-файла сам по себе не карантинит удалённую копию. Используйте изолированную копию Data Protection key ring: автоматическое создание ключей отключено, но действующий production mount не нужен и не должен передаваться в локальный drill. Полученный PHB3 проходит проверку размера и SHA-256 ciphertext; для доказательства расшифровки и восстановления отдельно примените существующий `--input` в изолированной БД. Это ещё не офлайн-восстановление без production-БД и не real-provider drill.

Для независимого от БД inventory можно вручную экспортировать подписанный sidecar: `dotnet run --project src/ProxyHarbor.Restore -- catalog export --backup-run-id <uuid> --output <absolute-new.catalog.json> --key-file <absolute-backup-key-file>`. Экспорт требует текущей БД и включает только S3-копии в состоянии `verified`, с точным hash/size/policy, разрешённым `read` и locator, совпадающим с настроенным prefix. Подписывающий ключ выводится из backup-ключа отдельным PBKDF2-HMAC-SHA256 доменом (200 000 итераций, случайная соль на каждый sidecar); inline ключи/connection strings не принимаются. Сохраните sidecar вне production БД/VPS отдельно от файла ключа. Команда `catalog inspect --input <absolute.catalog.json> --key-file <absolute-backup-key-file>` проверяет схему и HMAC без БД и показывает backup ID, SHA-256, key reference, destination ID и object key каждого кандидата. Каталог не содержит endpoint, bucket, credentials и Data Protection ciphertext; `keyRef=legacy` пока лишь обозначение ключа, не доказательство его внешнего escrow. Подписанный каталог доказывает целостность inventory на момент экспорта, но не текущую доступность объектов. Полный restore drill остаётся незавершённым.

После потери production БД можно извлечь ciphertext по сохранённому sidecar: `dotnet run --project src/ProxyHarbor.Restore -- offline-materialize --catalog <absolute.catalog.json> --providers <absolute.providers.json> --key-file <absolute-backup-key-file> --output <absolute-new-file.phbackup>`. Файл `providers.json` содержит версию 1 и массив `destinations` с `destinationId`, HTTPS `endpoint`, `region`, `bucket`, `prefix`, `usePathStyle` и абсолютными путями `accessKeyFile`/`secretKeyFile`; точный образец доступен в `offline-materialize --help`. Inline credentials и неизвестные поля запрещены. Держите provider config и файлы ключей в отдельном защищённом escrow; секретные файлы не копируйте в sidecar. Команда проверяет подпись до provider I/O, связывает destination ID с точным object key, пробует копии по приоритету в общем дедлайне, повторно сверяет размер и SHA-256 локального файла и не перезаписывает output. БД и Data Protection key ring не используются, но реальная доступность S3 и актуальность escrow credentials должны быть проверены оператором; при повреждении offline-команда не может обновить DB quarantine. Для доказательства расшифровки и восстановления отдельно примените существующий `--input` в изолированной БД. Весь сценарий с реальным provider ещё не проверен.

Автоматическая публикация однокопийного sidecar для verified S3-копии по умолчанию выключена. Для контролируемого включения нужны `BackupRouting__Enabled=true`, `BackupCatalogSigning__Enabled=true`, отдельный `SecretFiles__BackupCatalogSigningKey` → `BackupCatalogSigning__SigningKey` и несекретный `BackupCatalogSigning__KeyReference` (по умолчанию `catalog-v1`). Ключ не должен совпадать с PHB3 encryption key; храните все его старые версии весь срок жизни подписанных каталогов. Worker берёт lease в PostgreSQL, публикует catalog рядом с ciphertext только при разрешённых `put` и `read` маршрутах и сохраняет `published` лишь после подтверждения точных байтов. После неопределённого PUT он повторяет те же байты: время экспорта фиксируется моментом verification, соль подписи выводится из copy ID отдельным HMAC-доменом. Collision/расхождение содержимого требуют ручного разбора; смена key reference у начатой публикации также переводит её в `manual_review` до provider I/O. Ручной export по-прежнему использует случайную соль. Текущие ручные `catalog export`/`inspect` принимают явно выбранный `--key-file`; для работы с отдельным signing key оператор использует один и тот же файл для обеих команд и последующего `offline-materialize`. Перед production-включением проведите canary на фактическом S3-compatible provider и полный offline restore drill; локальные тесты не доказывают семантику конкретного provider.

Для отдельного тестового S3 bucket есть ограниченный protocol canary `pwsh -File tools/Invoke-IsolatedS3Canary.ps1 -Bucket <test-bucket>` (без `-Execute` это dry-run без provider I/O). После `dotnet build ProxyHarbor.slnx -c Release` локально выполните `Get-Credential | Export-Clixml "$env:TEMP\proxyharbor-drill-credential.xml"`: введите S3 Access Key как имя пользователя и S3 Secret Key как пароль, не отправляя их повторно в чат. Запустите canary с `-CredentialPath "$env:TEMP\proxyharbor-drill-credential.xml" -ConfirmBucket <test-bucket> -Execute`, затем удалите этот временный файл. Он создаёт небольшой синтетический PHB3 и подписанный sidecar только под `proxyharbor-drill/`, проверяет PUT+HEAD, GET+PHB3 authentication, запрет повторного conditional PUT, публикацию catalog, GET и его подпись, затем удаляет только свои verified objects и подтверждает отсутствие через HEAD. При versioned bucket скрипт удаляет точные подтверждённые versions, если provider возвращает их ID; при отсутствии version ID может остаться скрытая версия. Если очистка не подтверждена, скрипт выводит точные object keys для ручной проверки. Это не полный DR-дрилл: исходная БД, реальный backup encryption key и Data Protection key ring здесь не восстанавливаются. Раскрытые в сообщениях ключи перевыпускайте после проверки.

### Восстановление ключей

PHB3 encryption key и Data Protection key ring — разные recovery-артефакты. Ключ PHB3 проверяйте на сохранённом архиве командой `dotnet run --project src/ProxyHarbor.Restore -- --input <absolute.phbackup> --encryption-key-file <absolute-secret-file> --inspect-settings`: она расшифровывает и валидирует архив без подключения к БД и не заменяет данные. Проверка одного архива не доказывает наличие всех старых версий ключа; сохраняйте отдельный key reference и проверяйте архивы каждой удерживаемой эпохи.

Для Data Protection заранее создайте синтетический marker на **изолированной копии** действующего key ring: `dotnet run --project src/ProxyHarbor.Restore -- dp-marker create --keys-directory <absolute-isolated-ring-copy> --output <absolute-new.marker.json>`. Сохраните marker вне VPS отдельно от копии ring. После переноса key ring на изолированную recovery-машину проверьте `dp-marker verify --keys-directory <absolute-restored-ring-copy> --input <absolute.marker.json>`. Команды не читают БД/provider и не создают новые DP keys; действующий production mount им не передавайте. Создавайте и удерживайте новый marker после каждой ротации: один marker доказывает расшифровку только тем ключом, которым он был создан. Успех marker не доказывает расшифровку всех реальных credentials — в полном drill отдельно проверьте representative Data Protection ciphertext и работоспособность восстановленных интеграций. Утрату или компрометацию старого ключа нельзя исправить повторным созданием marker задним числом.

Проверка полного production-контракта:

```powershell
./tools/Audit-Backup.ps1 `
  -ApiBaseUrl https://proxy.example.com `
  -AdminKey $env:ADMIN_API_KEY `
  -ReportPath artifacts/backup-audit.json
```

Audit требует непустой канонический PHB3, завершённую persisted audit row и `sentToTelegram=true`. `-AllowLocalOnly` применяйте только для явно выбранного локального canary, не для production acceptance.

## Telegram parts

Скачайте все документы одного backup и объедините их в исходный `.phbackup`:

```powershell
./tools/Join-BackupParts.ps1 `
  -InputDirectory C:\recovery\parts `
  -OutputPath C:\recovery\proxyharbor.phbackup
```

Скрипт проверяет имена, непрерывность набора и bounded part count. Не смешивайте части разных запусков.

## Проверка настроек без БД

```bash
docker compose --profile tools run --rm --no-deps -T restore \
  --input /app/backups/proxyharbor-YYYYMMDD-HHMMSS.phbackup \
  --inspect-settings > recovery-settings.json
jq --exit-status '.manifest.version == 9 and .manifest.secretsIncluded == false' recovery-settings.json
```

Этот JSON предназначен для операторской сверки. Настройки автоматически не применяются.

## Пробное восстановление

Каждый новый ключ, изменение backup-кода и production release следует проверять на отдельной БД. Укажите отдельную connection string и тот же ключ расшифрования. Для локального CLI безопаснее абсолютный `--encryption-key-file`; inline key остаётся виден в process arguments.

```powershell
$env:ConnectionStrings__Postgres='Host=localhost;Port=5432;Database=proxyharbor_restore_drill;Username=proxyharbor;Password=...'
dotnet run --project src/ProxyHarbor.Restore -- `
  --input C:\recovery\proxyharbor.phbackup `
  --encryption-key-file C:\secrets\backup-key `
  --replace-existing-data
```

После restore проверьте readiness, количество строк, несколько известных source/proxy/audit записей, отсутствие эфемерных proxy lease и создание нового backup новым экземпляром.

## Аварийная замена production-БД

Restore является destructive replacement. До него подтвердите целевой host/database и сохраните текущий архив отдельно. Затем остановите **все** API-реплики:

```bash
docker compose stop web api
docker compose --profile tools run --rm restore \
  --input /app/backups/proxyharbor-YYYYMMDD-HHMMSS.phbackup \
  --replace-existing-data
docker compose up -d api web
curl --fail https://proxy.example.com/health/ready
```

API удерживает shared PostgreSQL lifetime lease, restore требует exclusive lease. Поэтому забытая живая реплика блокирует замену данных. Во время restore новые API/worker write pipelines также не стартуют.

Restore выполняет migrations и транзакционный импорт. Backup v6 добавляет аккаунты, роли и подписки, v7 — внешний checker-каталог и связь validation-аудита с узлами, v8 — все ранее пропущенные durable-таблицы и S3-поля backup-аудита, v9 — destination/pool/copy/job/restore-verification state. Архивы v2–v5 не содержат Identity snapshot и сохраняют текущие аккаунты целевой БД; архивы v2–v8 продолжают приниматься по своим историческим контрактам. При восстановлении v8 и старше новые v9-таблицы целевой БД намеренно не заменяются. SSH-пароли и agent-токены в backup не попадают: после переноса checker-узлы нужно переустановить из админки. Успешное сообщение появляется только после подтверждённого удаления временного plaintext. Если cleanup завершился ошибкой, считайте это инцидентом обращения с plaintext и удалите названный каталог вручную.

Если ошибка произошла после возможного commit, сначала исследуйте целевую БД. Не повторяйте destructive restore вслепую.

## Ротация ключа

1. Убедитесь, что старый ключ доступен для всех архивов retention-периода.
2. Выполните restore drill последнего архива со старым ключом.
3. Замените runtime secret и перезапустите API.
4. Создайте backup вручную, проверьте Telegram delivery и restore новым ключом.
5. Храните старый ключ до истечения срока всех зашифрованных им архивов.

ProxyHarbor сохраняет чтение legacy PHB2 и ключей от 16 символов, но новые PHB3 создаются только с ключом от 32 символов.

## Retention и контроль

- `Backup:RetentionDays` управляет локальными опубликованными файлами.
- `Backup:HistoryRetentionDays` управляет строками аудита в БД.
- Telegram не заменяет независимую off-site копию, если доступ к боту и серверу контролируется одной учётной записью.
- Мониторьте время последнего успешного backup, размер, delivery status, свободный диск и ошибки cleanup.
- Минимум раз в квартал выполняйте restore drill; после изменения схемы, ключа или restore-кода — немедленно.

Развёртывание и порядок остановки сервисов дополнительно описаны в [DEPLOYMENT.md](DEPLOYMENT.md), метрики и alarms — в [MONITORING.md](MONITORING.md).
