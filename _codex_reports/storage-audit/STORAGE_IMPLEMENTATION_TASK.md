# Техническое задание: надёжное хранение и восстановление ProxyHarbor

Версия: 1.5, 2026-09-22. Статус: `IMPLEMENTATION IN PROGRESS`; STG-00/01 и TASK-020–024 прошли CI и слиты в `main`, маршрутизация остаётся выключенной по умолчанию. Следующий локальный этап — TASK-030 protection evaluator. Evidence: [STORAGE_EVIDENCE.md](STORAGE_EVIDENCE.md), риски: [STORAGE_RISK_REGISTER.md](STORAGE_RISK_REGISTER.md), порядок: [STORAGE_IMPLEMENTATION_ROADMAP.md](STORAGE_IMPLEMENTATION_ROADMAP.md).

## 1. Scope и требования

Цель — сделать текущий PHB3 backup действительно полным, затем обеспечить несколько разрешённых destinations, честный per-copy audit, автоматическое переключение доставки/получения backup, восстановление пропущенных копий и безопасный failback. В проекте нет durable пользовательских runtime files (EVID-020); поэтому scope не включает создание универсальной файловой платформы ради гипотетических объектов.

| ID | Требование |
|---|---|
| REQ-001 | Каждый durable DB entity должен быть явно классифицирован как `included`, `ephemeral` или `external-secret/reprovision`; неизвестная таблица должна ломать contract test. Manifest v8 обязан включать все включённые данные. |
| REQ-002 | Restore v8 должен транзакционно заменить и проверить весь включённый набор, сохранить backward read v2–v7 и все поля audit; destructive restore запускается только после preflight и exclusive lease. |
| REQ-003 | Поддержать N backup destinations через registry и operation-specific pool; legacy single S3/Telegram configuration продолжает работать. |
| REQ-004 | Разделить `snapshot created`, `copy verified`, `protection satisfied`, `desired redundancy pending`. HTTP/worker не должны сообщать false success. |
| REQ-005 | При отказе preferred destination новый годный PHB3 автоматически пишется в следующий разрешённый и capability-compatible destination; ошибка source/snapshot не маскируется fallback. |
| REQ-006 | Состояние каждой копии и delivery job durable: idempotency, retry budget, `UNKNOWN`, reconciliation, checksum, generation/version/native locator и last error. |
| REQ-007 | Restore/read path получает verified copy по locator без production DB, проверяет bytes/hash/PHB3 и пробует только разрешённые альтернативы; stale/corrupt copy не возвращается. |
| REQ-008 | PHB3 keys, Data Protection key ring и provider credentials имеют независимый recovery/custody plan; секреты не попадают в отчёты, API или catalog manifests. |
| REQ-009 | После возврата destination worker восстанавливает missing copies из verified source; repair не переписывает newer generation, не resurrect'ит delete и не вызывает flapping. |
| REQ-010 | API/UI/metrics/alerts показывают protection state, verified copy count, debt, age, UNKNOWN, destination health и last restore drill. Labels не содержат locator/account/secret. |
| REQ-011 | Credentials least-privilege, endpoint HTTPS, tenant/role/pool routes allowlisted; capabilities не выдумываются и не обходятся fallback'ом. |
| REQ-012 | Существующие S3 и Telegram contracts сохраняются. Telegram остаётся non-S3 secondary delivery с явным size/part capability; ни один найденный adapter не удаляется и не подменяется S3. |
| REQ-013 | Read/write failover относится к фактически существующим backup objects: write — создание копий, read — materialization для download/restore. Для будущих runtime files interfaces расширяемы, но runtime file subsystem сейчас `NOT_REQUIRED`. |
| REQ-014 | Local predeploy dump классифицируется как short-lived rollback artifact, а не DR copy; его retention, шифрование и offsite policy не смешиваются с PHB3. |
| REQ-015 | Rollout сохраняет legacy local filenames/object keys/API fields; B-only copies остаются доступными после flag rollback через dual-read/locator compatibility либо выполняется roll-forward. |

