# Изолированный DR-дрилл backup

Этот runbook проверяет восстановление ProxyHarbor после потери production-БД и VPS. Успешный protocol canary S3 сам по себе **не** является полным DR-дриллом: он использует синтетический PHB3, а не сохранённый архив приложения, PHB3 key escrow или Data Protection key ring.

## Предусловия и границы

- Владелец заранее выбирает отдельные тестовые S3 bucket/prefix и PostgreSQL-БД, подтверждает допустимые записи/удаления и способ безопасной передачи ключей. Не используйте production-БД или действующий Data Protection mount как цель дрилла.
- Изолированное приложение не должно отправлять платежи, клиентские сообщения, Telegram-уведомления или запускать production collectors/workers. Исходящий доступ разрешается только к выбранному тестовому provider; перед API-smoke проверьте эту изоляцию.
- Зафиксируйте revision приложения, BackupRunId, время завершения snapshot, SHA-256 и размер ciphertext, policy required/desired, идентификаторы failure domains без credentials. Отдельно согласуйте RPO/RTO: значения 24 ч / 4 ч в техническом задании пока только `PROPOSED`.
- До начала проверьте manifest version исходного **реального** архива: production checkout на момент EVID-091 содержит writer v7, а внутренние manifest имеющихся файлов ещё не инспектировались. Подтверждённый v7-архив не может дать `PASS` для полного DR. Для перехода требуется отдельно одобренный flag-off deploy нового writer, свежий v9 archive и независимая проверка его восстановления; synthetic S3 canary не повышает старый v7 до полного backup ([storage evidence](../_codex_reports/storage-audit/STORAGE_EVIDENCE.md)).
- В независимом escrow уже должны быть PHB3 decrypt key нужной эпохи, подписанный catalog, `providers.json` с разрешёнными destination IDs, файлы provider credentials, изолированная копия Data Protection key ring и созданный **до аварии** DP marker. Если любого компонента нет, остановитесь и зафиксируйте `NOT VERIFIED`, не подменяйте его новым ключом или пустой БД.
- Секреты держите вне репозитория, отчёта, command arguments и логов. `providers.json` содержит только абсолютные пути к приватным файлам access/secret key. Не публикуйте сам `providers.json`, если его пути раскрывают внутреннюю инфраструктуру.

## 1. Protocol canary тестового S3

Сначала выполните `dotnet build ProxyHarbor.slnx -c Release`, затем:

```powershell
pwsh -File tools/Invoke-IsolatedS3Canary.ps1 `
  -Bucket <test-bucket> -ConfirmBucket <test-bucket> `
  -PromptForCredential -Execute
```

В защищённом запросе S3 Access Key вводится как имя пользователя, Secret Key — как пароль. Canary работает только с HOSTKEY NL endpoint/region и случайными объектами под `proxyharbor-drill/`; он проверяет PUT/HEAD/GET, запрет повторного conditional PUT и подписанный sidecar. Убедитесь, что итог сообщает об адресном удалении обоих собственных объектов. На versioned bucket отдельно подтвердите отсутствие сохранённой версии, если provider не вернул VersionId. Ошибка cleanup — инцидент, а не успешный canary. Реальные архивы и production credentials здесь не используются.

## 2. Подготовка офлайн-восстановления

Пока исходная БД доступна, сохраните подписанный catalog в независимом escrow. Для ручного экспорта:

```powershell
dotnet run --project src/ProxyHarbor.Restore -- catalog export `
  --backup-run-id <uuid> --output <absolute-new.catalog.json> `
  --key-file <absolute-private-signing-key-file>
```

Если используется автоматически опубликованный sidecar, извлеките и сохраните именно его и соответствующую версию signing key. Ручной export требует исходную БД; после её потери создать такой catalog задним числом нельзя. Держите catalog отдельно от ключа и конфигурации provider. До имитации аварии сохраните точный `providers.json` и файлы credentials вне VPS. Убедитесь, что как минимум одна catalog copy действительно имеет разрешённый read-route; два объекта в одном failure domain не считаются двумя независимыми копиями.

Далее отключите доступ recovery-среды к исходной БД/VPS и проверьте catalog без неё:

```powershell
dotnet run --project src/ProxyHarbor.Restore -- catalog inspect `
  --input <absolute.catalog.json> --key-file <absolute-private-signing-key-file>
