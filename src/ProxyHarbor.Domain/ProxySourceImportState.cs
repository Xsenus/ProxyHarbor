namespace ProxyHarbor.Domain;

/// <summary>Возобновляемый импорт одного неизменяемого снимка; не переносится в portable backup.</summary>
public sealed class ProxySourceImportState
{
    /// <summary>PK и cascade FK к источнику; не более одного снимка на source.</summary>
    public Guid ProxySourceId { get; set; }
    /// <summary>Конфигурация URL, для которой был получен снимок.</summary>
    public required string SourceUrl { get; set; }
    /// <summary>Fallback протокол конфигурации снимка.</summary>
    public ProxyProtocol SourceProtocol { get; set; }
    /// <summary>Generation token исключает подтверждение прогресса другого снимка.</summary>
    public Guid SnapshotId { get; set; } = Guid.NewGuid();
    /// <summary>Время получения неизменяемого набора кандидатов.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Последнее продвижение после успешного bulk commit; используется для fairness.</summary>
    public DateTimeOffset? LastProgressAt { get; set; }
    /// <summary>Количество уникальных кандидатов во всём снимке.</summary>
    public int CandidateCount { get; set; }
    /// <summary>Первый кандидат, ещё не подтверждённый успешным импортом.</summary>
    public int NextIndex { get; set; }
    /// <summary>Сжатые страницы канонических IP/port/protocol без provider key и diagnostic metadata.</summary>
    public byte[] Payload { get; set; } = [];
    /// <summary>SHA-256 pending payload; очищается после завершения снимка.</summary>
    public byte[] PayloadHash { get; set; } = [];
    /// <summary>Вычисляемый PostgreSQL размер для общего bounded-бюджета хранилища.</summary>
    public int StoredBytes { get; private set; }
}
