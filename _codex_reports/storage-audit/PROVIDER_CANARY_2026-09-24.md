# HOSTKEY NL isolated S3 protocol canary — 2026-09-24

## Scope and authorization

- Owner authorized writes and deletion only in the separate test bucket under `proxyharbor-drill/`.
- Endpoint: `https://s3-nl.hostkey.com`; region: `nl`; bucket: `b6ab81768-proxyharbor`.
- Executed from the local workspace using `tools/Invoke-IsolatedS3Canary.ps1` with interactive credentials. Credentials were not written to this report, command arguments, or process environment.
- Synthetic PHB3 only; no production backup, catalog, Data Protection keys, or database was transferred.

## Observed result

On 2026-09-23 UTC (2026-09-24 Asia/Novosibirsk), the canary exited with code 0 and reported:

| Check | Result |
|---|---|
| Encrypted PHB3 PUT and HEAD identity | PASS |
| PHB3 GET, SHA-256, and decrypt/verify | PASS |
| Conditional duplicate PUT rejected as collision | PASS |
| Signed catalog sidecar PUT, HEAD, GET, and open | PASS |
| Addressed deletion and absence confirmation of both created objects | PASS |

The created object keys used a random canary basename beneath `proxyharbor-drill/`. No production object was modified. This proves only the listed operations against this provider/bucket at this time. Bucket versioning/Object Lock, failure injection, quota/auth failures, second independent provider, and a real application restore remain **NOT VERIFIED**.

## Production observation (read-only)

At the same audit, SSH to the previously supplied VPS succeeded. Five ProxyHarbor containers were running. `/opt/proxyharbor` was at `981c1ca02`, whereas local `main` was `538799f` (40 commits ahead). The API had mounted local backup and Data Protection volumes. Several local `.phbackup` files and a DP key XML file existed; their content and recoverability were not inspected. This is not evidence of offsite protection or a completed restore drill. No production write was made.

## Remaining acceptance gates

1. Obtain explicit owner approval and an isolated PostgreSQL target before transferring production backup/key material or running a full restore.
2. Recover a real archive and signed catalog without depending on the production DB/VPS; verify PHB3 key and pre-existing DP marker from independent escrow.
3. Exercise isolated restore, sentinel/login/credential checks, RPO/RTO measurement, and cleanup.
4. Only then decide on production rollout, with a separate owner approval and deployment gate.