## 2. Non-goals и отложенное

- Не подключать Google Drive, Yandex Disk, Mail Cloud, OneDrive, Dropbox, WebDAV, SFTP, Azure Blob или rclone: таких adapters в проекте нет (EVID-027). Новый provider добавляется только после отдельного выбора и contract fixture.
- Не превращать Telegram в object store, не переносить S3 multipart session в другой provider и не обещать атомарность внешнего send.
- Не строить Kafka/RabbitMQ: PostgreSQL job/outbox tables и существующий `BackgroundService` достаточны (DEC-004).
- Не делать runtime uploads, presigned upload, soft delete, Range/multipart runtime objects: durable runtime files отсутствуют. Admin local backup download с Range сохраняется; remote materialization создаёт проверенный local file перед тем же API.
- PostgreSQL HA/streaming replication и managed database backup — `DEFERRED`, отдельная infrastructure decision (RISK-018). PHB3/pg_dump не являются runtime failover БД.
- Cold/archive tier — `DEFERRED` до подтверждённых owner RTO/retention и provider capabilities. Hot offsite backup обязателен первым.
- Не выполнять автоматический delete remote copies в первой версии. Retention — report-only/explicit operator gate до доказанной independent-copy policy.

## 3. Архитектурные решения

| ID | Решение | Причина |
|---|---|---|
| DEC-001 | Ограничить storage orchestration backup workload'ом; не вводить generic runtime object catalog. | Нет пользовательских файлов; source of truth — PostgreSQL. |
| DEC-002 | `geo-data` считать reconstructible cache; frontend/static/exports — rebuild/stream outputs. | Они восстанавливаются из upstream/repo/DB. |
| DEC-003 | PostgreSQL availability отделить от object-storage failover. | Иначе создаётся ложное обещание runtime HA. |
| DEC-004 | Durable orchestration хранить в PostgreSQL (`BackupCopies`, `BackupDeliveryJobs`, destinations/policies) и выполнять hosted worker. | Совпадает с существующим стеком/locks, не требует брокера. |
| DEC-005 | S3 write — immutable/deterministic key + conditional create где capability подтверждена; сохранять provider response version/checksum/size. | Разрешает reconcile UNKNOWN и исключает silent overwrite. |
| DEC-006 | Backup success policy: `RequiredVerifiedExternalCopies=1` по умолчанию для backward-compatible production bootstrap; `DesiredVerifiedExternalCopies=2` — `PROPOSED`, включается только после второго независимого destination. | Не ломает single-provider config и честно показывает degraded redundancy. |
| DEC-007 | Local published PHB3 — staging/hot operational copy, но не независимая external copy. Telegram учитывается только если проходит size/capability policy. | Local разделяет failure domain с VPS; Telegram имеет ограничения. |
| DEC-008 | Snapshot создаётся один раз; delivery jobs повторно используют те же immutable bytes. | Не нагружает БД при destination failure и сохраняет BackupId/hash. |
| DEC-009 | Restore catalog должен быть доступен без production DB: non-secret manifest рядом с каждой copy + экспортируемый inventory. | Устраняет circular dependency каталога от потерянной БД. |
| DEC-010 | Failback — только после hysteresis, capability preflight, reconcile и required-copy gate; автоматически меняется eligibility, но не удаляются B-only copies. | Защита от flapping/stale overwrite. |

## 4. Pools, policies и tiers

