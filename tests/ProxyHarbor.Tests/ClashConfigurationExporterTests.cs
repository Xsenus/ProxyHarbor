using System.Text;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using YamlDotNet.RepresentationModel;

namespace ProxyHarbor.Tests;

public sealed class ClashConfigurationExporterTests
{
    [Theory]
    [InlineData("vless")]
    [InlineData("vmess")]
    [InlineData("trojan")]
    [InlineData("ss")]
    [InlineData("ssr")]
    [InlineData("hysteria")]
    [InlineData("hysteria2")]
    [InlineData("tuic")]
    [InlineData("wireguard")]
    [InlineData("anytls")]
    [InlineData("http")]
    [InlineData("socks4")]
    [InlineData("socks5")]
    public void EverySupportedProtocolKeepsPublishedSettingsAndGetsClientRouting(string type)
    {
        var candidate = Candidate($"proxies: [{{type: {type}, server: 8.8.8.8, port: 443, password: '0012', cipher: aes-128-gcm, uuid: published, tls: true, reality-opts: {{public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA}}, future-option: {{values: [a,b]}}}}]");
        var original = Proxy(ClashYamlFeedReader.ReadRequired(candidate.ClashConfiguration!));
        var root = Read(ClashConfigurationExporter.Export([candidate]));
        var exported = Proxy(root);
        foreach (var pair in original.Children) Assert.Equal(pair.Value, exported.Children[pair.Key]);
        Assert.Equal($"PH-1-node-1 · {type} 8.8.8.8:443", Scalar(exported, "name"));
        Assert.Equal("7890", Scalar(root, "mixed-port"));
        Assert.Equal("false", Scalar(root, "allow-lan"));
        Assert.Equal("MATCH,ProxyHarbor", Assert.IsType<YamlScalarNode>(Assert.IsType<YamlSequenceNode>(Property(root, "rules")).Children[0]).Value);
        var replayed = Assert.Single(VpnFeedParser.Parse(Encoding.UTF8.GetString(ClashConfigurationExporter.Export([candidate])), VpnProtocol.Vless));
        Assert.Equal(candidate.Host, replayed.Host);
        Assert.Equal(candidate.Protocol, replayed.Protocol);
    }

    [Fact]
    public void CollidingNamesAndNestedGroupReferencesAreIsolatedWithoutChangingOtherStrings()
    {
        VpnCandidate Node(string host, string password) => Candidate(
            $"proxies: [{{name: shared, type: vless, server: {host}, port: 443, uuid: published, dialer-proxy: route, ws-opts: {{path: '/peer', headers: {{Host: route}}}}}}, {{name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: {password}}}]\nproxy-groups: [{{name: route, type: select, proxies: [nested]}}, {{name: nested, type: select, proxies: [peer, DIRECT], include-all: false}}]", host);
        var first = Node("8.8.8.8", "alpha");
        var second = Node("9.9.9.9", "beta");
        var root = Read(ClashConfigurationExporter.Export([first, second]));
        var proxies = Assert.IsType<YamlSequenceNode>(Property(root, "proxies"));
        var groups = Assert.IsType<YamlSequenceNode>(Property(root, "proxy-groups"));
        Assert.Equal(4, proxies.Children.Count);
        Assert.Equal(5, groups.Children.Count);
        var names = proxies.Children.Concat(groups.Children).Cast<YamlMappingNode>().Select(node => Scalar(node, "name")).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        for (var index = 0; index < 2; index++)
        {
            var prefix = $"PH-{index + 1}-";
            var primary = (YamlMappingNode)proxies.Children[index * 2];
            Assert.Equal(prefix + "group-1", Scalar(primary, "dialer-proxy"));
            var options = Assert.IsType<YamlMappingNode>(Property(primary, "ws-opts"));
            Assert.Equal("/peer", Scalar(options, "path"));
            Assert.Equal("route", Scalar(Assert.IsType<YamlMappingNode>(Property(options, "headers")), "Host"));
            Assert.Equal(index == 0 ? "alpha" : "beta", Scalar((YamlMappingNode)proxies.Children[index * 2 + 1], "password"));
            var nested = Assert.IsType<YamlSequenceNode>(Property((YamlMappingNode)groups.Children[index * 2 + 1], "proxies"));
            Assert.Equal(Scalar((YamlMappingNode)proxies.Children[index * 2 + 1], "name"), ((YamlScalarNode)nested.Children[0]).Value);
            Assert.Equal("DIRECT", ((YamlScalarNode)nested.Children[1]).Value);
        }
        Assert.Equal("shared", Scalar(Proxy(ClashYamlFeedReader.ReadRequired(first.ClashConfiguration!)), "name"));
    }

