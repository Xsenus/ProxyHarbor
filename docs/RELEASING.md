# Выпуск ProxyHarbor

## Предварительные условия

1. Настройте Git remote на целевой GitHub-репозиторий, включите GitHub Actions/Packages и ruleset, разрешающий создание `v*` tag только release-администраторам.
2. Убедитесь, что CI и последний `Source feed audit` зелёные, рабочее дерево чистое, а `main` содержит нужный commit.
3. Для private/internal GitHub Enterprise Cloud задайте repository variable `ENABLE_GITHUB_ATTESTATIONS=true`. В публичном репозитории attestation включается автоматически; на планах без private attestations BuildKit SBOM/provenance всё равно публикуются вместе с OCI manifest.
4. Выберите строгую SemVer-версию. Примеры: `v1.0.0`, `v1.1.0-rc.1`; leading zero и tag без префикса `v` намеренно отклоняются.

## Создание релиза

Рекомендуется подписанный annotated tag:

```bash
git switch main
git pull --ff-only
git tag -s v1.0.0 -m "ProxyHarbor 1.0.0"
git push origin v1.0.0
```

Сначала workflow заново проверяет именно tagged commit: locked dependency graph, отсутствие pending EF migration, backend на настоящей PostgreSQL, frontend, vulnerability audits, operational contracts и release Compose. Publish jobs получают write-permissions и начинают сборку только после этого gate. Затем workflow собирает три multi-architecture manifest, публикует их в `ghcr.io/<owner>`, создаёт provenance и только после успеха всех matrix jobs создаёт GitHub Release. Повторный запуск идемпотентно заменяет attached manifest/Compose-файлы, не создавая второй релиз.

`proxyharbor-release.json` — authoritative mapping компонента на digest. Для deployment по неизменяемой ссылке вместо tag можно подставить `image@sha256:...` из этого файла в собственный override.

## Проверка поставки

```bash
docker buildx imagetools inspect ghcr.io/OWNER/proxyharbor-api:1.0.0
gh attestation verify oci://ghcr.io/OWNER/proxyharbor-api:1.0.0 --repo OWNER/REPOSITORY
```

Повторите attestation-проверку для `proxyharbor-web` и `proxyharbor-restore`. Затем разверните release overlay в отдельном окружении, дождитесь `/health/ready`, выполните JSON/XML/TXT/CSV smoke-export, ручной backup и пробный restore. Только после этого обновляйте production.

## Откат

Верните `PROXYHARBOR_IMAGE_TAG` на предыдущую проверенную версию и повторите `docker compose up -d`. Не откатывайте код поверх уже применённых несовместимых миграций: сначала используйте backup/restore процедуру и отдельную тестовую БД.