| Data class | Source of truth | Pool/policy | Tier | Required target |
|---|---|---|---|---|
| PostgreSQL application data | PostgreSQL | `database-runtime` (вне storage router) | hot | Отдельная HA decision; PHB3 RPO below. |
| PHB3 DB snapshot | Immutable local file + `BackupRun`/`BackupCopies` | `database-backup-hot` | local staging + hot offsite | `required external=1`, `desired external=2 PROPOSED`; independent failure domains. |
| Backup catalog/manifest | DB + replicated non-secret sidecar | `database-backup-catalog` | hot | Рядом с каждой verified copy и offline export. |
| Data Protection key ring | Restricted volume + external encrypted custody | `recovery-secrets` | hot, independent | Не менее двух controlled recovery locations `PROPOSED`; owner confirmation required. |
| PHB3 keys/provider secret refs | External secret management | `recovery-secrets` | hot | Versioned key references; old decrypt keys retained through backup retention. |
| Predeploy `.dump` | Local protected directory | `deployment-rollback-local` | short-lived hot | Не считается external/DR; keep 7 current behavior until policy approval. |
| GeoIP MMDB | DB-IP upstream/local cache | `reconstructible-cache` | hot cache | No backup requirement. |
| Prometheus/Alertmanager/Caddy state | Local operational volumes | workload-specific | hot | Backup need separate owner decision; not part of PHB3 v8 unless explicitly added. |

`PROPOSED` RPO: successful protected PHB3 every 24h (current default) and alert at 1.5 intervals; `PROPOSED` RTO: 4h for isolated restore from hot offsite copy. These are recommendations, not confirmed SLOs.

## 5. Exact code and schema changes

### 5.1 Сначала полнота v8

- `src/ProxyHarbor.Infrastructure/BackupService.cs`
  - заменить ручной неполный v7 перечень новым v8 layout;
  - добавить entries для `UserApiTokens`, `UserApiTokenRequests`, `ReferralRelationships`, `ReferralRewards`, `MetricsSnapshotStates`, `ProxySourceCredentials` и фактически используемых Identity auxiliary tables;
  - сохранить `ProxyValidationLeases` как `ephemeral` и очищать lease fields по определённому contract;
  - добавить `BackupSchemaInventory` (`NEW / PROPOSED`) — allowlist всех EF entity/table с classification и archive entry.
- `src/ProxyHarbor.Infrastructure/BackupArchiveValidator.cs`
  - manifest v8 strict required entries/field allowlist; v2–v7 unchanged;
  - sidecar content manifest: entry name, row count where inexpensive, canonical SHA-256/size, schema version.
- `src/ProxyHarbor.Restore/Program.cs`
  - v8 import/delete in FK-safe order for every included table;
  - import every `BackupRun` field, включая S3 audit;
  - post-import invariant/count/sentinel verification before commit;
  - preflight reports classifications and missing recovery dependencies without printing secrets.
- `tests/ProxyHarbor.Tests/BackupRestoreRoundTripIntegrationTests.cs`
  - заменить representative subset exhaustive table fixtures/sentinels;
  - test must fail when EF model gains an unclassified entity.
- `docs/BACKUP_RESTORE.md`, `ARCHITECTURE.md`, `CONFIGURATION.md`, `README.md`
  - correct “full snapshot” wording only after v8 gate; document intentional exclusions/reprovision steps.

### 5.2 Destination model (`NEW / PROPOSED`)

Entities in `ProxyHarbor.Domain` and `ProxyHarborDbContext`:

- `BackupDestination`: `Id`, `Name`, `Kind` (`s3`/`telegram`), `Enabled`, `FailureDomain`, `Priority`, `CapabilitiesJson`, `SettingsJson`, `ProtectedSecrets`, `CreatedAt`, `UpdatedAt`, `RowVersion`/concurrency token.
- `BackupPool`: `Id`, `Name`, `RequiredVerifiedCopies`, `DesiredVerifiedCopies`, `MaxAttemptsPerCycle`, `OverallDeadlineSeconds`, `FailbackHealthyForSeconds`, `PolicyVersion`.
- `BackupPoolDestination`: pool/destination, priority, allowed operations, role (`primary|fallback|secondary`), enabled/draining.
- `BackupCopy`: unique `(BackupRunId, DestinationId)`, immutable `ContentSha256`, `SizeBytes`, `State`, `NativeLocator`, `NativeVersion`, `NativeChecksum`, `AttemptCount`, `LastAttemptAt`, `VerifiedAt`, `LastErrorCode`, `UnknownSince`, `PolicyVersion`.
- `BackupDeliveryJob`: `Id`, unique idempotency key, copy FK, `State`, `NotBefore`, `LeaseId/LeaseUntil`, `Attempt`, `LastErrorCode`, timestamps.
- `BackupRestoreVerification`: backup/copy, environment label (bounded enum), started/finished, result, application revision; no secrets/connection strings.

