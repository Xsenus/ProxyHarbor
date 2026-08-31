namespace ProxyHarbor.Domain;

/// <summary>Поддерживаемые транспортные протоколы публичных прокси.</summary>
public enum ProxyProtocol
{
    /// <summary>Открытый HTTP forward proxy без шифрования участка клиент–прокси.</summary>
    Http,
    /// <summary>HTTP proxy, объявленный источником как HTTPS endpoint.</summary>
    Https,
    /// <summary>Прокси протокола SOCKS4/SOCKS4a.</summary>
    Socks4,
    /// <summary>Прокси протокола SOCKS5.</summary>
    Socks5
}

/// <summary>Текущее состояние прокси по результатам последней проверки.</summary>
public enum ProxyStatus
{
    /// <summary>Адрес ещё не получил объективного результата проверки.</summary>
    Pending,
    /// <summary>Последняя объективная проверка успешно прошла через прокси.</summary>
    Alive,
    /// <summary>Последняя объективная проверка установила неработоспособность прокси.</summary>
    Dead
}

/// <summary>Поддерживаемые форматы явно опубликованных бесплатных VPN-конфигураций.</summary>
public enum VpnProtocol
{
    /// <summary>Конфигурация OpenVPN.</summary>
    OpenVpn,
    /// <summary>Конфигурация WireGuard.</summary>
    WireGuard,
    /// <summary>URI протокола VLESS.</summary>
    Vless,
    /// <summary>Base64-конфигурация VMess.</summary>
    Vmess,
    /// <summary>URI протокола Trojan.</summary>
    Trojan,
    /// <summary>URI протокола Shadowsocks.</summary>
    Shadowsocks,
    /// <summary>URI протокола Hysteria 2.</summary>
    Hysteria2,
    /// <summary>URI протокола TUIC.</summary>
    Tuic
}

/// <summary>Результат безопасной проверки доступности VPN endpoint без использования опубликованных credentials.</summary>
public enum VpnEndpointStatus
{
    /// <summary>Endpoint ещё не проверен.</summary>
    Pending,
    /// <summary>TCP endpoint доступен.</summary>
    Reachable,
    /// <summary>TCP endpoint недоступен.</summary>
    Unreachable,
    /// <summary>Транспорт нельзя безопасно проверить без credentials.</summary>
    UnsupportedTransport
}

/// <summary>Публичный VPN endpoint и опубликованная провайдером ссылка подключения.</summary>
public sealed class VpnEndpoint
{
    /// <summary>Стабильный идентификатор.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Публичный IP или каноническое DNS-имя.</summary>
    public required string Host { get; set; }
    /// <summary>Порт VPN endpoint.</summary>
    public int Port { get; set; }
    /// <summary>Формат VPN-конфигурации.</summary>
    public VpnProtocol Protocol { get; set; }
    /// <summary>TCP либо UDP; только TCP проверяется активным подключением в первой версии.</summary>
    public string Transport { get; set; } = "tcp";
    /// <summary>ISO 3166-1 alpha-2 страны endpoint, определённый локальным GeoIP-справочником.</summary>
    public string? CountryCode { get; set; }
    /// <summary>
    /// Готовая публичная URI-конфигурация из открытого feed. Значение требуется клиенту
    /// для импорта VLESS/VMess/Trojan/SS и не является внутренним секретом ProxyHarbor.
    /// </summary>
    public string? ConnectionUri { get; set; }
    /// <summary>Текущее состояние доступности.</summary>
    public VpnEndpointStatus Status { get; set; } = VpnEndpointStatus.Pending;
    /// <summary>Задержка установления TCP-соединения.</summary>
    public int? LatencyMs { get; set; }
    /// <summary>Первое обнаружение.</summary>
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Последнее обнаружение в feed.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Последняя проверка доступности.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }
    /// <summary>План следующей проверки.</summary>
    public DateTimeOffset? NextCheckAt { get; set; }
    /// <summary>Число успешных TCP-проверок.</summary>
    public int SuccessfulChecks { get; set; }
    /// <summary>Число неуспешных TCP-проверок.</summary>
    public int FailedChecks { get; set; }
    /// <summary>Безопасная диагностика без секретов.</summary>
    public string? LastError { get; set; }
    /// <summary>Источник-первооткрыватель. Полный provenance сохраняется отдельными связями.</summary>
    public Guid? FirstSourceId { get; set; }
    /// <summary>Навигация к первому источнику.</summary>
    public VpnSource? FirstSource { get; set; }
    /// <summary>Все provenance-связи.</summary>
    public ICollection<VpnEndpointSource> Sources { get; set; } = [];
}

