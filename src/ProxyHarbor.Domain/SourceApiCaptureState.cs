namespace ProxyHarbor.Domain;

/// <summary>Operational API page checkpoint, excluded from portable backups.</summary>
public sealed class SourceApiCaptureState
{
    /// <summary>Stable identity of one capture generation.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Optional ordinary-source owner; exactly one owner is required.</summary>
    public Guid? ProxySourceId { get; set; }
    /// <summary>Optional VPN-source owner; exactly one owner is required.</summary>
    public Guid? VpnSourceId { get; set; }
    /// <summary>Exact public URL configuration for this capture.</summary>
    public required string SourceUrl { get; set; }
    /// <summary>Protocol configuration, compared before accepting any page.</summary>
    public int SourceProtocol { get; set; }
    /// <summary>Compare-and-swap token, replaced after every committed checkpoint.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();
    /// <summary>Compressed independently validated pages and observation times.</summary>
    public byte[] Payload { get; set; } = [];
    /// <summary>SHA-256 of the complete compressed checkpoint.</summary>
    public byte[] PayloadHash { get; set; } = [];
    /// <summary>Full traversal and head reconciliation are complete.</summary>
    public bool Complete { get; set; }
    /// <summary>Last checkpoint commit, separate from source HTTP health.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Computed cost used in atomic global storage admission.</summary>
    public int StoredBytes { get; private set; }
}

/// <summary>Persistent per-origin request cooldown shared by proxy and VPN workers.</summary>
public sealed class SourceApiOriginState
{
    /// <summary>Canonical HTTPS origin without path or credentials.</summary>
    public required string Origin { get; set; }
    /// <summary>Earliest permitted request, including provider Retry-After and local pacing.</summary>
    public DateTimeOffset NotBefore { get; set; }
}
