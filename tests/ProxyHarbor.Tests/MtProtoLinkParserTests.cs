using System.Text;
using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class MtProtoLinkParserTests
{
    // Synthetic bytes, never a provider credential. Keep the fixture out of secret-like literals.
    private static readonly string Secret = Convert.ToHexString(
        new byte[] { 1, 35, 69, 103, 137, 171, 205, 239, 1, 35, 69, 103, 137, 171, 205, 239 }).ToLowerInvariant();

    [Theory]
    [InlineData("server")]
    [InlineData("port")]
    [InlineData("secret")]
    public void OversizedJsonFieldDoesNotDiscardHealthyNeighbor(string field)
    {
        var oversized = new Dictionary<string, string>
        {
            ["server"] = "8.8.8.8",
            ["port"] = "443",
            ["secret"] = Secret,
            [field] = new string('a', 20_000)
        };
        var valid = $"tg://proxy?server=1.1.1.1&port=443&secret={Secret}";
        var body = JsonSerializer.Serialize(new object[] { oversized, valid });
        Assert.Equal(valid, Assert.Single(VpnFeedParser.Parse(body, VpnProtocol.MtProto)).ConnectionUri);
    }

    [Fact]
    public void JsonApiReadsPublishedAndConstructedLinksWithoutDiagnosticFields()
    {
        var published = $"tg://proxy?server=8.8.8.8&port=443&secret={Secret}";
        var body = JsonSerializer.Serialize(new
        {
            proxies = new object[]
        {
            new { link = published }, new { server = "1.1.1.1", port = 8443, secret = Secret },
            new { server = "127.0.0.1", port = 443, secret = Secret }, new { message = published },
            "vless://user@8.8.4.4:443"
        },
            message = published
        });
        var items = VpnFeedParser.Parse(body, VpnProtocol.MtProto);
        Assert.Equal(3, items.Count);
        Assert.Equal(published, items.Single(item => item.Host == "8.8.8.8").ConnectionUri);
        Assert.Equal(2, items.Count(item => item.Protocol == VpnProtocol.MtProto));
        Assert.Single(VpnFeedParser.Parse(body, VpnProtocol.MtProto, 1));
        Assert.Empty(VpnFeedParser.Parse(JsonSerializer.Serialize(new { message = published }), VpnProtocol.MtProto));
        Assert.Empty(VpnFeedParser.Parse(JsonSerializer.Serialize(new { success = false, proxies = new[] { published } }), VpnProtocol.MtProto));
        Assert.Empty(VpnFeedParser.Parse("{invalid-json", VpnProtocol.MtProto));
    }

    [Theory]
    [InlineData("tg://proxy", "8.8.8.8")]
    [InlineData("tg:proxy", "example.com")]
    [InlineData("https://t.me/proxy", "2606:4700:4700::1111")]
    [InlineData("https://telegram.me/proxy", "one.one.one.one")]
    [InlineData("https://telegram.dog/proxy", "8.8.4.4")]
    public void ReadsActualEndpointAndPreservesPublishedLink(string route, string host)
    {
        var uri = $"{route}?secret={Secret}&port=443&server={Uri.EscapeDataString(host)}#public-node";
        var candidate = Assert.Single(VpnFeedParser.Parse(uri, VpnProtocol.Vless));
        Assert.Equal(host, candidate.Host);
        Assert.Equal(443, candidate.Port);
        Assert.Equal(VpnProtocol.MtProto, candidate.Protocol);
        Assert.Equal("tcp", candidate.Transport);
        Assert.Equal(uri, candidate.ConnectionUri);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(0xdd, false)]
    [InlineData(0xdd, true)]
    [InlineData(0xee, false)]
    [InlineData(0xee, true)]
    public void AcceptsTdLibBinarySecretFormsInHexAndBase64(int prefix, bool base64)
    {
        byte[] bytes = Convert.FromHexString(Secret);
        if (prefix != 0) bytes = [(byte)prefix, .. bytes];
        if (prefix == 0xee) bytes = [.. bytes, .. Encoding.ASCII.GetBytes("example.com")];
        var encoded = base64 ? Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') : Convert.ToHexString(bytes);
        var uri = $"tg://proxy?server=8.8.8.8&port=443&secret={Uri.EscapeDataString(encoded)}";
        Assert.Single(VpnFeedParser.Parse(uri, VpnProtocol.MtProto));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    [InlineData("node.local")]
    [InlineData("bad_host")]
    [InlineData("8.8.8.8:80")]
    [InlineData("8.8.8.8%0A")]
    public void RejectsUnsafeServerEvenThoughTelegramLinkHostIsPublic(string host)
    {
        Assert.Empty(VpnFeedParser.Parse($"https://t.me/proxy?server={host}&port=443&secret={Secret}", VpnProtocol.MtProto));
    }

    [Theory]
    [InlineData("tg://socks")]
    [InlineData("tg://proxy/path")]
    [InlineData("tg://user@proxy")]
    [InlineData("tg://proxy:80")]
    [InlineData("https://evil.example/proxy")]
    [InlineData("https://t.me.evil.example/proxy")]
    [InlineData("https://t.me/channel")]
    public void RejectsOtherRoutes(string route) =>
        Assert.Empty(VpnFeedParser.Parse($"{route}?server=8.8.8.8&port=443&secret={Secret}", VpnProtocol.MtProto));

    [Theory]
    [InlineData("server=8.8.8.8&server=1.1.1.1&port=443")]
    [InlineData("server=8.8.8.8&port=443&%70ort=80")]
    [InlineData("server=8.8.8.8&port=0")]
    [InlineData("server=8.8.8.8&port=65536")]
    [InlineData("server=8.8.8.8&port=+443")]
    [InlineData("port=443")]
    public void RejectsMissingAmbiguousOrInvalidEndpoint(string query) =>
        Assert.Empty(VpnFeedParser.Parse($"tg://proxy?{query}&secret={Secret}", VpnProtocol.MtProto));

    [Theory]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggg")]
    [InlineData("000123456789abcdef0123456789abcdef00")]
    [InlineData("ee0123456789abcdef0123456789abcdef")]
    [InlineData("%00")]
    [InlineData("%0A")]
    public void RejectsUnsupportedOrDamagedSecret(string secret) =>
        Assert.Empty(VpnFeedParser.Parse($"tg://proxy?server=8.8.8.8&port=443&secret={secret}", VpnProtocol.MtProto));

    [Fact]
    public void SecretLengthBoundaryAndDuplicateSecretAreEnforced()
    {
        var prefix = "ee" + Secret;
        var maximum = prefix + Convert.ToHexString(new byte[182]);
        var uri = $"tg://proxy?server=8.8.8.8&port=443&secret={maximum}";
        Assert.Single(VpnFeedParser.Parse(uri, VpnProtocol.MtProto));
        Assert.Empty(VpnFeedParser.Parse(uri + "00", VpnProtocol.MtProto));
        Assert.Empty(VpnFeedParser.Parse(uri + "&secret=" + Secret, VpnProtocol.MtProto));
    }

    [Fact]
    public void NativeSnapshotReplaysTelegramLinkBesideOtherProtocols()
    {
        var uri = $"tg://proxy?server=8.8.8.8&port=443&secret={Secret}";
        var body = uri + "\nvless://user@1.1.1.1:443";
        var snapshot = VpnCandidateSnapshotCodec.Encode(body, VpnProtocol.MtProto);
        var restored = new List<VpnCandidate>();
        VpnCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, 10, candidate => { restored.Add(candidate); return true; });
        Assert.Equal(2, restored.Count);
        Assert.Equal(uri, restored.Single(candidate => candidate.Protocol == VpnProtocol.MtProto).ConnectionUri);
    }
}
