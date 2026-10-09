using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnConnectionProfileIntegrityTests
{
    [Fact]
    public void SettingsIdentityRetainsCredentialsTlsDnsDisplayAndStandaloneConfiguration()
    {
        var original = new VpnCandidate("example.com", 443, VpnProtocol.Vless, "tcp",
            "vless://first@example.com:443?security=tls&sni=example.com#published");
        var variants = new[]
        {
            original,
            original with { ConnectionUri = original.ConnectionUri!.Replace("first", "second", StringComparison.Ordinal) },
            original with { ConnectionUri = original.ConnectionUri!.Replace("security=tls", "security=none", StringComparison.Ordinal) },
            original with { ConnectionUri = original.ConnectionUri!.Replace("sni=example.com", "sni=cdn.example.com", StringComparison.Ordinal) },
            original with { ConnectionUri = original.ConnectionUri!.Replace("published", "другое имя", StringComparison.Ordinal) },
            original with { ClashConfiguration = "proxies: [{name: node, type: vless, server: example.com, port: 443, uuid: first}]" },
            original with { Host = "cdn.example.com" },
            original with { Port = 8443 },
            original with { Transport = "udp" },
            original with { Protocol = VpnProtocol.Trojan }
        };
        Assert.Equal(variants.Length, variants.Select(VpnConnectionProfileIntegrity.ComputeHash).Distinct().Count());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("hash")]
    [InlineData("host")]
    [InlineData("uri-private")]
    [InlineData("uri-different")]
    [InlineData("settings")]
    [InlineData("timeline")]
    [InlineData("null-host")]
    [InlineData("port")]
    public void RestoreRejectsMismatchedOrUnsafeProfilesEvenWithARecomputedHash(string damage)
    {
        var profile = Profile();
        switch (damage)
        {
            case "source": profile.VpnSourceId = Guid.Empty; break;
            case "hash": profile.ProfileHash = new string('0', 64); break;
            case "host": profile.Host = "other.example.com"; break;
            case "uri-private": profile.ConnectionUri = "vless://id@127.0.0.1:443"; break;
            case "uri-different": profile.ConnectionUri = "vless://id@other.example.com:443"; break;
            case "settings": profile.ConnectionUri = null; break;
            case "timeline": profile.LastSeenAt = profile.FirstSeenAt.AddSeconds(-1); break;
            case "null-host": profile.Host = null!; break;
            case "port": profile.Port = 0; break;
        }
        if (damage != "hash") profile.ProfileHash = VpnConnectionProfileIntegrity.ComputeHash(new VpnCandidate(
            profile.Host, profile.Port, profile.Protocol, profile.Transport, profile.ConnectionUri));
        Assert.Throws<InvalidDataException>(() => VpnConnectionProfileIntegrity.Validate(profile));
    }

    [Fact]
    public void RestoreAcceptsOriginalUriAndCompleteStandaloneSettings()
    {
        VpnConnectionProfileIntegrity.Validate(Profile());
        var candidate = Assert.Single(VpnFeedParser.Parse(
            "proxies: [{name: node, type: trojan, server: example.com, port: 443, password: original, sni: cdn.example.com, skip-cert-verify: true}]",
            VpnProtocol.Trojan));
        var profile = Profile();
        profile.Protocol = candidate.Protocol;
        profile.ConnectionUri = candidate.ConnectionUri;
        profile.ClashConfiguration = candidate.ClashConfiguration;
        profile.ProfileHash = VpnConnectionProfileIntegrity.ComputeHash(candidate);
        VpnConnectionProfileIntegrity.Validate(profile);
    }

    private static VpnConnectionProfile Profile()
    {
        var candidate = new VpnCandidate("example.com", 443, VpnProtocol.Vless, "tcp", "vless://id@example.com:443#published");
        return new VpnConnectionProfile
        {
            VpnSourceId = Guid.NewGuid(),
            ProfileHash = VpnConnectionProfileIntegrity.ComputeHash(candidate),
            Host = candidate.Host,
            Port = candidate.Port,
            Protocol = candidate.Protocol,
            Transport = candidate.Transport,
            ConnectionUri = candidate.ConnectionUri,
            FirstSeenAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastSeenAt = DateTimeOffset.UtcNow
        };
    }
}