dotnet run --project src/ProxyHarbor.Restore -- offline-materialize `
  --catalog <absolute.catalog.json> --providers <absolute.providers.json> `
  --key-file <absolute-private-signing-key-file> `
  --output <absolute-new-recovered.phbackup>
```

`offline-materialize` не подключается к БД и не использует Data Protection keys. Он проверяет HMAC каталога, привязку destination/object key, размер и SHA-256 полученного ciphertext, пробует только разрешённые кандидаты и не перезаписывает output. Зафиксируйте, какая независимая копия сработала; если A недоступна, отдельно проверьте разрешённый B без изменения исходных объектов. Не удаляйте исходные копии при сбое. Сверьте SHA-256 полученного PHB3 с зафиксированным до дрилла значением.

## 3. Ключи и изолированная БД

Проверьте расшифрование PHB3 без БД и перенос Data Protection key ring из escrow:

```powershell
dotnet run --project src/ProxyHarbor.Restore -- `
  --input <absolute-recovered.phbackup> `
  --encryption-key-file <absolute-private-phb3-key-file> --inspect-settings
dotnet run --project src/ProxyHarbor.Restore -- dp-marker verify `
  --keys-directory <absolute-restored-isolated-ring-copy> `
  --input <absolute-preexisting.marker.json>
```

Marker подтверждает только конкретную эпоху DP key. После восстановления дополнительно проверьте расшифрование representative сохранённых credentials через изолированное приложение; успешный синтетический marker не доказывает их восстановимость.

Создайте **новую пустую** PostgreSQL-БД на отдельном хосте/инстансе. Перед destructive restore независимо сверяйте host, port и database name с согласованной изолированной целью; не используйте production connection string и не подставляйте секретный пароль в командную строку. Передайте `ConnectionStrings__Postgres` и при необходимости `SecretFiles__PostgresPassword` только изолированному процессу, остановите все API-реплики этой среды и выполните:

```powershell
dotnet run --project src/ProxyHarbor.Restore -- `
  --input <absolute-recovered.phbackup> `
  --encryption-key-file <absolute-private-phb3-key-file> `
  --replace-existing-data
```

Не повторяйте restore вслепую после неоднозначной ошибки: сначала проверьте состояние целевой БД и возможный commit. Если cleanup временного plaintext не подтверждён, остановите дрилл и обработайте это как инцидент с чувствительными данными.

## 4. Acceptance и измерение

На изолированном приложении подтвердите readiness и заранее записанные sentinel/invariant: ожидаемые количества и выбранные записи durable-таблиц, Identity login и hash-проверку API token, подписку/баланс без финансовой операции, отсутствие эфемерных validation leases, доступность backup history и representative Data Protection ciphertext. Не запускайте реальные платежи или уведомления. Затем создайте новый тестовый backup и дождитесь обязательного числа **независимых verified внешних** копий; локальный файл и Telegram без независимой проверки не удовлетворяют quorum.

В несекретном отчёте укажите:

| Поле | Что записать |
|---|---|
| Revision, BackupRunId, policy | Непривилегированные идентификаторы и required/desired copies |
| Snapshot finished → verified copy | Фактический RPO и источник времени из audit/provider |
| Начало retrieval → readiness + sentinels | Фактический RTO, отдельно retrieval и restore |
| Catalog/PHB3/DP/credentials | `PASS`, `FAIL` или `NOT VERIFIED` по каждому независимому артефакту |
| Copy failover, checksum, new backup | Проверенный destination/failure domain и результат |
| Cleanup | Подтверждённая адресная очистка тестовых объектов/БД или открытый инцидент |
| Residual risks и решение владельца | Принятые RPO/RTO, HA/cold-tier риски, разрешение rollout либо отказ |

`PASS` допустим только если все проверки выполнены на настоящем provider и изолированной БД, секреты восстановлены из независимого escrow, а cleanup подтверждён. При любом `FAIL`/`NOT VERIFIED` production rollout остаётся закрыт. Удаление тестовой БД/объектов выполняйте отдельным одобренным операторским шагом по точному списку целей; команды удаления не совмещайте с restore.

Связанные детали: [BACKUP_RESTORE.md](BACKUP_RESTORE.md), [MONITORING.md](MONITORING.md), [STORAGE_IMPLEMENTATION_ROADMAP.md](../_codex_reports/storage-audit/STORAGE_IMPLEMENTATION_ROADMAP.md).
