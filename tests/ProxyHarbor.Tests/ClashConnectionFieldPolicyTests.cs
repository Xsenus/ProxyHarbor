using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ClashConnectionFieldPolicyTests
{
    [Theory]
    [InlineData("ss", "cipher: aes-128-gcm")]
    [InlineData("ss", "cipher: aes-128-gcm, password: ''")]
    [InlineData("ss", "cipher: aes-128-gcm, password: null")]
    [InlineData("ss", "cipher: aes-128-gcm, password: NULL")]
    [InlineData("ss", "cipher: aes-128-gcm, password: !!null ignored")]
    [InlineData("ss", "cipher: aes-128-gcm, password: []")]
    [InlineData("ss", "cipher: unknown-cipher, password: private-password")]
    [InlineData("ss", "cipher: AES-128-GCM, password: private-password")]
    [InlineData("ss", "password: private-password")]
    [InlineData("vless", "flow: xtls-rprx-direct")]
    [InlineData("vless", "flow: xtls-rprx-splice")]
    [InlineData("vless", "flow: [xtls-rprx-vision]")]
    [InlineData("vless", "flow: 'абвгдежз'")]
    [InlineData("vless", "reality-opts: {public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: abc}")]
    [InlineData("vless", "reality-opts: {public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: '000000000000000000'}")]
    [InlineData("vless", "reality-opts: {public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: 'gg'}")]
    [InlineData("vless", "reality-opts: {public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: []}")]
    [InlineData("vless", "reality-opts: {public-key: invalid-key}")]
    [InlineData("vless", "reality-opts: {public-key: []}")]
    [InlineData("vless", "reality-opts: {public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=}")]
    public void InvalidConnectionSettingsExcludeOnlyTheInvalidRootAndCannotBeExported(string type, string settings)
    {
        var saved = $"proxies: [{{type: {type}, server: 8.8.8.8, port: 443, {settings}}}]";
        var candidate = new VpnCandidate("8.8.8.8", 443, type == "ss" ? VpnProtocol.Shadowsocks : VpnProtocol.Vless, "tcp")
        { ClashConfiguration = saved };
        Assert.False(ClashYamlFeedParser.IsValidStandalone(candidate));
        var error = Assert.Throws<InvalidDataException>(() => ClashConfigurationExporter.Export([candidate]));
        Assert.DoesNotContain("private-password", error.Message, StringComparison.Ordinal);
        var mixed = saved.Replace("}]", "}, {type: http, server: 1.1.1.1, port: 80}]", StringComparison.Ordinal);
        var valid = Assert.Single(VpnFeedParser.Parse(mixed, VpnProtocol.Vless));
        Assert.Equal(VpnProtocol.HttpProxy, valid.Protocol);
    }

    [Theory]
    [InlineData("cipher: none")]
    [InlineData("cipher: none, password: ''")]
    [InlineData("cipher: aes-128-gcm, password: ' '")]
    [InlineData("cipher: aes-128-gcm, password: 'null'")]
    [InlineData("cipher: aes-128-gcm, password: !!str null")]
    [InlineData("cipher: aes-128-gcm, password: nUlL")]
    [InlineData("cipher: ascon128, password: published")]
    [InlineData("cipher: ascon128a, password: published")]
    [InlineData("cipher: 2022-blake3-chacha8-poly1305, password: published")]
    [InlineData("cipher: 2022-blake3-aes-128-ccm, password: published")]
    [InlineData("cipher: 2022-blake3-aes-256-ccm, password: published")]
    public void SupportedCipherNamesAndPasswordScalarsArePreservedWithoutNormalization(string settings)
    {
        var candidate = Assert.Single(VpnFeedParser.Parse($"proxies: [{{type: ss, server: 8.8.8.8, port: 443, {settings}}}]", VpnProtocol.Vless));
        Assert.Equal(VpnProtocol.Shadowsocks, candidate.Protocol);
        Assert.True(ClashYamlFeedParser.IsValidStandalone(candidate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("flow: xtls-rprx-vision,")]
    [InlineData("flow: xtls-rprx-vision-udp443,")]
    [InlineData("flow: '',")]
    public void EmptyOrHexRealityIdsAndVisionSuffixesRemainImportable(string flow)
    {
        var yaml = $"proxies: [{{type: vless, server: 8.8.8.8, port: 443, {flow} reality-opts: {{public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: 'Ab01'}}}}]";
        Assert.Single(VpnFeedParser.Parse(yaml, VpnProtocol.Vless));
        Assert.Single(VpnFeedParser.Parse(yaml.Replace("'Ab01'", "''", StringComparison.Ordinal), VpnProtocol.Vless));
        Assert.Single(VpnFeedParser.Parse(yaml.Replace(", short-id: 'Ab01'", "", StringComparison.Ordinal), VpnProtocol.Vless));
    }

    [Fact]
    public void DisabledRealityOptionsPreserveNativeOptionalSemantics()
    {
        Assert.Single(VpnFeedParser.Parse("proxies: [{type: vless, server: 8.8.8.8, port: 443, reality-opts: null}]", VpnProtocol.Vless));
        Assert.Single(VpnFeedParser.Parse("proxies: [{type: vless, server: 8.8.8.8, port: 443, reality-opts: {short-id: ignored-without-key}}]", VpnProtocol.Vless));
    }

    [Theory]
    [InlineData("08", false)]
    [InlineData("0008", false)]
    [InlineData("01", false)]
    [InlineData("0010", false)]
    [InlineData("0b10", false)]
    [InlineData("1e02", false)]
    [InlineData("!!float 12", false)]
    [InlineData("!!int ab", false)]
    [InlineData("'08'", true)]
    [InlineData("!!str 08", true)]
    [InlineData("0012", true)]
    [InlineData("012", true)]
    [InlineData("0b1010", true)]
    [InlineData("12", true)]
    [InlineData("ab01", true)]
    [InlineData("01e999", true)]
    public void RealityIdValidationUsesNativeNumericResolutionWithoutRewritingPublishedValues(string id, bool accepted)
    {
        var yaml = $"proxies: [{{type: vless, server: 8.8.8.8, port: 443, reality-opts: {{public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: {id}}}}}]";
        var records = VpnFeedParser.Parse(yaml, VpnProtocol.Vless);
        if (!accepted) Assert.Empty(records);
        else
        {
            var candidate = Assert.Single(records);
            Assert.True(ClashYamlFeedParser.IsValidStandalone(candidate));
            Assert.NotEmpty(ClashConfigurationExporter.Export([candidate]));
        }
    }

    [Fact]
    public void MergedSettingsUseExplicitOverridesAndInvalidDependencyFailsTheWholeBody()
    {
        const string merged = "defaults: &defaults {cipher: unknown-cipher, password: null}\nproxies: [{<<: *defaults, type: ss, server: 8.8.8.8, port: 443, cipher: aes-128-gcm, password: published}]";
        Assert.Single(VpnFeedParser.Parse(merged, VpnProtocol.Vless));
        const string incomplete = "proxies: [{name: main, type: http, server: 8.8.8.8, port: 80, dialer-proxy: broken}, {name: broken, type: ss, server: 1.1.1.1, port: 443, cipher: aes-128-gcm}]";
        Assert.Throws<InvalidDataException>(() => VpnFeedParser.Parse(incomplete, VpnProtocol.Vless));
    }
}
