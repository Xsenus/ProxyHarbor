# Участие в разработке ProxyHarbor

Спасибо за помощь проекту. Изменения должны сохранять безопасность сети, воспроизводимость и совместимость публичного API.

## Локальная проверка

```powershell
dotnet build ProxyHarbor.slnx -c Release
dotnet test ProxyHarbor.slnx -c Release --no-build
dotnet format ProxyHarbor.slnx --verify-no-changes --no-restore
./tools/Test-ActionlintContracts.ps1
./tools/Invoke-Actionlint.ps1
./tools/Test-GitleaksContracts.ps1
./tools/Test-CodeQLContracts.ps1
./tools/Test-PublicationReadiness.ps1
./tools/Test-PublicationReadinessContracts.ps1
cd src/proxyharbor-web
npm ci
npm run lint
npm run build
```

Для изменений PostgreSQL создайте EF Core migration и убедитесь, что `dotnet ef migrations has-pending-model-changes` не сообщает расхождений. Для сетевой логики добавляйте unit-тест либо воспроизводимый integration-сценарий.

PostgreSQL integration-тесты используют одну внешнюю базу и сериализованы xUnit-коллекцией. Задайте `PROXYHARBOR_INTEGRATION_POSTGRES`; тест concurrent startup дополнительно создаёт и удаляет только собственную случайную schema, чтобы проверить гонку migrations/seed на чистом состоянии без изменения пользовательских таблиц.

## Новые proxy-feed’ы

- только публичный HTTPS URL без регистрации и секретного API-ключа;
- владелец источника должен разрешать автоматическое получение списка;
- endpoint обязан возвращать распознаваемые `host:port` либо `scheme://host:port`;
- укажите проект-провайдер, протокол, частоту обновления и ссылку на условия использования;
- перед PR выполните `tools/Audit-SourceFeeds.ps1` на отдельной тестовой БД.

Не добавляйте сами списки прокси, токены, `.env`, backup-файлы и production-дампы в Git.

Full-history secret scan запускается в Linux x64 CI через `tools/Invoke-Gitleaks.ps1`. Если секрет когда-либо попал в commit, одного удаления из последующего commit недостаточно: немедленно отзовите его и очистите историю согласованным способом до публикации.

## Release changes

Не создавайте release tag из непроверенной feature-ветки. Заметные изменения добавляйте в `Unreleased` файла `CHANGELOG.md`; перед тегом создайте датированный раздел точной SemVer-версии. Сторонний container image указывайте только как `version-tag@sha256:manifest-digest`; при обновлении меняйте тег и digest вместе. PostgreSQL pin синхронно повторяется в `docker-compose.yml` и service containers файлов `ci.yml`, `release.yml`, `source-audit.yml`, поскольку Dependabot не обновляет workflow service images. Любое изменение Dockerfile, Compose overlay или `.github/workflows/release.yml` должно пройти `Test-ReleaseMetadata.ps1`, `Test-ChangelogContracts.ps1`, `Test-WorkflowSecurity.ps1`, `Test-WorkflowSecurityContracts.ps1`, actionlint и container configuration gate. Выпуск выполняется только строгим SemVer-тегом по процедуре [docs/RELEASING.md](docs/RELEASING.md).
