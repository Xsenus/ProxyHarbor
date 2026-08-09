namespace ProxyHarbor.Domain;

/// <summary>Поддерживаемые транспортные протоколы публичных прокси.</summary>
public enum ProxyProtocol { Http, Https, Socks4, Socks5 }

/// <summary>Текущее состояние прокси по результатам последней проверки.</summary>
public enum ProxyStatus { Pending, Alive, Dead }

/// <summary>Публичный прокси-сервер и накопленная статистика его качества.</summary>
public sealed class ProxyEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Host { get; set; }
    public int Port { get; set; }
    public ProxyProtocol Protocol { get; set; }
    public ProxyStatus Status { get; set; } = ProxyStatus.Pending;
    public int? LatencyMs { get; set; }
    public string? ExitIp { get; set; }
    public string? CountryCode { get; set; }
    public bool IsAnonymous { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAt { get; set; }
    /// <summary>Момент следующей проверки; null означает немедленную проверку нового кандидата.</summary>
    public DateTimeOffset? NextCheckAt { get; set; }
    /// <summary>Короткая распределённая аренда, исключающая повторную проверку другим экземпляром сервиса.</summary>
    public DateTimeOffset? CheckLeaseUntil { get; set; }
    /// <summary>Точный токен владельца аренды; UUID не зависит от точности времени в разных СУБД.</summary>
    public Guid? CheckLeaseId { get; set; }
    public int SuccessfulChecks { get; set; }
    public int FailedChecks { get; set; }
    public int ConsecutiveFailedChecks { get; set; }
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
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Url { get; set; }
    public ProxyProtocol DefaultProtocol { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public DateTimeOffset? LastFetchedAt { get; set; }
    public DateTimeOffset? LastSucceededAt { get; set; }
    /// <summary>Неудачный источник не опрашивается background worker до этого момента; admin-аудит игнорирует паузу.</summary>
    public DateTimeOffset? NextFetchAt { get; set; }
    public int LastItemCount { get; set; }
    /// <summary>Последний успешный ответ содержал больше уникальных адресов, чем разрешено сохранить из одного feed.</summary>
    public bool LastResultTruncated { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Аудит одного цикла сбора и проверки.</summary>
public sealed class CollectionRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int SourcesProcessed { get; set; }
    public int SourcesSucceeded { get; set; }
    public int SourcesFailed { get; set; }
    public int SourcesSkipped { get; set; }
    /// <summary>Число успешно загруженных feed'ов, усечённых индивидуальным защитным лимитом.</summary>
    public int SourcesTruncated { get; set; }
    public int CandidatesFound { get; set; }
    /// <summary>Общий набор кандидатов достиг защитного лимита цикла и мог быть усечён.</summary>
    public bool CandidateLimitReached { get; set; }
    public int NewProxies { get; set; }
    public int AliveProxies { get; set; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
}

/// <summary>Постоянный аудит создания и внешней доставки резервной копии.</summary>
public sealed class BackupRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public bool TelegramConfigured { get; set; }
    public bool SentToTelegram { get; set; }
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