/// <summary>Разрешённый HTTPS feed публичных VPN-конфигураций.</summary>
public sealed class VpnSource
{
    /// <summary>Стабильный идентификатор.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Отображаемое имя feed.</summary>
    public required string Name { get; set; }
    /// <summary>Владелец или автор feed.</summary>
    public required string Provider { get; set; }
    /// <summary>Публичный HTTPS URL.</summary>
    public required string Url { get; set; }
    /// <summary>Протокол строк без явной схемы.</summary>
    public VpnProtocol DefaultProtocol { get; set; }
    /// <summary>Участвует ли источник в сборе.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Порядок обработки.</summary>
    public int Priority { get; set; } = 100;
    /// <summary>Идентификатор лицензии либо пояснение условий публикации.</summary>
    public required string License { get; set; }
    /// <summary>Последняя попытка загрузки.</summary>
    public DateTimeOffset? LastFetchedAt { get; set; }
    /// <summary>Последняя успешная загрузка.</summary>
    public DateTimeOffset? LastSucceededAt { get; set; }
    /// <summary>Не раньше какого момента повторять загрузку после ошибки.</summary>
    public DateTimeOffset? NextFetchAt { get; set; }
    /// <summary>Число безопасно разобранных endpoint.</summary>
    public int LastItemCount { get; set; }
    /// <summary>Число последовательных ошибок.</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>Bounded-диагностика последней ошибки.</summary>
    public string? LastError { get; set; }
    /// <summary>Связи с найденными endpoint.</summary>
    public ICollection<VpnEndpointSource> Endpoints { get; set; } = [];
}

/// <summary>Provenance-связь VPN endpoint с каждым feed, где он был замечен.</summary>
public sealed class VpnEndpointSource
{
    /// <summary>Идентификатор VPN endpoint.</summary>
    public Guid VpnEndpointId { get; set; }
    /// <summary>Навигация к VPN endpoint.</summary>
    public VpnEndpoint? VpnEndpoint { get; set; }
    /// <summary>Идентификатор источника.</summary>
    public Guid VpnSourceId { get; set; }
    /// <summary>Навигация к источнику.</summary>
    public VpnSource? VpnSource { get; set; }
    /// <summary>Последнее подтверждение связи.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Публичный прокси-сервер и накопленная статистика его качества.</summary>
public sealed class ProxyEndpoint
{
    /// <summary>Внутренний стабильный идентификатор строки прокси.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Канонический публичный IPv4- или IPv6-адрес без скобок.</summary>
    public required string Host { get; set; }
    /// <summary>TCP-порт в диапазоне 1–65535.</summary>
    public int Port { get; set; }
    /// <summary>Транспортный протокол подключения к прокси.</summary>
    public ProxyProtocol Protocol { get; set; }
    /// <summary>Состояние, подтверждённое последней объективной проверкой.</summary>
    public ProxyStatus Status { get; set; } = ProxyStatus.Pending;
    /// <summary>Полная измеренная задержка успешной проверки в миллисекундах.</summary>
    public int? LatencyMs { get; set; }
    /// <summary>Канонический внешний IP, увиденный контрольным endpoint.</summary>
    public string? ExitIp { get; set; }
    /// <summary>Двухбуквенный ISO-код страны выхода, если он был определён.</summary>
    public string? CountryCode { get; set; }
    /// <summary>Прокси не раскрыл исходный IP контрольному endpoint.</summary>
    public bool IsAnonymous { get; set; }
    /// <summary>Первое появление адреса в любом успешно разобранном источнике.</summary>
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Последнее появление адреса в успешно разобранном источнике.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Время последней объективной Alive/Dead-проверки.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }
    /// <summary>Момент первого когда-либо подтверждённого успешного подключения.</summary>
    public DateTimeOffset? FirstAliveAt { get; set; }
    /// <summary>Момент последнего подтверждённого успешного подключения.</summary>
    public DateTimeOffset? LastAliveAt { get; set; }
    /// <summary>Начало текущей непрерывной серии Alive; null для Pending и Dead.</summary>
    public DateTimeOffset? CurrentAliveSince { get; set; }
    /// <summary>Время последней завершённой попытки, включая нейтральный Deferred-результат.</summary>
    public DateTimeOffset? LastValidationAttemptAt { get; set; }
    /// <summary>Последняя попытка не смогла объективно оценить прокси и не изменила его качество.</summary>
    public bool LastValidationDeferred { get; set; }
    /// <summary>Момент следующей проверки; null означает немедленную проверку нового кандидата.</summary>
    public DateTimeOffset? NextCheckAt { get; set; }
    /// <summary>Короткая распределённая аренда, исключающая повторную проверку другим экземпляром сервиса.</summary>
    public DateTimeOffset? CheckLeaseUntil { get; set; }
    /// <summary>Точный токен владельца аренды; UUID не зависит от точности времени в разных СУБД.</summary>
    public Guid? CheckLeaseId { get; set; }
    /// <summary>Накопленное число объективных успешных проверок.</summary>
    public int SuccessfulChecks { get; set; }
    /// <summary>Накопленное число объективных неуспешных проверок.</summary>
    public int FailedChecks { get; set; }
    /// <summary>Текущая непрерывная серия объективных неуспешных проверок.</summary>
    public int ConsecutiveFailedChecks { get; set; }
    /// <summary>Безопасная bounded-диагностика последней неуспешной проверки.</summary>
    public string? LastError { get; set; }

