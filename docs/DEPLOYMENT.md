# Production-развёртывание

## До запуска

1. Установите Docker Engine 26+ и Docker Compose 2.24.4+.
2. Направьте DNS A/AAAA для выбранного имени на сервер. Если IPv6 фактически не маршрутизируется, не создавайте AAAA-запись.
3. Разрешите входящие TCP 80/443 и UDP 443. PostgreSQL, API и порт frontend наружу не публикуются.
4. Скопируйте `.env.example` в `.env`, замените все обязательные ключи и задайте `PUBLIC_HOST` без схемы/пути и `ACME_EMAIL`.
5. До публичного запуска проверьте условия использования всех источников и ограничьте admin routes дополнительным firewall/WAF, если панель не должна быть общедоступной.

## Запуск и проверка

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml config
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d --build
docker compose -f docker-compose.yml -f docker-compose.production.yml ps
curl --fail https://proxy.example.com/health/ready
curl --fail https://proxy.example.com/api/v1/stats
```

Не добавляйте `https://` в `PUBLIC_HOST`. Первый выпуск сертификата требует корректного DNS и доступности портов 80/443. Named volumes `caddy-data` и `caddy-config` содержат сертификаты и ACME-состояние; не удаляйте их при обычном обновлении.

Проверьте отдельно admin-аутентификацию, создание зашифрованного backup, подтверждённую Telegram-доставку, расшифровку и пробное восстановление в отдельную БД. Настройте внешний мониторинг `/health/ready`, срока TLS-сертификата, свободного диска, PostgreSQL и ключевых Prometheus-метрик.

## Обновление и откат

Перед обновлением создайте и проверьте backup. Затем получите новую версию и повторите production-команду `up -d --build`; постоянные volumes не заменяются. Для диагностики используйте:

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml logs --tail 200 api caddy
```

Откат выполняйте на предыдущий проверенный Git commit той же командой. Не откатывайте код через уже применённые необратимые миграции без заранее проверенного плана восстановления БД.

По умолчанию Caddy должен быть непосредственной публичной точкой входа. При добавлении CDN/load balancer настройте доверенные proxy ranges одновременно в Caddy и API; иначе rate limiting будет видеть адрес промежуточного узла. Никогда не доверяйте произвольному `X-Forwarded-For` из интернета.

## Встроенный мониторинг

Opt-in профиль запускает Prometheus с 30-дневным/10-ГБ bounded retention и готовыми alert rules:

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile monitoring up -d
curl --fail http://127.0.0.1:9090/-/ready
```

Порт привязан только к loopback. Для удалённого просмотра используйте SSH tunnel `ssh -L 9090:127.0.0.1:9090 user@server`, а не открывайте Prometheus в интернет. История находится в volume `prometheus-data`; пределы меняются через `PROMETHEUS_RETENTION_TIME` и `PROMETHEUS_RETENTION_SIZE`. Уведомления требуют отдельного доверенного Alertmanager либо интеграции существующей monitoring-платформы. Runbook и alarms описаны в [MONITORING.md](MONITORING.md).