    [Fact]
    public void MergedNameAndDialerAreOverriddenWithoutLosingMergedConnectionOptions()
    {
        const string yaml = "defaults: &opts {name: old, type: vless, server: 8.8.8.8, port: 443, dialer-proxy: peer, uuid: published, tls: true}\nproxies: [{<<: *opts}, {name: peer, type: socks5, server: 1.1.1.1, port: 1080}]";
        var root = Read(ClashConfigurationExporter.Export([Candidate(yaml)]));
        var primary = Proxy(root);
        Assert.Equal("PH-1-node-1 · vless 8.8.8.8:443", Scalar(primary, "name"));
        Assert.Equal("PH-1-node-2 · socks5 1.1.1.1:1080", Scalar(primary, "dialer-proxy"));
        Assert.Equal("published", Scalar(primary, "uuid"));
        Assert.Equal("true", Scalar(primary, "tls"));
        Assert.Equal(2, VpnFeedParser.Parse(Encoding.UTF8.GetString(ClashConfigurationExporter.Export([Candidate(yaml)])), VpnProtocol.Vless).Count);
    }

    [Theory]
    [InlineData("proxies: [{type: vless, server: 127.0.0.1, port: 443, password: private-secret}]")]
    [InlineData("proxies: [{type: vless, server: 9.9.9.9, port: 443, password: private-secret}]")]
    [InlineData("proxies: [{type: vless, server: 8.8.8.8, port: 443, dialer-proxy: missing, password: private-secret}]")]
    [InlineData("proxies: [{type: vless, server: 8.8.8.8, port: 443}]\nexternal-controller: 0.0.0.0:9090")]
    public void InvalidSavedDataCannotLeakSecretsOrPublishAnIncompleteDocument(string configuration)
    {
        var candidate = new VpnCandidate("8.8.8.8", 443, VpnProtocol.Vless, "tcp") { ClashConfiguration = configuration };
        var exception = Assert.Throws<InvalidDataException>(() => ClashConfigurationExporter.Export([candidate]));
        Assert.DoesNotContain("private-secret", exception.ToString());
    }

    [Fact]
    public void EmptyMissingAndOversizedInputAreRefusedWithoutPartialOutput()
    {
        Assert.Throws<InvalidDataException>(() => ClashConfigurationExporter.Export([]));
        Assert.Throws<InvalidDataException>(() => ClashConfigurationExporter.Export([new VpnCandidate("8.8.8.8", 443, VpnProtocol.Vless, "tcp")]));
        var candidate = Candidate("proxies: [{type: vless, server: 8.8.8.8, port: 443, future-option: '" + new string('x', 14_000) + "'}]");
        Assert.Throws<InvalidDataException>(() => ClashConfigurationExporter.Export(Enumerable.Repeat(candidate, 400)));
        var small = Candidate("proxies: [{type: vless, server: 8.8.8.8, port: 443}]");
        Assert.Throws<InvalidDataException>(() => ClashConfigurationExporter.Export(Enumerable.Repeat(small, 5_001)));
    }

    [Fact]
    public void ProfileQuotaCountsEveryDialerDependencyAndKeepsCompleteChainsAtTheBoundary()
    {
        var chain = Candidate("proxies: [{name: primary, type: vless, server: 8.8.8.8, port: 443, uuid: preserved-secret, dialer-proxy: peer}, {name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: preserved-secret}]");
        var document = Read(ClashConfigurationExporter.Export(Enumerable.Repeat(chain, 5), maximumProfiles: 10));
        Assert.Equal(10, Assert.IsType<YamlSequenceNode>(Property(document, "proxies")).Children.Count);
        var selector = (YamlMappingNode)Assert.IsType<YamlSequenceNode>(Property(document, "proxy-groups")).Children.Last();
        Assert.Equal(5, Assert.IsType<YamlSequenceNode>(Property(selector, "proxies")).Children.Count);
        var error = Assert.Throws<InvalidDataException>(() => ClashConfigurationExporter.Export(Enumerable.Repeat(chain, 6), maximumProfiles: 10));
        Assert.DoesNotContain("preserved-secret", error.ToString());
        Assert.Throws<InvalidDataException>(() => ClashConfigurationExporter.Export([chain], maximumProfiles: 1));
    }