    /// <summary>Стабильный нормализованный ключ для дедупликации.</summary>
    public string Key => $"{Protocol.ToString().ToLowerInvariant()}://{(Host.Contains(':') ? $"[{Host}]" : Host).ToLowerInvariant()}:{Port}";

    /// <summary>Доля успешных проверок от нуля до ста.</summary>
    public decimal SuccessRate => SuccessfulChecks + FailedChecks == 0
        ? 0
        : Math.Round(100m * SuccessfulChecks / (SuccessfulChecks + FailedChecks), 1);
}

/// <summary>Настраиваемый источник текстового списка прокси.</summary>
public sealed class ProxySource
{
    /// <summary>Стабильный идентификатор источника.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Отображаемое имя feed'а.</summary>
    public required string Name { get; set; }
    /// <summary>Канонический публичный HTTPS URL текстового feed'а.</summary>
    public required string Url { get; set; }
    /// <summary>Протокол для строк без явной схемы.</summary>
    public ProxyProtocol DefaultProtocol { get; set; }
    /// <summary>Включён ли источник в плановый и принудительный сбор.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Стабильный порядок обработки и отображения источников.</summary>
    public int Priority { get; set; } = 100;
    /// <summary>Время последней завершённой HTTP-попытки, успешной или нет.</summary>
    public DateTimeOffset? LastFetchedAt { get; set; }
    /// <summary>Время последнего успешного HTTP 2xx/подтверждённого 304 результата.</summary>
    public DateTimeOffset? LastSucceededAt { get; set; }
    /// <summary>
    /// Последний успешный ответ с полным body. В отличие от LastSucceededAt не меняется
    /// на 304 и гарантирует периодическое восстановление membership после retention.
    /// </summary>
    public DateTimeOffset? LastContentFetchedAt { get; set; }
    /// <summary>Неудачный источник не опрашивается background worker до этого момента; admin-аудит игнорирует паузу.</summary>
    public DateTimeOffset? NextFetchAt { get; set; }
    /// <summary>Последний серверный ETag для условной повторной загрузки неизменившегося feed'а.</summary>
    public string? HttpETag { get; set; }
    /// <summary>Последний Last-Modified для fallback conditional request при отсутствии ETag.</summary>
    public DateTimeOffset? HttpLastModifiedAt { get; set; }
    /// <summary>Число уникальных валидных endpoint'ов последнего успешного результата.</summary>
    public int LastItemCount { get; set; }
    /// <summary>Последний успешный ответ содержал больше уникальных адресов, чем разрешено сохранить из одного feed.</summary>
    public bool LastResultTruncated { get; set; }
    /// <summary>Число последовательных неуспешных попыток для расчёта backoff.</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>Безопасная bounded-диагностика последнего отказа источника.</summary>
    public string? LastError { get; set; }
}

