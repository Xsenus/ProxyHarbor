using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует bounded trust boundary для X-Forwarded-For и X-Forwarded-Proto.</summary>
public sealed class ForwardedNetworkPolicyTests
{
    [Fact]
    public void AcceptsCanonicalPrivateCdnHostAndIpv6Networks()
    {
        var valid = ForwardedNetworkPolicy.TryParse(
            ["172.30.0.0/24", "203.0.113.45/32", "2001:db8::/32", "::1/128", "172.30.0.0/24"],
            out var networks);

        Assert.True(valid);
        Assert.Equal(4, networks.Length);
        Assert.Equal("172.30.0.0/24", networks[0].ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 172.30.0.0/24")]
    [InlineData("172.30.0.1/24")]
    [InlineData("172.30.0.0")]
    [InlineData("0.0.0.0/0")]
    [InlineData("10.0.0.0/7")]
    [InlineData("::/0")]
    [InlineData("2001::/23")]
    [InlineData("not-a-network")]
    public void RejectsMalformedNoncanonicalOrOverbroadNetworks(string value)
    {
        Assert.False(ForwardedNetworkPolicy.TryParse([value], out var networks));
        Assert.Empty(networks);
    }

    [Fact]
    public void EmptyConfigurationKeepsFrameworkLoopbackDefaults()
    {
        Assert.True(ForwardedNetworkPolicy.TryParse(null, out var networks));
        Assert.Empty(networks);
    }

    [Fact]
    public void RejectsUnboundedNetworkCountAndLength()
    {
        var tooMany = Enumerable.Range(1, 33).Select(index => $"10.{index}.0.0/16");

        Assert.False(ForwardedNetworkPolicy.TryParse(tooMany, out _));
        Assert.False(ForwardedNetworkPolicy.TryParse([new string('1', 65)], out _));
    }
}
