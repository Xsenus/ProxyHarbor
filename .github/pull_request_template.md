## Что изменено

<!-- Кратко опишите результат и мотивацию. -->

## Проверки

- [ ] `dotnet build ProxyHarbor.slnx -c Release`
- [ ] `dotnet test ProxyHarbor.slnx -c Release --no-build`
- [ ] `dotnet format ProxyHarbor.slnx --verify-no-changes --no-restore`
- [ ] `npm run lint && npm run build` в `src/proxyharbor-web`
- [ ] Миграции, Docker и сетевые изменения проверены в релевантном runtime-сценарии
- [ ] В diff нет секретов, backup-файлов и списков собранных прокси
