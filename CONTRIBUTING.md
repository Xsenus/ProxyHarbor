# Участие в разработке ProxyHarbor

Спасибо за помощь проекту. Изменения должны сохранять безопасность сети, воспроизводимость и совместимость публичного API.

## Локальная проверка

```powershell
dotnet build ProxyHarbor.slnx -c Release
dotnet test ProxyHarbor.slnx -c Release --no-build
dotnet format ProxyHarbor.slnx --verify-no-changes --no-restore
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
