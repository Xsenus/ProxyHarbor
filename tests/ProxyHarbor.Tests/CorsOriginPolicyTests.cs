using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует HTTPS-only CORS boundary и Development-исключение для локального Vite.</summary>
public sealed class CorsOriginPolicyTests
{
    [Fact]
    public void ProductionCanonicalizesAndDeduplicatesHttpsOrigins()
    {
        var valid = CorsOriginPolicy.TryNormalize(
            [" HTTPS://Dashboard.Example/ ", "https://dashboard.example", "https://[2001:db8::1]:8443"],
            allowHttp: false,
            out var origins);

        Assert.True(valid);
        Assert.Equal(["https://dashboard.example", "https://[2001:db8::1]:8443"], origins);
    }

    [Fact]
    public void DevelopmentCanUsePlainHttpOrigin()
    {
        Assert.True(CorsOriginPolicy.TryNormalize(
            ["http://localhost:5173/"], allowHttp: true, out var origins));
        Assert.Equal(["http://localhost:5173"], origins);
    }

    [Theory]
    [InlineData("http://dashboard.example")]
    [InlineData("ftp://dashboard.example")]
    [InlineData("//dashboard.example")]
    [InlineData("https:\\dashboard.example")]
    [InlineData("https://user:password@dashboard.example")]
    [InlineData("https://dashboard.example/path")]
    [InlineData("https://dashboard.example/?query=1")]
    [InlineData("https://dashboard.example/#fragment")]
    public void ProductionRejectsUnsafeOrNonOriginValues(string value)
    {
        Assert.False(CorsOriginPolicy.TryNormalize([value], allowHttp: false, out var origins));
        Assert.Empty(origins);
    }

    [Fact]
    public void EmptyConfigurationDisablesCors()
    {
        Assert.True(CorsOriginPolicy.TryNormalize(null, allowHttp: false, out var origins));
        Assert.Empty(origins);
    }

    [Fact]
    public void RejectsUnboundedOriginCountAndLength()
    {
        var tooMany = Enumerable.Range(1, 33).Select(index => $"https://dashboard-{index}.example");

        Assert.False(CorsOriginPolicy.TryNormalize(tooMany, allowHttp: false, out _));
        Assert.False(CorsOriginPolicy.TryNormalize(
            [$"https://{new string('a', 2049)}.example"], allowHttp: false, out _));
    }
}
