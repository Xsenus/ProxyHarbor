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
2. Создайте service account с минимальными правами `PutObject` и `HeadObject` только на этот bucket/prefix.
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

Worker арендует due jobs через PostgreSQL `FOR UPDATE SKIP LOCKED`, выполняет одну потоковую delivery за раз, применяет bounded exponential backoff с jitter и ограничивает provider call меньшим из policy deadline и остатка lease. Истёкший `processing` lease переводится в `UNKNOWN/reconciling` без слепого повторного PUT. Отдельный worker-цикл арендует reconciliation job и проверяет детерминированный S3 locator через HEAD: совпавшие размер и SHA-256 дают `verified`, расхождение — `quarantined`, временная ошибка повторяет только HEAD, а отсутствие или неподдерживаемая проверка требуют `manual_review`. Telegram без независимой проверки никогда не получает `verified`. При S3 PUT отправляется `If-None-Match: *`; семантика конкретного S3-compatible provider должна подтверждаться отдельным canary, поэтому автоматический повтор PUT после UNKNOWN не разрешён даже при HEAD `NotFound`. Staging старше `BackupRouting__StagingTtlHours` и переполнение `BackupRouting__MaximumStagingBytes` завершаются fail-closed. Routing остаётся выключенным по умолчанию.

При включённом routing бюджет одной PUT-попытки не превышает оставшееся время от создания durable job до `OverallDeadlineSeconds` и равную долю для пригодных PUT-маршрутов pool. Это оставляет время следующему назначению, если preferred route завис. Отмена начавшегося PUT считается `UNKNOWN`, а не основанием для немедленной повторной загрузки. Локальный breaker разделён по destination и операции; его короткие cooldown (1 минута для повторных временных ошибок, 5 минут для auth/quota) и half-open окно — предлагаемые значения для тестов, не подтверждённые production canary. Недавние PUT outcomes читаются из durable jobs новой replica; VERIFY пока имеет только локальную короткоживущую историю. Отказ optional destination не меняет глобальную readiness и не расширяет pool allowlist.

Внутренний S3 adapter умеет читать существующий object key в новый локальный файл через private partial и проверяет размер и SHA-256 до атомарной публикации. Adapter требует точного совпадения locator с настроенным prefix и именем backup; route должен явно разрешать `read`. Draining route запрещает новый `put`, но сохраняет разрешённые `verify/read` для уже созданных copies. Это только низкоуровневый контракт: автоматический выбор verified-копии, failover, каталог вне production БД и пользовательский remote restore пока не реализованы. Локальный `--input` restore не меняется.

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
