using System.Text;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет endpoint и сохранение готовых URI для публичной выдачи.</summary>
public sealed class VpnFeedParserTests
{
    [Fact]
    public void ParseStopsAtConfiguredResultLimit()
    {
        var content = string.Join('\n', Enumerable.Range(1, 100)
            .Select(index => $"vless://id@8.8.8.{index % 250 + 1}:443?tag={index}"));

        var candidates = VpnFeedParser.Parse(content, VpnProtocol.Vless, 7);

        Assert.Equal(7, candidates.Count);
    }

    [Fact]
    public void ParsesUrisAndPreservesReadyConnectionLinks()
    {
        const string content = "vless://secret-uuid@1.1.1.1:443?security=tls#name\n" +
            "trojan://secret-password@8.8.8.8:8443?sni=example.org";

        var candidates = VpnFeedParser.Parse(content, VpnProtocol.Vless);

        Assert.Collection(candidates.OrderBy(x => x.Host),
            item => { Assert.Equal("1.1.1.1", item.Host); Assert.Equal(443, item.Port); Assert.Equal(VpnProtocol.Vless, item.Protocol); Assert.StartsWith("vless://", item.ConnectionUri); },
            item => { Assert.Equal("8.8.8.8", item.Host); Assert.Equal(8443, item.Port); Assert.Equal(VpnProtocol.Trojan, item.Protocol); Assert.StartsWith("trojan://", item.ConnectionUri); });
        Assert.DoesNotContain(candidates, item => item.Host.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesVmessAndOpenVpn()
    {
        var vmessJson = "{\"add\":\"9.9.9.9\",\"port\":\"443\",\"id\":\"must-not-survive\"}";
        var vmess = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmessJson));
        var openVpn = "client\nproto udp\nremote 1.0.0.1 1194 udp\n";

        var vmessCandidate = Assert.Single(VpnFeedParser.Parse(vmess, VpnProtocol.Vmess));
        var openVpnCandidate = Assert.Single(VpnFeedParser.Parse(openVpn, VpnProtocol.OpenVpn));

        Assert.Equal(new VpnCandidate("9.9.9.9", 443, VpnProtocol.Vmess, "tcp", vmess), vmessCandidate);
        Assert.Equal(new VpnCandidate("1.0.0.1", 1194, VpnProtocol.OpenVpn, "udp"), openVpnCandidate);
    }

    [Theory]
    [InlineData("vless://id@127.0.0.1:443")]
    [InlineData("trojan://password@10.0.0.1:443")]
    [InlineData("wireguard://key@[::1]:51820")]
    public void RejectsPrivateAndLoopbackEndpoints(string value) =>
        Assert.Empty(VpnFeedParser.Parse(value, VpnProtocol.Vless));

    [Fact]
    public void ParsesEverySupportedUriAndNormalizesDuplicates()
    {
        const string content = "vless://id@Example.COM:443\n" +
            "vless://other@example.com:443#duplicate\n" +
            "ss://secret@1.1.1.1:8388\n" +
            "hy2://secret@8.8.8.8:8443\n" +
            "hysteria2://secret@9.9.9.9:9443\n" +
            "tuic://secret@1.0.0.1:10443\n" +
            "wg://secret@208.67.222.222:51820";

        var candidates = VpnFeedParser.Parse(content, VpnProtocol.Vless);

        Assert.Equal(6, candidates.Count);
        Assert.Contains(candidates, x => x == new VpnCandidate("example.com", 443, VpnProtocol.Vless, "tcp", x.ConnectionUri) && x.ConnectionUri!.StartsWith("vless://", StringComparison.Ordinal));
        Assert.Contains(candidates, x => x == new VpnCandidate("1.1.1.1", 8388, VpnProtocol.Shadowsocks, "tcp", x.ConnectionUri) && x.ConnectionUri!.StartsWith("ss://", StringComparison.Ordinal));
        Assert.Contains(candidates, x => x.Protocol == VpnProtocol.Hysteria2 && x.Transport == "udp");
        Assert.Contains(candidates, x => x.Protocol == VpnProtocol.Tuic && x.Transport == "udp");
        Assert.Contains(candidates, x => x.Protocol == VpnProtocol.WireGuard && x.Transport == "udp");
    }

