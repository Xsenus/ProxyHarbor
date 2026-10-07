using ProxyHarbor.Infrastructure;
using YamlDotNet.RepresentationModel;

namespace ProxyHarbor.Tests;

public sealed class ClashYamlFeedReaderTests
{
    [Theory]
    [InlineData("vless")]
    [InlineData("vmess")]
    [InlineData("ss")]
    [InlineData("ssr")]
    [InlineData("trojan")]
    [InlineData("hysteria")]
    [InlineData("hysteria2")]
    [InlineData("tuic")]
    [InlineData("wireguard")]
    [InlineData("anytls")]
    [InlineData("http")]
    [InlineData("socks5")]
    public void KeepsProtocolSpecificAndUnknownConnectionSettings(string protocol)
    {
        var root = ClashYamlFeedReader.ReadRequired($$$"""
            proxies:
              - type: {{{protocol}}}
                server: example.com
                port: 443
                password: "0012"
                reality-opts: {public-key: published-key, short-id: '0001'}
                ws-opts: {path: '/socket?ed=2048', headers: {Host: example.org}}
                future-option: {values: [a, b]}
            rules: [MATCH,DIRECT]
            """);
        var proxy = Assert.IsType<YamlMappingNode>(Assert.IsType<YamlSequenceNode>(root.Children[new YamlScalarNode("proxies")]).Children.Single());
        Assert.Equal(protocol, Assert.IsType<YamlScalarNode>(proxy.Children[new YamlScalarNode("type")]).Value);
        Assert.Equal("0012", Assert.IsType<YamlScalarNode>(proxy.Children[new YamlScalarNode("password")]).Value);
        Assert.IsType<YamlMappingNode>(proxy.Children[new YamlScalarNode("reality-opts")]);
        Assert.IsType<YamlMappingNode>(proxy.Children[new YamlScalarNode("ws-opts")]);
        Assert.IsType<YamlMappingNode>(proxy.Children[new YamlScalarNode("future-option")]);
        Assert.True(root.Children.ContainsKey(new YamlScalarNode("rules")));
    }

    [Fact]
    public void ResolvesOrdinaryAliasesWithoutDroppingNestedSettings()
    {
        var root = ClashYamlFeedReader.ReadRequired("options: &opts {sni: example.org, alpn: [h2, http/1.1]}\nproxies: [{type: trojan, server: example.com, port: 443, options: *opts}]");
        var proxy = Assert.IsType<YamlMappingNode>(Assert.IsType<YamlSequenceNode>(root.Children[new YamlScalarNode("proxies")]).Children.Single());
        Assert.Same(root.Children[new YamlScalarNode("options")], proxy.Children[new YamlScalarNode("options")]);
    }

    [Theory]
    [InlineData("proxies: &a [*a]")]
    [InlineData("proxies: &a [{nested: &b [*a]}]")]
    [InlineData("proxies: [{type: ss, type: vmess}]")]
    [InlineData("proxies: []\n---\nproxies: []")]
    [InlineData("proxies: []")]
    [InlineData("items: []")]
    [InlineData("proxies: [scalar]")]
    [InlineData("proxies: [{? [a,b] : c}]")]
    [InlineData("proxies: [{? &a [*a] : c}]")]
    [InlineData("key: &a type\nproxies: [{*a: ss}]")]
    [InlineData("proxies: [*missing]")]
    public void RejectsIncompleteAmbiguousOrRecursiveDocuments(string yaml) =>
        Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired(yaml));

    [Fact]
    public void BoundsDepthAndScalarSize()
    {
        Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired("proxies: " + new string('[', 65) + new string(']', 65)));
        Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired("proxies: [{password: " + new string('a', 16_385) + "}]"));
    }

    [Fact]
    public void RejectsInvalidUtf8AndOversizedBody()
    {
        Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired("proxies: [{password: '\ud800'}]"));
        Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired(new string('x', 8 * 1024 * 1024 + 1)));
        Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired(new string('\u4e00', 3 * 1024 * 1024)));
    }

    [Fact]
    public void ParserErrorsNeverExposeOriginalCredentials()
    {
        var error = Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired("proxies: [{password: private-test-credential,"));
        Assert.DoesNotContain("private-test-credential", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void BoundsAliasExpansion()
    {
        var yaml = "a: &a [x,x,x,x,x,x,x,x,x,x]\n";
        for (var letter = 'b'; letter <= 'f'; letter++)
            yaml += $"{letter}: &{letter} [" + string.Join(',', Enumerable.Repeat("*" + (char)(letter - 1), 10)) + "]\n";
        yaml += "proxies: [" + string.Join(',', Enumerable.Repeat("*f", 10)) + "]";
        Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired(yaml));
    }

    [Fact]
    public void RejectsTooManyRecordsBeforeReturningPartialData()
    {
        var yaml = "proxies: [" + string.Join(',', Enumerable.Repeat("{}", 50_001)) + "]";
        Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired(yaml));
    }

    [Fact]
    public void BoundsAliasReferencesBeforeBuildingTheGraph()
    {
        var yaml = "option: &a scalar\nproxies: [" + string.Join(',', Enumerable.Repeat("*a", 50_001)) + "]";
        Assert.Throws<InvalidDataException>(() => ClashYamlFeedReader.ReadRequired(yaml));
    }
}