/// <summary>Аудит одного цикла сбора источников.</summary>
public sealed class CollectionRun
{
    /// <summary>Идентификатор цикла сбора.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Начало цикла в UTC.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Завершение цикла; null означает активный либо аварийно прерванный run.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Число источников, для которых цикл завершил попытку обработки.</summary>
    public int SourcesProcessed { get; set; }
    /// <summary>Число источников с успешным непустым результатом.</summary>
    public int SourcesSucceeded { get; set; }
    /// <summary>Число источников с ошибкой загрузки или разбора.</summary>
    public int SourcesFailed { get; set; }
    /// <summary>Число пропущенных из-за расписания источников.</summary>
    public int SourcesSkipped { get; set; }
    /// <summary>Число успешно загруженных feed'ов, усечённых индивидуальным защитным лимитом.</summary>
    public int SourcesTruncated { get; set; }
    /// <summary>Число уникальных кандидатов после межисточниковой дедупликации.</summary>
    public int CandidatesFound { get; set; }
    /// <summary>Общий набор кандидатов достиг защитного лимита цикла и мог быть усечён.</summary>
    public bool CandidateLimitReached { get; set; }
    /// <summary>Число впервые сохранённых proxy rows.</summary>
    public int NewProxies { get; set; }
    /// <summary>Число подтверждённых Alive proxy после завершения цикла.</summary>
    public int AliveProxies { get; set; }
    /// <summary>Машиночитаемое состояние running/completed/failed.</summary>
    public string Status { get; set; } = "running";
    /// <summary>Bounded-диагностика отказа или null при полном успехе.</summary>
    public string? Error { get; set; }
}

/// <summary>Постоянный аудит одной арендованной validation-партии.</summary>
public sealed class ValidationRun
{
    /// <summary>Идентификатор validation-партии.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Токен связывает аудит с арендованными proxy rows и позволяет безопасно выявить crash.</summary>
    public Guid LeaseId { get; set; }
    /// <summary>Начало партии в UTC.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Завершение партии; null означает активный либо аварийно прерванный run.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Число строк, атомарно арендованных партией.</summary>
    public int Claimed { get; set; }
    /// <summary>Число полученных сетевых результатов, включая Deferred.</summary>
    public int Checked { get; set; }
    /// <summary>Число объективно подтверждённых Alive результатов.</summary>
    public int Alive { get; set; }
    /// <summary>Число нейтральных результатов из-за недоступной контрольной инфраструктуры.</summary>
    public int Deferred { get; set; }
    /// <summary>Машиночитаемое состояние running/completed/failed.</summary>
    public string Status { get; set; } = "running";
    /// <summary>Bounded-диагностика отказа или null при полном успехе.</summary>
    public string? Error { get; set; }
    /// <summary>Внешний checker-узел, выполнявший партию; null означает локальный validator.</summary>
    public Guid? CheckerNodeId { get; set; }
    /// <summary>Навигация к внешнему checker-узлу.</summary>
    public CheckerNode? CheckerNode { get; set; }
}

