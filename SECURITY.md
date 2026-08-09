# Политика безопасности

## Сообщение об уязвимости

Не публикуйте сведения об уязвимости в обычном issue. Используйте приватный GitHub Security Advisory репозитория и приложите версию, сценарий воспроизведения, ожидаемое влияние и предлагаемые меры. Не прикладывайте реальные токены, backup-файлы или данные пользователей.

## Границы доверия

ProxyHarbor получает недоверенные данные из внешних публичных feed’ов. Поэтому проект ограничивает размер и время ответа, запрещает private и актуальные IANA special-purpose ranges, URL fragments, повторно проверяет DNS перед соединением и независимо валидирует каждый прокси. Фильтр блокирует в том числе 6to4, benchmarking и documentation prefixes, не отбрасывая соседние публичные сети более широкими масками. После канонизации scheme/host URL path и query сравниваются точно, без ошибочного case folding. Публичный прокси контролируется третьей стороной: через него нельзя передавать пароли, платёжные данные, cookies или иные секреты.

Канонические реестры для сопровождения фильтра: [IANA IPv4 Special-Purpose Address Space](https://www.iana.org/assignments/iana-ipv4-special-registry/iana-ipv4-special-registry.xhtml) и [IANA IPv6 Special-Purpose Address Space](https://www.iana.org/assignments/iana-ipv6-special-registry/iana-ipv6-special-registry.xhtml).

Control endpoint проверки должен быть доверенным HTTPS-сервисом с корректным сертификатом и JSON-полем `ip`. Его недоступность или некорректный ответ никогда не считается доказательством неисправности прокси: health-gate не арендует очередь, а уже полученный неоднозначный результат откладывается без изменения статистики качества.

Admin middleware отклоняет отсутствующий, oversized или многозначный `X-Admin-Key`, даже если объединение значений запятой совпало бы с настроенным секретом; сравнение выполняется по SHA-256 через constant-time API. Успешные и ошибочные admin-ответы получают `Cache-Control: no-store`. Панель позволяет явно удалить ключ из sessionStorage и локального состояния, а запрет Storage API не ломает in-memory сессию. API и nginx выставляют CSP, HSTS и cross-origin security headers; HSTS начинает действовать только при публикации через HTTPS.

Backup содержит данные БД и полную поддерживаемую безопасную часть collector/backup/runtime-настроек, шифруется PHB3 до отправки и не включает ключ шифрования, Telegram-токен, строку БД либо административный ключ. Manifest v3 требует все settings-файлы, а restore отклоняет любую ZIP-запись вне фиксированной схемы, поэтому ложное `secretsIncluded=false` не может скрыть дополнительный файл с секретами. ZIP передаётся шифратору через bounded memory pipe и никогда не записывается на диск в открытом виде; после аварийного завершения следующий backup под cluster lock удаляет `.partial`, Telegram-part и legacy plaintext ZIP от прежних версий. Потеря ключа восстановления необратима.

Compose restore доступен только через явный profile `tools`, работает без root и требует флаг `--replace-existing-data`. Backup volume подключается ему read-only, а временный расшифрованный ZIP создаётся в отдельном анонимном volume одноразового контейнера и удаляется приложением до завершения. Перед restore необходимо остановить API и web: startup advisory lock защищает migrations, но не предназначен для одновременной замены таблиц и работы collector/validator.

Telegram-клиент не следует redirect, запрещает соединения с приватными/служебными адресами после DNS-разрешения и не включает URI с bot token в стандартные HTTP-логи. HTTP 2xx без корректного ограниченного JSON-ответа `ok=true` считается неподтверждённой доставкой, поэтому не может выставить успешный backup audit.

Control endpoint validator также не следует redirect и использует повторную public-DNS проверку непосредственно перед соединением. Прямой распакованный ответ ограничен 16 КБ, ответ внутри TLS proxy-туннеля — 64 КБ; конфликтующие `Content-Length`/chunked framing и oversized headers/body отклоняются до JSON-разбора.

Docker build context использует allowlist проектов: `.env`, backup, локальные PostgreSQL data directories, `.git`, `node_modules`, `bin/obj` и другие непредназначенные для образа данные исключены до отправки Docker daemon. CI создаёт секретные sentinel-файлы и прерывает сборку, если хотя бы один оказывается внутри тестового context image.

NuGet restore использует репозиторный `NuGet.Config` с единственным источником `nuget.org`, а полный transitive graph фиксируется в `packages.lock.json`. CI и Docker применяют только locked restore, выполняют NuGet/npm vulnerability audit и не принимают незаявленное изменение графа зависимостей. Сторонние GitHub Actions закреплены на полных commit SHA. Dependabot еженедельно готовит отдельные обновления NuGet, npm, GitHub Actions и container images; такие PR проходят те же тесты, PostgreSQL integration и container smoke-gates.

В Production browser CORS закрыт по умолчанию; добавляйте только точные HTTP(S) origins без пути. Доверенные `X-Forwarded-For` и `X-Forwarded-Proto` ограничиваются одним переходом и CIDR reverse proxy. Не расширяйте доверие до общей private-сети: синхронно задавайте `BACKEND_SUBNET` в Docker Compose либо `ForwardedHeaders__KnownNetworks__N` в собственной инфраструктуре.

Публичный Docker deployment должен использовать `docker-compose.production.yml`: он удаляет прямую публикацию frontend-порта и оставляет единственной точкой входа hardened Caddy с автоматическим HTTPS. Gateway работает непривилегированным UID, без capabilities и с read-only root filesystem; API доверяет ему только как одному переходу внутри выделенной backend-сети. Базовый HTTP-порт 8080 предназначен исключительно для локальной разработки. Административный ключ нельзя передавать через него по недоверенной сети.

Production gateway не публикует `/metrics`: operational-срез содержит состояние источников, очередей и backup. Opt-in Prometheus получает его напрямую внутри backend-сети, сам работает distroless/non-root с read-only root filesystem и публикует UI только на loopback хоста. Для удалённого доступа используйте SSH tunnel либо отдельный аутентифицированный monitoring-контур; не меняйте binding на `0.0.0.0` без TLS и контроля доступа.

Alertmanager также публикуется только на loopback. Telegram token и числовой chat ID поступают из host environment в Compose secrets-файлы и не присутствуют в environment/конфигурации Alertmanager или его persistent notification volume; Telegram HTTP redirect запрещён, чтобы token из Bot API URL не ушёл на другой origin. Не копируйте секреты в шаблоны и не выводите содержимое `/run/secrets` в диагностику; при ротации пересоздайте контейнер Alertmanager.
