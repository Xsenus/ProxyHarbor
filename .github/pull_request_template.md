## Что изменено

<!-- Кратко опишите результат и мотивацию. -->

## Проверки

- [ ] `dotnet build ProxyHarbor.slnx -c Release`
- [ ] `dotnet test ProxyHarbor.slnx -c Release --no-build`
- [ ] `dotnet format ProxyHarbor.slnx --verify-no-changes --no-restore`
- [ ] `npm run lint && npm run build` в `src/proxyharbor-web`
- [ ] `./tools/Test-DocumentationLinks.ps1`
- [ ] Миграции, Docker и сетевые изменения проверены в релевантном runtime-сценарии
- [ ] Изменения поведения отражены в README/authoritative docs и `CHANGELOG.md`, если это заметно пользователю
- [ ] Новый proxy feed имеет подтверждённые условия использования и проходит source audit
- [ ] Изменения backup/restore проверены отдельным restore drill
- [ ] В diff нет секретов, backup-файлов и списков собранных прокси