    [Fact]
    public void ParsesWireGuardIniAndOpenVpnTcp()
    {
        const string wireGuard = "[Interface]\nPrivateKey = never-store\n[Peer]\nEndpoint = 8.8.4.4:51820\nEndpoint = invalid";
        const string openVpn = "remote 1.1.1.1 443 tcp-client\nremote invalid not-a-port\nremote\n";

        Assert.Equal(new VpnCandidate("8.8.4.4", 51820, VpnProtocol.WireGuard, "udp"),
            Assert.Single(VpnFeedParser.Parse(wireGuard, VpnProtocol.WireGuard)));
        Assert.Equal(new VpnCandidate("1.1.1.1", 443, VpnProtocol.OpenVpn, "tcp"),
            Assert.Single(VpnFeedParser.Parse(openVpn, VpnProtocol.OpenVpn)));
    }

    [Fact]
    public void ParsesWholeBase64FeedAndUrlSafeVmess()
    {
        var encodedFeed = Convert.ToBase64String(Encoding.UTF8.GetBytes("vless://id@8.8.8.8:443"));
        var json = "{\"add\":\"one.one.one.one\",\"port\":443}";
        var urlSafe = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Single(VpnFeedParser.Parse(encodedFeed, VpnProtocol.Vless));
        Assert.Equal(443, Assert.Single(VpnFeedParser.Parse("vmess://" + urlSafe, VpnProtocol.Vmess)).Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("# comment only")]
    [InlineData("not a supported value")]
    [InlineData("vless://id@1.1.1.1:0")]
    [InlineData("vless://id@1.1.1.1:65536")]
    [InlineData("vmess://not-base64!")]
    [InlineData("vmess://e25vdC1qc29ufQ==")]
    [InlineData("vmess://eyJhZGQiOiIxLjEuMS4xIn0=")]
    [InlineData("vmess://eyJhZGQiOiIxLjEuMS4xIiwicG9ydCI6Im5vcGUifQ==")]
    public void IgnoresMalformedInput(string content) =>
        Assert.Empty(VpnFeedParser.Parse(content, VpnProtocol.Vless));

    [Theory]
    [InlineData("vless://id@localhost:443")]
    [InlineData("vless://id@bad_host:443")]
    [InlineData("vless://id@169.254.1.1:443")]
    [InlineData("vless://id@224.0.0.1:443")]
    public void RejectsUnsafeHostNamesAndAddresses(string content) =>
        Assert.Empty(VpnFeedParser.Parse(content, VpnProtocol.Vless));

    [Fact]
    public void IgnoresOversizedLinesAndKeepsValidNeighbors()
    {
        var oversized = new string('x', 16_385);
        var candidates = VpnFeedParser.Parse($"{oversized}\n\"vless://id@8.8.8.8:443\"", VpnProtocol.Vless);

        Assert.Single(candidates);
    }
}

/// <summary>Защищает происхождение и целостность встроенного каталога VPN feed.</summary>
public sealed class BuiltInVpnSourceCatalogTests
{
    [Fact]
    public void SourcesAreUniqueHttpsLicensedAndCoverSupportedFamilies()
    {
        var sources = BuiltInVpnSourceCatalog.Sources;

        Assert.Equal(116, sources.Count);
        Assert.Equal(new DateOnly(2026, 8, 28), BuiltInVpnSourceCatalog.LastAuditedOn);
        Assert.Equal(sources.Count, sources.Select(x => x.Url).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(9, sources.Select(x => x.Provider).Distinct(StringComparer.Ordinal).Count());
        Assert.All(sources, source =>
        {
            Assert.StartsWith("https://", source.Url, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(source.Name));
            Assert.False(string.IsNullOrWhiteSpace(source.Provider));
            Assert.False(string.IsNullOrWhiteSpace(source.License));
        });
        Assert.Contains(sources, x => x.Protocol == VpnProtocol.OpenVpn);
        Assert.Contains(sources, x => x.Protocol == VpnProtocol.WireGuard);
        Assert.Contains(sources, x => x.Protocol == VpnProtocol.Vless);
        Assert.Contains(sources, x => x.Protocol == VpnProtocol.Vmess);
        Assert.Contains(sources, x => x.Protocol == VpnProtocol.Trojan);
        Assert.Contains(sources, x => x.Protocol == VpnProtocol.Shadowsocks);
        Assert.Contains(sources, x => x.Protocol == VpnProtocol.Hysteria2);
        Assert.Contains(sources, x => x.Protocol == VpnProtocol.Tuic);
    }
}
