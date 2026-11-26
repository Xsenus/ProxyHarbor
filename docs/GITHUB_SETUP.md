# Публикация ProxyHarbor на GitHub

Этот checklist выполняется владельцем после создания удалённого репозитория. Настройки GitHub не хранятся в Git, поэтому публикация не считается защищённой, пока каждый пункт не проверен в интерфейсе репозитория.

## До первого push

1. Запустите `./tools/Test-PublicationReadiness.ps1 -RequireCleanWorktree`, `./tools/Test-DocumentationLinks.ps1`, фактический `./tools/Invoke-Gitleaks.ps1` и полный локальный CI-набор из `CONTRIBUTING.md`.
2. Создайте публичный репозиторий без автоматически сгенерированных README/LICENSE/.gitignore, чтобы не расходилась история.
3. Добавьте remote и отправьте `main` обычным push. Установите `main` веткой по умолчанию. Не применяйте force push и не публикуйте `.env`, backup либо production dump.
4. Дождитесь первого успешного выполнения CI и CodeQL до включения required checks: GitHub предлагает только checks, которые уже запускались.

Пример после создания пустого репозитория:

```powershell
git remote add origin https://github.com/YOUR_GITHUB_OWNER/ProxyHarbor.git
git remote -v
git push --set-upstream origin main
```

Если `origin` уже существует, сначала проверьте его через `git remote get-url origin`; не заменяйте неизвестный remote вслепую. После выбора владельца обновите clone URL в `README.md`.

Рекомендуемое описание: `Fast self-hosted free proxy collector, validator and JSON/XML/TXT/CSV API built with ASP.NET Core and React.`

Рекомендуемые topics: `proxy`, `proxy-list`, `proxy-checker`, `socks5`, `aspnet-core`, `react`, `postgresql`, `docker`, `openapi`, `self-hosted`.

## Actions и безопасность

- В `Settings → Actions → General` разрешите Actions; default `GITHUB_TOKEN` оставьте read-only. Workflow сами запрашивают минимальные дополнительные permissions на уровне job.
- Разрешите только используемые namespaces: `actions/*`, `docker/*`, `github/codeql-action/*`. Локальные скрипты Gitleaks/actionlint скачивают закреплённые архивы и проверяют SHA-256.
- В [`Settings → Code security and analysis`](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-security-and-analysis-settings-for-your-repository) включите Dependency graph, Dependabot alerts/security updates, Code scanning, Secret scanning и Push protection.
- Dependabot открывает PR для исправлений безопасности. Плановые обновления зависимостей оформляются вручную. Включите автоматическое удаление веток после слияния PR.
- Включите Private vulnerability reporting. Публичные отчёты об уязвимостях запрещены политикой `SECURITY.md`.
- Не добавляйте production-секреты в Actions variables. Текущие CI/release workflows используют только искусственные значения; runtime-секреты остаются на production host в Compose secret files.

## Защита ветки `main`

Создайте [active branch ruleset](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/creating-rulesets-for-a-repository) для default branch:

- запрет удаления и force push;
- внесение изменений через pull request. Для репозитория с одним сопровождающим установите 0 обязательных одобрений. Увеличьте число одобрений после добавления второго сопровождающего.
- dismiss stale approvals и require resolution всех review conversations;
- strict required status checks, ветка должна быть актуальна относительно `main`;
- обязательные проверки — `verify`, `postgres-integration`, `Analyze csharp`, `Analyze javascript-typescript`;
- code scanning merge protection: блокировать новые CodeQL alerts уровня High/Critical;
- применяйте правила защиты к администраторам репозитория.

Отдельный active tag ruleset для `v*.*.*` должен запрещать изменение и удаление существующих тегов, а создание разрешать только release-администраторам. Сам release выполняется строго по `docs/RELEASING.md`.

## Packages и первый release

После первого SemVer tag убедитесь, что появились три GHCR package: `proxyharbor-api`, `proxyharbor-web`, `proxyharbor-restore`. Свяжите их с репозиторием, сделайте публичными для бесплатного сервиса и проверьте digest manifest, SBOM/provenance и GitHub attestation по процедуре выпуска.

В `Settings → General` отключите Wiki/Projects, если они не используются, включите Issues, задайте описание и topics. После появления реальных сопровождающих добавьте `.github/CODEOWNERS`; не указывайте вымышленный аккаунт до определения владельцев.

## Проверка после первого push

- README корректно отображает скриншоты и ссылки;
- Issues показывают bug/source/documentation templates, blank issues отключены;
- вкладка Security видит `SECURITY.md`, CodeQL и Dependabot;
- задачи CI `verify` и `postgres-integration`, а также обе задачи CodeQL завершились успешно;
- package/release workflows не запускаются без SemVer tag;
- repository не содержит `.env`, `.phbackup`, dumps, audit artifacts или собранные proxy lists;
- clone чистого репозитория проходит `Test-PublicationReadiness.ps1 -RequireCleanWorktree`.
