using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using YamlDotNet.RepresentationModel;

namespace ProxyHarbor.Tests;

public sealed class ClashYamlFeedParserTests
{
    [Theory]
    [InlineData("include-all: false")]
    [InlineData("include-all-proxies: false")]
    [InlineData("include-all-providers: false")]
    [InlineData("use: []")]
    public void ExplicitlyDisabledDynamicSettingsPreserveStaticGroupDependencies(string setting)
    {
        var yaml = "proxies: [{name: node, type: http, server: 8.8.8.8, port: 80, dialer-proxy: route}]\nproxy-groups: [{name: route, type: select, proxies: [DIRECT], " + setting + "}]";
        var candidate = Assert.Single(VpnFeedParser.Parse(yaml, VpnProtocol.Vless));
        Assert.Contains(setting, candidate.ClashConfiguration);
        Assert.True(ClashYamlFeedParser.IsValidStandalone(candidate));
    }

    [Theory]
    [InlineData("include-all: true")]
    [InlineData("include-all-proxies: invalid")]
    [InlineData("include-all-providers: [false]")]
    [InlineData("use: [remote]")]
    public void ActiveOrMalformedDynamicSettingsCannotBecomeAStandaloneGroup(string setting)
    {
        var yaml = "proxies: [{name: node, type: http, server: 8.8.8.8, port: 80, dialer-proxy: route}]\nproxy-groups: [{name: route, type: select, proxies: [DIRECT], " + setting + "}]";
        Assert.Throws<InvalidDataException>(() => VpnFeedParser.Parse(yaml, VpnProtocol.Vless));
    }

