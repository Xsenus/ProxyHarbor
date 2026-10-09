using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Shared identity and restore validation for original, publicly published VPN settings.</summary>
public static class VpnConnectionProfileIntegrity
{
    /// <summary>Hashes the exact settings without dropping credentials, fragments, DNS or TLS options.</summary>
    public static string ComputeHash(VpnCandidate candidate) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            candidate.Host, candidate.Port, (int)candidate.Protocol, candidate.Transport,
            candidate.ConnectionUri, candidate.ClashConfiguration
        })));

    /// <summary>Rejects a malformed or mismatched profile before a restore can persist it.</summary>
    public static void Validate(VpnConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var candidate = new VpnCandidate(profile.Host, profile.Port, profile.Protocol, profile.Transport, profile.ConnectionUri)
        { ClashConfiguration = profile.ClashConfiguration };
        if (profile.VpnSourceId == Guid.Empty || profile.FirstSeenAt == default || profile.LastSeenAt < profile.FirstSeenAt ||
            profile.Host is null || profile.Host.Any(char.IsUpper) || !VpnFeedParser.IsSafe(candidate) ||
            profile.ConnectionUri?.Length > 16_384 || profile.ClashConfiguration?.Length > 16_384 ||
            profile.ConnectionUri is null && profile.ClashConfiguration is null)
            throw InvalidProfile();
        if (profile.ConnectionUri is not null)
        {
            var parsed = VpnFeedParser.Parse(profile.ConnectionUri, profile.Protocol, 1);
            if (parsed.Count != 1 || parsed[0] != candidate with { ClashConfiguration = null }) throw InvalidProfile();
        }
        if (profile.ClashConfiguration is not null && !ClashYamlFeedParser.IsValidStandalone(candidate)) throw InvalidProfile();
        if (!string.Equals(profile.ProfileHash, ComputeHash(candidate), StringComparison.Ordinal)) throw InvalidProfile();
    }

    private static InvalidDataException InvalidProfile() => new("Invalid VPN connection profile or settings provenance.");
}
