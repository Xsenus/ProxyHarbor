using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует production Host Filtering без allow-all и неоднозначных patterns.</summary>
public sealed class ProductionHostPolicyTests
{
    [Theory]
    [InlineData("proxy.example.com")]
    [InlineData("proxy.example.com;localhost")]
    [InlineData("*.example.com")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public void AcceptsExplicitBoundedHostPatterns(string value) =>
        Assert.True(ProductionHostPolicy.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("proxy.example.com;*")]
    [InlineData("proxy.example.com;")]
    [InlineData(" proxy.example.com")]
    [InlineData("proxy.example.com:443")]
    [InlineData("https://proxy.example.com")]
    [InlineData("proxy_host.example.com")]
    [InlineData("*.*.example.com")]
    [InlineData("tést.example.com")]
    [InlineData("*.127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("127.1")]
    [InlineData("8.8.8.008")]
    [InlineData("[::]")]
    [InlineData("::1")]
    [InlineData("[2001:0db8::1]")]
    public void RejectsAllowAllOrAmbiguousHostPatterns(string? value) =>
        Assert.False(ProductionHostPolicy.IsValid(value));

    [Fact]
    public void RejectsUnboundedHostList()
    {
        var value = string.Join(';', Enumerable.Range(1, 33).Select(index => $"host{index}.example.com"));

        Assert.False(ProductionHostPolicy.IsValid(value));
    }

    [Fact]
    public void RejectsOversizedConfigurationAndSingleHost()
    {
        Assert.False(ProductionHostPolicy.IsValid(new string('a', 4097)));
        Assert.False(ProductionHostPolicy.IsValid(new string('a', 254)));
    }
}
