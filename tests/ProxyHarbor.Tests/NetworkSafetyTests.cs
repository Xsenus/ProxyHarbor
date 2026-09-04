using System.Net;
using System.Net.Sockets;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Доказывает fail-closed правила URL и финального TCP connect против SSRF/DNS rebinding.</summary>
public sealed class NetworkSafetyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://[")]
    [InlineData("https://")]
    [InlineData("https://8.8.8.8/feed\nnext")]
    [InlineData("https://user:secret@8.8.8.8/feed")]
    [InlineData("https://8.8.8.8:8443/feed")]
    [InlineData("https://8.8.8.8/feed#fragment")]
    public void SynchronousHttpsShapeGateRejectsMalformedInputWithoutThrowing(string? value)
    {
        Assert.False(NetworkSafety.TryParseSafeHttpsUrl(value, out var uri));
        Assert.Null(uri);
    }

    [Fact]
    public void SynchronousHttpsShapeGateRejectsOversizedInput()
    {
        Assert.False(NetworkSafety.TryParseSafeHttpsUrl(
            "https://8.8.8.8/" + new string('a', 2048), out _));
    }

    [Fact]
    public void SynchronousHttpsShapeGateBoundsNormalizedUnicodeUri()
    {
        var raw = "https://8.8.8.8/" + new string('я', 600);
        Assert.True(raw.Length < 2048);

        Assert.False(NetworkSafety.TryParseSafeHttpsUrl(raw, out _));
    }

    [Fact]
    public void SynchronousHttpsShapeGateReturnsNormalizedUri()
    {
        Assert.True(NetworkSafety.TryParseSafeHttpsUrl(
            "HTTPS://8.8.8.8:443/feed.txt?protocol=http", out var uri));

        Assert.Equal("https://8.8.8.8/feed.txt?protocol=http", uri.AbsoluteUri);
    }

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

    [Theory]
    [InlineData("127.0.0.1", 8080)]
    [InlineData("10.0.0.1", 8080)]
    [InlineData("169.254.169.254", 80)]
    [InlineData("::1", 8080)]
    [InlineData("not-an-ip", 8080)]
    [InlineData("8.8.8.8", 0)]
    public async Task ProxySocketGateRejectsUnsafeDatabaseEndpointBeforeConnect(string host, int port)
    {
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            PublicProxyConnector.ConnectAsync(host, port, CancellationToken.None));

        Assert.Contains("каноническим публичным IP", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2606:4700:4700:0:0:0:0:1111")]
    [InlineData("::ffff:8.8.8.8")]
    public async Task ProxySocketGateRejectsNonCanonicalPublicRepresentation(string host)
    {
        await Assert.ThrowsAsync<IOException>(() =>
            PublicProxyConnector.ConnectAsync(host, 8080, CancellationToken.None));
    }

    [Fact]
    public async Task FinalConnectionGateRejectsMixedDnsBeforeOpeningAnySocket()
    {
        var connectAttempts = 0;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            PublicNetworkConnector.ConnectCoreAsync(
                new DnsEndPoint("feed.example", 443),
                static (_, _) => Task.FromResult(new[]
                {
                    IPAddress.Parse("8.8.8.8"),
                    IPAddress.Loopback
                }),
                (_, _, _) =>
                {
                    connectAttempts++;
                    return ValueTask.FromResult<Stream>(new MemoryStream());
                },
                CancellationToken.None).AsTask());

        Assert.Contains("локальный или служебный", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, connectAttempts);
    }

    [Fact]
    public async Task FinalConnectionGateBoundsMaliciousDnsFanOut()
    {
        var addresses = Enumerable.Range(1, 33)
            .Select(index => IPAddress.Parse($"8.8.8.{index}"))
            .ToArray();
        var connectAttempts = 0;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            PublicNetworkConnector.ConnectCoreAsync(
                new DnsEndPoint("fanout.example", 443),
                (_, _) => Task.FromResult(addresses),
                (_, _, _) =>
                {
                    connectAttempts++;
                    return ValueTask.FromResult<Stream>(new MemoryStream());
                },
                CancellationToken.None).AsTask());

        Assert.Contains("лимит 32", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, connectAttempts);
    }

    [Fact]
    public async Task FinalConnectionGateFallsBackAcrossPublicAddressesOnly()
    {
        var first = IPAddress.Parse("8.8.8.8");
        var second = IPAddress.Parse("1.1.1.1");
        var attempts = new List<IPAddress>();
        using var expected = new MemoryStream();

        var actual = await PublicNetworkConnector.ConnectCoreAsync(
            new DnsEndPoint("fallback.example", 443),
            (_, _) => Task.FromResult(new[] { first, second }),
            (address, _, _) =>
            {
                attempts.Add(address);
                return address.Equals(first)
                    ? ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused))
                    : ValueTask.FromResult<Stream>(expected);
            },
            CancellationToken.None);

        Assert.Same(expected, actual);
        Assert.Equal(new[] { first, second }, attempts);
    }

    [Fact]
    public async Task HangingFirstPublicAddressDoesNotHideAWorkingFallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var expected = new MemoryStream();
        var first = IPAddress.Parse("8.8.8.8");
        var operation = PublicNetworkConnector.ConnectCoreAsync(
            new DnsEndPoint("fallback.example", 443),
            (_, _) => Task.FromResult(new[] { first, IPAddress.Parse("1.1.1.1") }),
            async (address, _, token) =>
            {
                if (address.Equals(first)) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return expected;
            }, cancellation.Token).AsTask();
        try
        {
            Assert.Same(expected, await operation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(expected.CanRead);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await operation; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task FinalConnectionGateDoesNotFallbackAfterCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PublicNetworkConnector.ConnectCoreAsync(
                new DnsEndPoint("cancel.example", 443),
                static (_, _) => Task.FromResult(new[]
                {
                    IPAddress.Parse("8.8.8.8"),
                    IPAddress.Parse("1.1.1.1")
                }),
                (_, _, token) =>
                {
                    attempts++;
                    return ValueTask.FromException<Stream>(new OperationCanceledException(token));
                },
                cancellation.Token).AsTask());

        Assert.Equal(0, attempts);
    }
}
