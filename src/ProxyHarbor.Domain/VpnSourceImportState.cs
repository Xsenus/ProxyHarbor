namespace ProxyHarbor.Domain;

/// <summary>Возобновляемый снимок VPN feed; курсор и payload очищаются при portable restore.</summary>
public sealed class VpnSourceImportState
{
    /// <summary>PK и cascade FK к публичному источнику.</summary>
    public Guid VpnSourceId { get; set; }
    /// <summary>URL конфигурации, для которой получен снимок.</summary>
    public required string SourceUrl { get; set; }
    /// <summary>Fallback протокол конфигурации.</summary>
    public VpnProtocol SourceProtocol { get; set; }
    /// <summary>Immutable generation token для CAS подтверждения.</summary>
    public Guid SnapshotId { get; set; } = Guid.NewGuid();
    /// <summary>Фактическое время наблюдения исходного body; не время delayed import.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Последнее продвижение, подтверждённое в import transaction.</summary>
    public DateTimeOffset? LastProgressAt { get; set; }
    /// <summary>Число уникальных endpoint в полном каноническом индексе.</summary>
    public int CandidateCount { get; set; }
    /// <summary>Первый unique endpoint, ещё не подтверждённый import commit.</summary>
    public int NextIndex { get; set; }
    /// <summary>Original physical records, including all settings for a shared endpoint.</summary>
    public int ProfileRecordCount { get; set; }
    /// <summary>First original record not yet saved to the durable profile catalog.</summary>
    public int ProfileNextIndex { get; set; }
    /// <summary>Compressed pages и индекс последних URI.</summary>
    public byte[] Payload { get; set; } = [];
    /// <summary>SHA-256 pending payload; очищается после полного импорта.</summary>
    public byte[] PayloadHash { get; set; } = [];
    /// <summary>Неизменяемый SHA-256 body исходного снимка.</summary>
    public byte[] SnapshotBodyHash { get; set; } = [];
    /// <summary>SHA-256 последнего принятого свежего окна.</summary>
    public byte[] FreshBodyHash { get; set; } = [];
    /// <summary>Чередование fresh/tail lanes при минимальной квоте.</summary>
    public bool PreferFresh { get; set; } = true;
    /// <summary>PostgreSQL computed size для общего VPN storage-бюджета.</summary>
    public int StoredBytes { get; private set; }
}
