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

/// <summary>Унифицированный ответ со страницей данных.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
