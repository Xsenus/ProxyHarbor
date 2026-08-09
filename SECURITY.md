# Политика безопасности

## Сообщение об уязвимости

Не публикуйте сведения об уязвимости в обычном issue. Используйте приватный GitHub Security Advisory репозитория и приложите версию, сценарий воспроизведения, ожидаемое влияние и предлагаемые меры. Не прикладывайте реальные токены, backup-файлы или данные пользователей.

## Границы доверия

ProxyHarbor получает недоверенные данные из внешних публичных feed’ов. Поэтому проект ограничивает размер и время ответа, запрещает private и актуальные IANA special-purpose ranges, URL fragments, повторно проверяет DNS перед соединением и независимо валидирует каждый прокси. Фильтр блокирует в том числе 6to4, benchmarking и documentation prefixes, не отбрасывая соседние публичные сети более широкими масками. После канонизации scheme/host URL path и query сравниваются точно, без ошибочного case folding. Публичный прокси контролируется третьей стороной: через него нельзя передавать пароли, платёжные данные, cookies или иные секреты.

Канонические реестры для сопровождения фильтра: [IANA IPv4 Special-Purpose Address Space](https://www.iana.org/assignments/iana-ipv4-special-registry/iana-ipv4-special-registry.xhtml) и [IANA IPv6 Special-Purpose Address Space](https://www.iana.org/assignments/iana-ipv6-special-registry/iana-ipv6-special-registry.xhtml).

Control endpoint проверки должен быть доверенным HTTPS-сервисом с корректным сертификатом и JSON-полем `ip`. Его недоступность или некорректный ответ никогда не считается доказательством неисправности прокси: health-gate не арендует очередь, а уже полученный неоднозначный результат откладывается без изменения статистики качества.

Admin middleware отклоняет отсутствующий, oversized или многозначный `X-Admin-Key`, даже если объединение значений запятой совпало бы с настроенным секретом; сравнение выполняется по SHA-256 через constant-time API. Успешные и ошибочные admin-ответы получают `Cache-Control: no-store`. Панель позволяет явно удалить ключ из sessionStorage и локального состояния, а запрет Storage API не ломает in-memory сессию. API и nginx выставляют CSP, HSTS и cross-origin security headers; HSTS начинает действовать только при публикации через HTTPS.

Backup содержит данные БД и полную поддерживаемую безопасную часть collector/backup/runtime-настроек, шифруется PHB3 до отправки и не включает ключ шифрования, Telegram-токен, строку БД либо административный ключ. Manifest v3 требует все settings-файлы, а restore отклоняет любую ZIP-запись вне фиксированной схемы, поэтому ложное `secretsIncluded=false` не может скрыть дополнительный файл с секретами. После аварийного завершения следующий backup под cluster lock удаляет оставшиеся plaintext ZIP, `.partial` и Telegram-part файлы. Потеря ключа восстановления необратима.

Telegram-клиент не следует redirect, запрещает соединения с приватными/служебными адресами после DNS-разрешения и не включает URI с bot token в стандартные HTTP-логи. HTTP 2xx без корректного ограниченного JSON-ответа `ok=true` считается неподтверждённой доставкой, поэтому не может выставить успешный backup audit.

Control endpoint validator также не следует redirect и использует повторную public-DNS проверку непосредственно перед соединением. Прямой распакованный ответ ограничен 16 КБ, ответ внутри TLS proxy-туннеля — 64 КБ; конфликтующие `Content-Length`/chunked framing и oversized headers/body отклоняются до JSON-разбора.

Docker build context использует allowlist проектов: `.env`, backup, локальные PostgreSQL data directories, `.git`, `node_modules`, `bin/obj` и другие непредназначенные для образа данные исключены до отправки Docker daemon. CI создаёт секретные sentinel-файлы и прерывает сборку, если хотя бы один оказывается внутри тестового context image.

В Production browser CORS закрыт по умолчанию; добавляйте только точные HTTP(S) origins без пути. Доверенные `X-Forwarded-For` и `X-Forwarded-Proto` ограничиваются одним переходом и CIDR reverse proxy. Не расширяйте доверие до общей private-сети: синхронно задавайте `BACKEND_SUBNET` в Docker Compose либо `ForwardedHeaders__KnownNetworks__N` в собственной инфраструктуре.