    [Theory]
    [InlineData("proxies: [{type: http, server: 8.8.8.8, port: 80, password: '")]
    [InlineData("vless://id@8.8.8.8:443#")]
    public void WholeSubscriptionBase64RejectsInvalidUtf8WithoutChangingPublishedSettings(string prefix)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(prefix).Concat(new byte[] { 0xff }).Concat(System.Text.Encoding.UTF8.GetBytes("'}]")).ToArray();
        var encoded = Convert.ToBase64String(bytes);
        Assert.Empty(VpnFeedParser.Parse(encoded, VpnProtocol.Vless));
        Assert.Throws<InvalidDataException>(() => VpnCandidateSnapshotCodec.Encode(encoded, VpnProtocol.Vless));
    }

    [Fact]
    public void WholeSubscriptionBase64PreservesLegitimateReplacementCharacter()
    {
        const string yaml = "proxies: [{type: http, server: 8.8.8.8, port: 80, password: '�'}]";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(yaml));
        var candidate = Assert.Single(VpnFeedParser.Parse(encoded, VpnProtocol.Vless));
        Assert.Contains("�", candidate.ClashConfiguration);
    }

    [Theory]
    [InlineData("\uFEFFproxies: [{type: http, server: 8.8.8.8, port: 80}]")]
    [InlineData("'proxies': [{type: http, server: 8.8.8.8, port: 80}]")]
    [InlineData("{mode: rule, proxies: [{type: http, server: 8.8.8.8, port: 80}]}")]
    [InlineData("{\"mode\":\"rule\",\"proxies\":[{\"type\":\"http\",\"server\":\"8.8.8.8\",\"port\":80}]}")]
    public void FeedDispatchRecognizesBlockFlowJsonAndBomDocuments(string yaml)
    {
        Assert.True(ClashYamlFeedParser.LooksLike(yaml));
        var candidate = Assert.Single(VpnFeedParser.Parse(yaml, VpnProtocol.Vless));
        Assert.Equal(VpnProtocol.HttpProxy, candidate.Protocol);
        Assert.True(ClashYamlFeedParser.IsValidStandalone(candidate));
        Assert.Single(VpnFeedParser.Parse(yaml, VpnProtocol.MtProto));
    }

    [Theory]
    [InlineData("{\"proxies\":[\"tg://proxy?server=8.8.8.8&port=443&secret=0123456789abcdef0123456789abcdef\"]}")]
    [InlineData("{\"proxies\":[{\"server\":\"8.8.8.8\",\"port\":443,\"secret\":\"0123456789abcdef0123456789abcdef\"}]}")]
    [InlineData("{\n  \"proxies\": [\n    {\"server\":\"8.8.8.8\",\"port\":443,\"secret\":\"0123456789abcdef0123456789abcdef\"}\n  ]\n}")]
    public void TelegramJsonProxiesArraysRemainWithTheirExistingParser(string json)
    {
        Assert.False(ClashYamlFeedParser.LooksLike(json));
        var candidate = Assert.Single(VpnFeedParser.Parse(json, VpnProtocol.MtProto));
        Assert.Equal(VpnProtocol.MtProto, candidate.Protocol);
        Assert.Null(candidate.ClashConfiguration);
        Assert.NotNull(candidate.ConnectionUri);
    }

    [Fact]
    public void FeedDispatchStopsAtRequestedUniqueLimitAndDoesNotScanIncidentalUris()
    {
        const string yaml = "proxies: [{type: http, server: 8.8.8.8, port: 80}, {type: http, server: 1.1.1.1, port: 81}]\nlog: 'vless://incidental@9.9.9.9:443'";
        Assert.Single(VpnFeedParser.Parse(yaml, VpnProtocol.Vless, 1));
        Assert.Equal(2, VpnFeedParser.Parse(yaml, VpnProtocol.Vless).Count);
    }

    [Theory]
    [InlineData("vless", VpnProtocol.Vless, "tcp")]
    [InlineData("vmess", VpnProtocol.Vmess, "tcp")]
    [InlineData("trojan", VpnProtocol.Trojan, "tcp")]
    [InlineData("ss", VpnProtocol.Shadowsocks, "tcp")]
    [InlineData("ssr", VpnProtocol.ShadowsocksR, "tcp")]
    [InlineData("hysteria", VpnProtocol.Hysteria, "udp")]
    [InlineData("hysteria2", VpnProtocol.Hysteria2, "udp")]
    [InlineData("tuic", VpnProtocol.Tuic, "udp")]
    [InlineData("wireguard", VpnProtocol.WireGuard, "udp")]
    [InlineData("anytls", VpnProtocol.AnyTls, "tcp")]
    [InlineData("http", VpnProtocol.HttpProxy, "tcp")]
    [InlineData("socks4", VpnProtocol.Socks4Proxy, "tcp")]
    [InlineData("socks5", VpnProtocol.Socks5Proxy, "tcp")]
    public void PreservesPublishedConfigurationWithoutInventingAUri(string type, VpnProtocol protocol, string transport)
    {
        var yaml = $$$"""
            proxies:
              - name: public-node
                type: {{{type}}}
                server: EXAMPLE.COM
                port: 443
                password: "0012"
                cipher: aes-128-gcm
                username: 'name:@'
                uuid: 00112233-4455-6677-8899-aabbccddeeff
                tls: true
                skip-cert-verify: false
                alpn: [h2, http/1.1]
                reality-opts: {public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: '0001'}
                ws-opts: {path: '/socket?ed=2048', headers: {Host: example.org}}
                future-option: {values: [a, b]}
            """;
        var candidates = Parse(yaml);
        var candidate = Assert.Single(candidates);
        Assert.Equal("example.com", candidate.Host);
        Assert.Equal(443, candidate.Port);
        Assert.Equal(protocol, candidate.Protocol);
        Assert.Equal(transport, candidate.Transport);
        Assert.Null(candidate.ConnectionUri);
        var original = Proxy(ClashYamlFeedReader.ReadRequired(yaml));
        var exported = Proxy(ClashYamlFeedReader.ReadRequired(Assert.IsType<string>(candidate.ClashConfiguration)));
        Assert.Equal(original, exported);
    }

    [Fact]
    public void PreservesAliasSettingsInStandaloneDocument()
    {
        const string yaml = "options: &opts {sni: example.org, alpn: [h2, http/1.1]}\nproxies: [{type: trojan, server: example.com, port: 443, tls-options: *opts}]";
        var candidate = Assert.Single(Parse(yaml));
        var proxy = Proxy(ClashYamlFeedReader.ReadRequired(candidate.ClashConfiguration!));
        var options = Assert.IsType<YamlMappingNode>(proxy.Children[new YamlScalarNode("tls-options")]);
        Assert.Equal("example.org", Assert.IsType<YamlScalarNode>(options.Children[new YamlScalarNode("sni")]).Value);
    }

    [Fact]
    public void ReadsYamlMergePrecedenceAndKeepsMergedSettings()
    {
        const string yaml = "first: &a {type: vless, server: example.com, port: 80, tls: true}\nsecond: &b {server: other.example, port: 81}\nproxies: [{<<: [*a, *b], port: 443}]";
        var candidate = Assert.Single(Parse(yaml));
        Assert.Equal("example.com", candidate.Host);
        Assert.Equal(443, candidate.Port);
        Assert.Equal(VpnProtocol.Vless, candidate.Protocol);
        var replayed = Assert.Single(Parse(candidate.ClashConfiguration!));
        Assert.Equal(candidate.Host, replayed.Host);
        Assert.Equal(candidate.Port, replayed.Port);
        Assert.Equal(candidate.Protocol, replayed.Protocol);
    }

    [Theory]
    [InlineData("127.0.0.1", "443")]
    [InlineData("192.168.1.1", "443")]
    [InlineData("[::1]", "443")]
    [InlineData("localhost", "443")]
    [InlineData("service.local", "443")]
    [InlineData("example.com", "0")]
    [InlineData("example.com", "65536")]
    public void RejectsUnsafeDestinationsAndInvalidPorts(string host, string port) =>
        Assert.Empty(Parse($"proxies: [{{type: http, server: '{host}', port: {port}}}]"));

    [Fact]
    public void DoesNotExtractIncidentalAddressesOrUriFromUnknownRecords()
    {
        var candidates = Parse("proxies: [{type: unknown, server: 8.8.8.8, port: 443, name: 'vless://incidental@1.1.1.1:80'}, {type: http, server: 9.9.9.9, port: 80}]\nlog: 'http://8.8.4.4:3128'");
        Assert.Equal("9.9.9.9", Assert.Single(candidates).Host);
    }

    [Fact]
    public void RefusesToPublishAConnectionWithoutItsDialerDependency() =>
        Assert.Throws<InvalidDataException>(() => Parse("proxies: [{type: http, server: 8.8.8.8, port: 443, dialer-proxy: missing}]"));

    [Fact]
    public void PreservesDialerProxyAndItsGroupDependencies()
    {
        const string yaml = "proxies: [{name: primary, type: vless, server: example.com, port: 443, dialer-proxy: route}, {name: peer, type: socks5, server: 8.8.8.8, port: 1080, password: published}]\nproxy-groups: [{name: route, type: select, proxies: [peer, DIRECT]}]";
        var primary = Parse(yaml).Single(candidate => candidate.Host == "example.com");
        var document = ClashYamlFeedReader.ReadRequired(primary.ClashConfiguration!);
        var proxies = Assert.IsType<YamlSequenceNode>(document.Children[new YamlScalarNode("proxies")]);
        Assert.Equal(2, proxies.Children.Count);
        Assert.Equal("primary", Assert.IsType<YamlScalarNode>(Assert.IsType<YamlMappingNode>(proxies.Children[0]).Children[new YamlScalarNode("name")]).Value);
        var groups = Assert.IsType<YamlSequenceNode>(document.Children[new YamlScalarNode("proxy-groups")]);
        Assert.Single(groups.Children);
        Assert.Contains("published", primary.ClashConfiguration);
    }

    [Theory]
    [InlineData("proxies: [{name: a, type: http, server: 8.8.8.8, port: 80, dialer-proxy: b}, {name: b, type: socks5, server: 1.1.1.1, port: 1080, dialer-proxy: a}]")]
    [InlineData("proxies: [{name: a, type: http, server: 8.8.8.8, port: 80, dialer-proxy: b}, {name: b, type: socks5, server: 127.0.0.1, port: 1080}]")]
    [InlineData("proxies: [{name: a, type: http, server: 8.8.8.8, port: 80, dialer-proxy: b}, {name: b, type: socks5, server: 1.1.1.1, port: 1080}, {name: b, type: socks5, server: 9.9.9.9, port: 1080}]")]
    [InlineData("proxies: [{name: a, type: http, server: 8.8.8.8, port: 80, dialer-proxy: group}]\nproxy-groups: [{name: group, type: select, use: [missing-provider], proxies: [DIRECT]}]")]
    public void RejectsUnsafeAmbiguousOrIncompleteDialerGraphs(string yaml) =>
        Assert.Throws<InvalidDataException>(() => Parse(yaml));

    [Fact]
    public void BoundsEscapedConfigurationOutput()
    {
        var yaml = "proxies: [{type: http, server: 8.8.8.8, port: 443, password: \"" + string.Concat(Enumerable.Repeat("\\a", 8_500)) + "\"}]";
        ClashYamlFeedReader.ReadRequired(yaml);
        Assert.Throws<InvalidDataException>(() => Parse(yaml));
    }

    private static YamlMappingNode Proxy(YamlMappingNode root) =>
        Assert.IsType<YamlMappingNode>(Assert.IsType<YamlSequenceNode>(root.Children[new YamlScalarNode("proxies")]).Children.Single());

    private static List<VpnCandidate> Parse(string content)
    {
        var candidates = new List<VpnCandidate>();
        ClashYamlFeedParser.ParseAllTo(content, candidate => { candidates.Add(candidate); return true; });
        return candidates;
    }
}