    [Fact]
    public void CurrentSettingsComparisonIgnoresNamesAndMappingOrderButDetectsCredentialAndTlsChanges()
    {
        var expected = Candidate("proxies: [{name: old, type: vless, server: 8.8.8.8, port: 443, uuid: same-secret, tls: true, sni: tls.example.net, dialer-proxy: peer}, {name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: peer-secret}]");
        const string reordered = "proxies: [{sni: tls.example.net, tls: true, uuid: same-secret, port: 443, server: 8.8.8.8, type: vless, name: renamed, dialer-proxy: renamed-peer}, {password: peer-secret, port: 1080, server: 1.1.1.1, type: socks5, name: renamed-peer}]";
        var current = Candidate(reordered);
        Assert.True(ClashConfigurationExporter.SameSettings(expected, current));
        Assert.Equal(2, ClashConfigurationExporter.ProfileCount(expected));
        Assert.Equal(2, ClashConfigurationExporter.Profiles(expected).Count());
        Assert.False(ClashConfigurationExporter.SameSettings(expected, Candidate(reordered.Replace("same-secret", "rotated-secret"))));
        Assert.False(ClashConfigurationExporter.SameSettings(expected, Candidate(reordered.Replace("peer-secret", "rotated-peer-secret"))));
        Assert.False(ClashConfigurationExporter.SameSettings(expected, Candidate(reordered.Replace("tls.example.net", "another.example.net"))));
        Assert.False(ClashConfigurationExporter.SameSettings(expected, new VpnCandidate("8.8.8.8", 443, VpnProtocol.Vless, "tcp")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5001)]
    public void InvalidProfileQuotasAreRejected(int quota) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ClashConfigurationExporter.Export([], quota));

    [Theory]
    [InlineData("0012", "'0012'", false)]
    [InlineData("'0012'", "\"0012\"", true)]
    [InlineData("Ab01", "'Ab01'", true)]
    public void CurrentRealitySettingsRespectScalarInterpretationRatherThanOnlyLexicalValue(string first, string second, bool equal)
    {
        VpnCandidate WithId(string id) => Candidate($"proxies: [{{type: vless, server: 8.8.8.8, port: 443, reality-opts: {{public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: {id}}}}}]");
        Assert.Equal(equal, ClashConfigurationExporter.SameSettings(WithId(first), WithId(second)));
    }

    [Theory]
    [InlineData("same-secret", "'same-secret'", true)]
    [InlineData("null", "'null'", false)]
    [InlineData("true", "'true'", false)]
    [InlineData("0012", "'0012'", false)]
    [InlineData("1e02", "'1e02'", false)]
    [InlineData("0x10", "'0x10'", false)]
    [InlineData("0b10", "'0b10'", false)]
    [InlineData("2026-10-07", "'2026-10-07'", false)]
    [InlineData(".nan", "'.nan'", false)]
    [InlineData("'0012'", "\"0012\"", true)]
    public void CurrentDependencySettingsDistinguishImplicitTypesAndPreserveEquivalentStringQuoting(string first, string second, bool equal)
    {
        VpnCandidate WithPassword(string password) => Candidate($"proxies: [{{type: vless, server: 8.8.8.8, port: 443, dialer-proxy: peer}}, {{name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: {password}}}]");
        Assert.Equal(equal, ClashConfigurationExporter.SameSettings(WithPassword(first), WithPassword(second)));
    }

    private static VpnCandidate Candidate(string yaml, string host = "8.8.8.8") => VpnFeedParser.Parse(yaml, VpnProtocol.Vless).Single(candidate => candidate.Host == host);
    private static YamlMappingNode Read(byte[] bytes) => ClashYamlFeedReader.ReadRequired(Encoding.UTF8.GetString(bytes));
    private static YamlMappingNode Proxy(YamlMappingNode root) => (YamlMappingNode)((YamlSequenceNode)Property(root, "proxies")!).Children[0];
    private static YamlNode? Property(YamlMappingNode node, string key) => ClashYamlFeedParser.Property(node, key);
    private static string? Scalar(YamlMappingNode node, string key) => (Property(node, key) as YamlScalarNode)?.Value;
}