/// <summary>Подключённый внешний VPS, который арендует пакеты проверки у центрального сервиса.</summary>
public sealed class CheckerNode
{
    /// <summary>Стабильный идентификатор и часть заголовка аутентификации агента.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Понятное администратору уникальное имя узла.</summary>
    public required string Name { get; set; }
    /// <summary>Публичный IP VPS, использовавшийся при развёртывании.</summary>
    public required string Host { get; set; }
    /// <summary>Порт SSH, использующийся только для установки и удаления контейнера.</summary>
    public int SshPort { get; set; } = 22;
    /// <summary>Системный пользователь VPS; пароль намеренно не хранится.</summary>
    public required string SshUsername { get; set; }
    /// <summary>SHA-256 bearer-токена агента. Сам токен существует только на VPS.</summary>
    public required byte[] TokenHash { get; set; }
    /// <summary>TOFU SHA-256 fingerprint SSH host key для защиты повторного развёртывания.</summary>
    public string? HostKeyFingerprint { get; set; }
    /// <summary>Разрешено ли агенту получать и продлевать задания.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Максимальное число параллельных сетевых проб на этом VPS.</summary>
    public int Concurrency { get; set; } = 200;
    /// <summary>Максимальный размер одной атомарной аренды.</summary>
    public int BatchSize { get; set; } = 400;
    /// <summary>Время регистрации узла.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Время последнего изменения администратором или deploy-процессом.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Последний успешно принятый heartbeat.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    /// <summary>Последняя выдача непустого пакета.</summary>
    public DateTimeOffset? LastLeaseAt { get; set; }
    /// <summary>Последняя полностью сохранённая партия.</summary>
    public DateTimeOffset? LastCompletedAt { get; set; }
    /// <summary>Текущий точный token аренды либо null.</summary>
    public Guid? CurrentLeaseId { get; set; }
    /// <summary>Момент автоматического возврата незавершённой партии.</summary>
    public DateTimeOffset? CurrentLeaseUntil { get; set; }
    /// <summary>Версия подключённого контейнера.</summary>
    public string? AgentVersion { get; set; }
    /// <summary>Фактический адрес последнего HTTPS-запроса агента.</summary>
    public string? RemoteAddress { get; set; }
    /// <summary>Состояние provisioning: pending/deploying/starting/failed.</summary>
    public string DeploymentStatus { get; set; } = "pending";
    /// <summary>Последняя bounded-ошибка установки или работы.</summary>
    public string? LastError { get; set; }
    /// <summary>Общее число объективных проверок, сохранённых от узла.</summary>
    public long CompletedChecks { get; set; }
    /// <summary>Общее число подтверждённых узлом живых прокси.</summary>
    public long AliveChecks { get; set; }
    /// <summary>История распределённых validation-партий узла.</summary>
    public ICollection<ValidationRun> ValidationRuns { get; set; } = [];
}

/// <summary>Постоянный аудит создания и внешней доставки резервной копии.</summary>
public sealed class BackupRun
{
    /// <summary>Идентификатор попытки резервного копирования.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Начало попытки в UTC.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Завершение попытки; null означает активный либо аварийно прерванный run.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Машиночитаемое состояние running/completed/failed.</summary>
    public string Status { get; set; } = "running";
    /// <summary>Имя опубликованного локального PHB3 без пути.</summary>
    public string? FileName { get; set; }
    /// <summary>Размер опубликованного ciphertext в байтах.</summary>
    public long SizeBytes { get; set; }
    /// <summary>Для попытки были заданы одновременно Telegram token и chat ID.</summary>
    public bool TelegramConfigured { get; set; }
    /// <summary>Telegram подтвердил ok=true для файла или каждой его части.</summary>
    public bool SentToTelegram { get; set; }
    /// <summary>Для попытки было включено и полностью настроено S3-совместимое хранилище.</summary>
    public bool ObjectStorageConfigured { get; set; }
    /// <summary>Object storage подтвердил размер и SHA-256 загруженного PHB3.</summary>
    public bool SentToObjectStorage { get; set; }
    /// <summary>Безопасный object key без bucket, endpoint и реквизитов.</summary>
    public string? ObjectStorageKey { get; set; }
    /// <summary>Bounded-диагностика отказа или null при полном успехе.</summary>
    public string? Error { get; set; }
}

/// <summary>Результат сетевой проверки, не привязанный к слою хранения.</summary>
public sealed record ProxyCheckResult(
    Guid ProxyId,
    bool IsAlive,
    int? LatencyMs,
    string? ExitIp,
    bool IsAnonymous,
    string? Error,
    // Контрольная инфраструктура не позволила оценить прокси; статистика качества не меняется.
    bool IsDeferred = false);

/// <summary>Унифицированный ответ со страницей данных и сведениями о тарифном ограничении.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    /// <summary>Явное право текущего клиента обходить весь каталог страницами.</summary>
    public bool FullAccess { get; init; } = true;
    /// <summary>Количество записей, доступных текущему тарифу; null означает отсутствие отдельного лимита.</summary>
    public int? Accessible { get; init; }
    /// <summary>Признак того, что Total больше доступной текущему клиенту выборки.</summary>
    public bool Limited { get; init; }
    /// <summary>Локализованное объяснение ограничения.</summary>
    public string? Message { get; init; }
    /// <summary>Ссылка на оформление подписки.</summary>
    public string? UpgradeUrl { get; init; }
}