Constraints: bounded enums; nonnegative sizes/attempts; verified requires locator+hash+time; `UNKNOWN` has timestamp; completed protection never derives only from job existence; destination secrets never in general settings; indexes on due jobs, run/copy and last verified restore. Migration must seed a legacy S3 destination and Telegram destination from `BackupConfigurations` without exposing secrets, or retain legacy projection until operator saves new model.

### 5.3 Interfaces and services

- Replace narrow `IBackupObjectStorageTransport` internally with:
  - `IBackupDestinationAdapter` (`NEW`): `Kind`, declared capabilities, `PutAsync`, `ProbeWriteOutcomeAsync`, `MaterializeAsync`; optional operations expressed as capabilities, not throwing fake support.
  - `BackupDestinationRegistry` (`NEW`): resolves only registered DI adapters.
  - `BackupProtectionEvaluator` (`NEW`): calculates `protected/degraded/pending/unavailable` from policy and verified independent copies.
  - `BackupDeliveryPlanner` (`NEW`): immutable policy snapshot, allowlisted graph and capabilities.
  - `BackupDeliveryWorker` (`NEW`): leases due jobs, bounded retries/deadlines, reconcile UNKNOWN, catch-up.
  - `BackupCatalogService` (`NEW`): non-secret manifest/locator export and remote materialization.
- Adapt `S3BackupObjectStorageTransport` rather than rewrite it; preserve validation/key format. Add conditional create where supported, SDK checksum/version capture, separate transient/permanent/unknown error map, `GetObject` streaming verification.
- Adapt `TelegramBackupTransport` through existing resolver/client. Preserve proxy/direct transport and part behavior. Declare no list/delete/versioning/Range and a bounded maximum total size. UNKNOWN Telegram send is not automatically claimed verified without API evidence.
- `BackupService.CreateAndSendAsync` becomes snapshot creation + initial delivery orchestration. It must never call adapters serially in a way that prevents permitted fallback.
- Existing `BackupWorker` schedules snapshots; delivery worker independently drains copy debt. Existing advisory/runtime leases remain.

## 6. State and acknowledgement contracts

Copy state machine:

`PLANNED → UPLOADING → VERIFYING → VERIFIED`

Failure branches: `UPLOADING/VERIFYING → RETRYABLE_FAILED → PLANNED`, `→ PERMANENT_FAILED`, or `→ UNKNOWN → RECONCILING → VERIFIED|PLANNED|MANUAL_REVIEW`. `MISSING` is produced by reconcile. `QUARANTINED` blocks repair source selection.

- Admin trigger returns `200 protected` only after `RequiredVerifiedExternalCopies` are verified within request deadline.
- If immutable snapshot exists and durable jobs exist but required copies are not met, return `202 pending` with stable `backupRunId`; never `200 completed`.
- Invalid snapshot/source returns failure; destination fallback cannot change it.
- Desired copies below target set `degraded=true` but may satisfy required protection. No implicit required-copy downgrade.
- All destinations failed with no durable/replayable local bytes returns `503`; with durable staging may return `202 pending` only within configured capacity/TTL.
- Client retry uses idempotency key/`BackupRunId`; it does not create a second logical backup for the same request.
- Repair reads only a `VERIFIED` matching content hash and writes only a missing copy of the same immutable BackupId. No last-write-wins.

## 7. Configuration and compatibility

New DB runtime settings/API manage destinations and pool; deploy config remains bootstrap. Defaults:

- feature flag `BackupRouting__Enabled=false` during migration;
- legacy config projects to one S3 + optional Telegram destination;
- `RequiredVerifiedExternalCopies=1`; desired remains 1 until owner configures independent second destination;
- bounded per-attempt timeout, overall deadline and retry/backoff; exact numeric values measured in test/canary and marked `PROPOSED` until accepted;
- no automatic remote deletion;
- health is operation-scoped (`write`, `verify`, `read`), cached with TTL/hysteresis and not a global app readiness dependency.

