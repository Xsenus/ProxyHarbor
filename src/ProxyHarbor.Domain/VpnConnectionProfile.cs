namespace ProxyHarbor.Domain;

/// <summary>Published connection settings retained independently of the endpoint's preferred settings.</summary>
public sealed class VpnConnectionProfile
{
    /// <summary>Publisher provenance; deleting a source removes only its own profile observations.</summary>
    public Guid VpnSourceId { get; set; }
    /// <summary>SHA-256 of the complete endpoint identity and original URI/configuration.</summary>
    public required string ProfileHash { get; set; }
    /// <summary>Canonical public IP or DNS name.</summary>
    public required string Host { get; set; }
    /// <summary>Published port.</summary>
    public int Port { get; set; }
    /// <summary>Published VPN protocol.</summary>
    public VpnProtocol Protocol { get; set; }
    /// <summary>Published TCP or UDP transport.</summary>
    public required string Transport { get; set; }
    /// <summary>Original public connection URI, including display fragments and credentials.</summary>
    public string? ConnectionUri { get; set; }
    /// <summary>Original standalone configuration, including its required static dependencies.</summary>
    public string? ClashConfiguration { get; set; }
    /// <summary>Earliest observation in a source body.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }
    /// <summary>Latest observation in a source body; collection time cannot promote an old snapshot.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}
