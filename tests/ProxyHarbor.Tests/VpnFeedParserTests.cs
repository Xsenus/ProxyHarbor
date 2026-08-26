using System.Text;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет извлечение endpoint и обязательное отбрасывание секретных частей конфигураций.</summary>
public sealed class VpnFeedParserTests
{
    [Fact]
    public void ParsesUrisAndDoesNotExposeCredentials()
    {
        const string content = "vless://secret-uuid@1.1.1.1:443?security=tls#name\n" +
            "trojan://secret-password@8.8.8.8:8443?sni=example.org";

        var candidates = VpnFeedParser.Parse(content, VpnProtocol.Vless);

        Assert.Collection(candidates.OrderBy(x => x.Host),
            item => { Assert.Equal("1.1.1.1", item.Host); Assert.Equal(443, item.Port); Assert.Equal(VpnProtocol.Vless, item.Protocol); },
            item => { Assert.Equal("8.8.8.8", item.Host); Assert.Equal(8443, item.Port); Assert.Equal(VpnProtocol.Trojan, item.Protocol); });
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

        Assert.Equal(new VpnCandidate("9.9.9.9", 443, VpnProtocol.Vmess, "tcp"), vmessCandidate);
        Assert.Equal(new VpnCandidate("1.0.0.1", 1194, VpnProtocol.OpenVpn, "udp"), openVpnCandidate);
    }

    [Theory]
    [InlineData("vless://id@127.0.0.1:443")]
    [InlineData("trojan://password@10.0.0.1:443")]
    [InlineData("wireguard://key@[::1]:51820")]
    public void RejectsPrivateAndLoopbackEndpoints(string value) =>
        Assert.Empty(VpnFeedParser.Parse(value, VpnProtocol.Vless));
}
