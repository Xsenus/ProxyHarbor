# Backup и восстановление

Backup ProxyHarbor — это переносимый зашифрованный `.phbackup`, а не копия PostgreSQL volume. Он предназначен для восстановления данных приложения и безопасной сверки настроек на другой установке.

## Что входит в архив

Manifest v5 содержит согласованный repeatable-read snapshot:

- `Proxies`, `Sources`, collection runs, validation runs и завершённые предыдущие backup runs;
- настройки Collector и Backup;
- безопасные runtime-настройки CORS, trusted networks, hosts и logging;
- UTC-время, версии manifest/settings schema и `secretsIncluded=false`.

В архив никогда не входят PostgreSQL connection string/password, admin password, admin API key, Telegram credentials, data-protection keys или encryption key. Эти секреты необходимо независимо хранить во внешнем secret manager.

## Создание и доставка

Snapshot сериализуется в ZIP-поток и сразу шифруется в PHB3: plaintext ZIP не записывается в backup volume. Результат проверяется, атомарно публикуется и затем отправляется Telegram document. Файлы крупнее настроенного лимита делятся максимум на 20 частей; части имеют общий идентификатор и могут быть объединены скриптом.

Production запуск требует backup key и подтверждённую Telegram-конфигурацию. Ошибка доставки завершает audit неуспешно, но уже созданный локальный шифротекст сохраняется согласно retention policy.

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
jq --exit-status '.manifest.version == 5 and .manifest.secretsIncluded == false' recovery-settings.json
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

Restore выполняет migrations и импорт пяти таблиц транзакционно, проверяет структуру и инварианты snapshot. Успешное сообщение появляется только после подтверждённого удаления временного plaintext. Если cleanup завершился ошибкой, считайте это инцидентом обращения с plaintext и удалите названный каталог вручную.

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