Existing API fields (`sentToTelegram`, `sentToObjectStorage`, `objectStorageKey`) remain derived compatibility fields for at least one release. Add copy/policy DTOs without credentials. Existing admin UI uses established `Toggle`, `StyledSelect`, buttons, modal/table patterns per `AGENTS.md`.

## 8. Read/write/backup routing and failover

- Write: planner selects enabled destinations by pool priority, failure domain, capability, policy version and current breaker. It may move to B before bytes are sent to A, or after classified failure. UNKNOWN at A is reconciled before a duplicate attempt to deterministic A locator; B may be used concurrently if required policy still unmet.
- Read/download/restore: use actual verified copies for requested BackupId, newest valid generation only; try candidates within read policy and overall deadline. Verify size/hash and PHB3 before publish. Direct provider URLs are not exposed in v1; proxy/materialize avoids signed-URL expiry/failover ambiguity.
- Backup delivery: source snapshot must already be authenticated. Independent jobs allow A failure and B success. Overall protection is copy-policy based, not “all enabled channels succeeded”.
- Recovery: half-open probe only for the required operation; destination becomes eligible after healthy window. Catch-up prioritizes newest unprotected backups, is rate-limited, and preserves runtime snapshot cadence.
- Failback: changes preferred route only; no copy deletion. B-only locators remain dual-readable until backfill coverage and explicit cleanup approval.

## 9. Backup, restore and catalog

- Sidecar catalog contains BackupId, created time, app/schema/manifest version, ciphertext hash/size, destination kind, opaque native locator/version, protection status at export; no endpoint credentials or secret values.
- Each destination receives PHB3 plus catalog entry when its API supports an adjacent object/message. Where Telegram cannot provide independent catalog semantics, the operator inventory maps message/file evidence; it is not sole catalog of record.
- Restore CLI accepts local path first (unchanged) and a new materialization command/config reference, not inline credentials. It retrieves to private temp, verifies provider checksum when available, local SHA-256, PHB3 AEAD and strict archive manifest.
- Restore drill checks all durable table sentinels, authentication/API token behavior, paid source reprovision state, Telegram/payment config decryptability, backup creation on restored instance and absence of ephemeral leases.
- DB+files consistency: current project has no non-reconstructible runtime objects, so PHB3 is the unit. If future runtime objects appear, implementation must introduce generation references and consistent manifest before claiming combined restore.

## 10. Security and observability

- No credentials in `BackupCopy`, jobs, logs, metrics, sidecar or API. Destination keeps protected secret or external secret reference.
- Validate HTTPS/DNS/SSRF boundaries before provider calls; retain current safe endpoint restrictions. Least privilege: S3 prefix-scoped put/head/get; delete/list only if an explicitly approved feature needs them.
- Metrics: latest protected timestamp, required/desired/verified counts, jobs by bounded state/kind, oldest debt age, UNKNOWN count, breaker state by destination ID (safe internal ID), materialization failures, last restore verification.
- Alerts: RPO breach, protection unmet, debt/UNKNOWN over budget, all destinations unavailable, local staging capacity, restore drill overdue, configuration/key-ring unreadable. Do not make optional destination failure global readiness failure.

## 11. Test requirements

