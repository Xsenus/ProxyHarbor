using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Доказывает fail-closed правила URL и финального TCP connect против SSRF/DNS rebinding.</summary>
public sealed class NetworkSafetyTests
{
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("http://8.8.8.8/feed.txt")]
    [InlineData("https://user:password@8.8.8.8/feed.txt")]
    [InlineData("https://8.8.8.8:444/feed.txt")]
    [InlineData("https://8.8.8.8/feed.txt#fragment")]
    [InlineData("https://127.0.0.1/feed.txt")]
    [InlineData("https://[::1]/feed.txt")]
    public async Task SourceUrlRejectsUnsafeEndpointShapes(string url) =>
        Assert.False(await NetworkSafety.IsSafePublicHttpsUrlAsync(url, CancellationToken.None));

    [Fact]
    public async Task SourceUrlAcceptsPublicHttpsLiteralWithoutExternalDnsDependency() =>
        Assert.True(await NetworkSafety.IsSafePublicHttpsUrlAsync(
            "https://8.8.8.8/feed.txt?protocol=http",
            CancellationToken.None));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task FinalConnectionGateRejectsPrivateLiteralBeforeOpeningSocket(string host)
    {
        using var handler = PublicNetworkConnector.Harden(new SocketsHttpHandler());
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var uriHost = host.Contains(':') ? $"[{host}]" : host;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"https://{uriHost}/feed.txt"));

        Assert.Contains("локальный или служебный", exception.ToString(), StringComparison.Ordinal);
    }
}
