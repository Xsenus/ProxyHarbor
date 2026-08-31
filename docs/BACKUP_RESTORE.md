# Backup и восстановление

Backup ProxyHarbor — это переносимый зашифрованный `.phbackup`, а не копия PostgreSQL volume. Он предназначен для восстановления данных приложения и безопасной сверки настроек на другой установке.

## Что входит в архив

Manifest v7 содержит согласованный repeatable-read snapshot:

- `Proxies`, `Sources`, collection runs, validation runs и завершённые предыдущие backup runs;
- настройки Collector и Backup;
- безопасные runtime-настройки CORS, trusted networks, hosts и logging;
- счета со способом оплаты, подписки, одноразовые уведомления, аудит ручных продлений, агрегаты выдачи по IP и правила блокировки;
- конфигурацию commerce-бота, Telegram CRM, очередь доставки и обработанные update;
- внешние checker-узлы, их несекретные SSH-реквизиты, fingerprint, состояние lease и счётчики;
- UTC-время, версии manifest/settings schema и `secretsIncluded=false`.

В архив никогда не входят PostgreSQL connection string/password, admin password, admin API key, credentials Telegram/S3-доставки backup, data-protection keys или encryption key. Token commerce-бота сохраняется только как Data Protection ciphertext. Без независимо сохранённого volume ключей он после переноса не расшифруется, поэтому ключи и исходный token необходимо хранить во внешнем secret manager.

## Создание и доставка

Snapshot сериализуется в ZIP-поток и сразу шифруется в PHB3: plaintext ZIP не записывается в backup volume. Результат проверяется, атомарно публикуется и затем сначала загружается в настроенный S3-совместимый bucket. После `PUT` выполняется `HEAD`, сверяются размер и сохранённый SHA-256. Дополнительно архив может отправляться Telegram document; файлы крупнее настроенного лимита делятся максимум на 20 частей.

Production запуск требует backup key и хотя бы один внешний канал: S3-совместимое хранилище либо активного получателя из CRM основного Telegram-бота. Для больших архивов S3 является основным каналом. Endpoint обязан быть HTTPS; bucket должен быть непубличным, с versioning и по возможности Object Lock. Access/secret key защищаются ASP.NET Core Data Protection и никогда не возвращаются в браузер. Ошибка любого включённого канала завершает audit неуспешно, но уже созданный локальный шифротекст и подтверждение успешно доставленного канала сохраняются.

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
jq --exit-status '.manifest.version == 7 and .manifest.secretsIncluded == false' recovery-settings.json
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

После restore проверьте readiness, количество строк, несколько известных source/proxy/audit записей и создание нового backup новым экземпляром.

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

Restore выполняет migrations и транзакционный импорт proxy/audit-таблиц. Backup v6 добавляет аккаунты, роли и подписки, а v7 — внешний checker-каталог и связь validation-аудита с узлами. Архивы v2–v5 не содержат Identity snapshot и сохраняют текущие аккаунты целевой БД. SSH-пароли и agent-токены в backup не попадают: после переноса checker-узлы нужно переустановить из админки. Успешное сообщение появляется только после подтверждённого удаления временного plaintext. Если cleanup завершился ошибкой, считайте это инцидентом обращения с plaintext и удалите названный каталог вручную.

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