- `TEST-001`: exhaustive EF classification fails on new unclassified table.
- `TEST-002`: v8 round-trip every durable table/representative field and full `BackupRun` S3 audit; v2–v7 backward fixtures remain valid.
- `TEST-003`: snapshot consistency, PHB3 corruption/truncation/cancellation, cleanup and bounded memory.
- `TEST-004`: S3 local fixture contract: conditional create, checksum/version, lost response→UNKNOWN→reconcile, timeout/quota/auth/permanent errors, streaming get.
- `TEST-005`: Telegram baseline: proxy/direct, split, retry, secret redaction; capability rejection falls through to S3 without false success.
- `TEST-006`: A down/B healthy, slow A budget, A success/DB update lost, all down with/without staging, restart/resume, two workers/lease steal.
- `TEST-007`: required vs desired copy acknowledgement and API 200/202/503 mapping.
- `TEST-008`: recovery/catch-up/failback, flapping hysteresis, B-only preservation, corrupt source quarantine.
- `TEST-009`: migration legacy S3-only, Telegram-only, mixed, disabled; settings/IDs/API/UI compatibility.
- `TEST-010`: isolated restore from each real approved provider copy; measured RPO/RTO and business smoke. Requires owner authorization and test accounts; not run in ordinary CI.
- `TEST-011`: retention simulation never removes last required restore point; remote delete remains disabled until explicit acceptance.
- `TEST-012`: full project gates per `CONTRIBUTING.md`/`AGENTS.md`, migrations pending check, Docker/actionlint/security/docs.

## 12. Migration, rollout and rollback

1. Ship v8 completeness before destination schema. Continue reading v2–v7; create only v8.
2. Add schema/registry with routing flag off; seed/projection must preserve legacy settings.
3. Shadow-create copy rows from existing audit/local inventory, no remote writes/deletes. Compare diagnostics.
4. Enable new delivery for manual canary backups; legacy API remains. Stop on checksum/audit/secret leak/schema invariant failures.
5. Configure second independent test destination, backfill newest retention window, verify each copy and remote-source restore.
6. Enable scheduler routing, observe at least two normal intervals (`PROPOSED`), then retire serial delivery code.
7. Never delete legacy objects/settings during initial rollout. Rollback flag uses legacy delivery only if all B-only copies remain materializable via compatibility read; otherwise roll-forward repair is mandatory.
8. Production cutover/real provider writes require separate owner authorization. No cleanup until independent restore and retention evidence passes.

## 13. Acceptance criteria

| ID | Критерий |
|---|---|
| AC-001 | Implementation PR changes only scoped files; baseline/user changes preserved; reports remain traceable. |
| AC-002 | TEST-001/002 prove v8 covers every durable EF table and every restore deletion has matching import or explicit ephemeral classification. |
| AC-003 | v2–v7 local restore compatibility passes; v8 retains all S3 audit fields and new durable entities. |
| AC-004 | Legacy disabled/S3-only/Telegram-only/mixed settings start without new mandatory credentials and preserve API/UI contracts. |
| AC-005 | A down/B healthy produces one BackupId with B `VERIFIED`; required policy determines 200/202 truthfully, while unrelated jobs continue. |
| AC-006 | Lost response/crash/restart does not create duplicate logical backup or false verified copy; UNKNOWN is reconciled or alerted. |
| AC-007 | All destinations down obey bounded staging/backpressure and never returns protected success. |
| AC-008 | Catch-up restores required copies from matching verified hash; failback preserves B-only copies and resists flapping. |
| AC-009 | Restore works from a remote verified copy without production DB catalog and passes table/business/secret-dependency checks in isolation. |
| AC-010 | Metrics/UI expose copy protection/debt/restore evidence without secrets or unbounded labels. |
| AC-011 | S3/Telegram characterization and regression suites pass; Telegram native limitations remain explicit. |
| AC-012 | Owner approves measured RPO/RTO, retention, independent failure domains and key custody before production “DR ready” claim. |
| AC-013 | Full CI/security/migration/Docker/docs gates are green; no provider mock is presented as real-provider proof. |
| AC-014 | No remote cleanup, primary switch or production fault injection occurs without a separate approved operator step and rollback/roll-forward gate. |

## 14. Definition of Done

Все REQ имеют `TASK` и `TEST/AC`, RISK-001–016/019–020 либо закрыты evidence, либо явно приняты владельцем; v8 completeness и isolated restore обязательны. Production-ready наступает только после STG-06: реальный approved provider canary, independent recovery dependencies, measured drill and observation window. До этого статус — implemented/tested locally, а не operationally verified.
