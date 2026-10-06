using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ProxyParserWindowTests
{
    [Fact]
    public void WindowsReachTailAndKeepExplicitProtocols()
    {
        const string body = "http://8.8.8.8:80\nsocks5://1.1.1.1:1080\nhttps://9.9.9.9:443";
        var accepted = new List<ProxyCandidateKey>();
        var offset = 0;
        ProxyParseWindow window;
        do
        {
            window = ProxyParser.ParseWindowTo(body, ProxyProtocol.Socks4, offset, 1, candidate =>
            {
                accepted.Add(candidate);
                return true;
            });
            Assert.True(window.Completed || window.NextOffset > offset);
            offset = window.NextOffset;
        } while (!window.Completed);

        Assert.Equal(body.Length, offset);
        Assert.Equal(
            [("8.8.8.8", 80, ProxyProtocol.Http), ("1.1.1.1", 1080, ProxyProtocol.Socks5), ("9.9.9.9", 443, ProxyProtocol.Https)],
            accepted.Select(candidate => candidate.ToEndpoint()));
    }

    [Fact]
    public void RejectedCandidateIsRetriedBeforeAdvancing()
    {
        const string body = "8.8.8.8:80\nsocks5://1.1.1.1:1080";
        var calls = 0;
        var first = ProxyParser.ParseWindowTo(body, ProxyProtocol.Http, 0, 20, _ => ++calls == 1);
        Assert.Equal(1, first.Count);
        Assert.False(first.Completed);
        Assert.Equal(body.IndexOf("1.1.1.1", StringComparison.Ordinal), first.NextOffset);
        ProxyCandidateKey retried = default;
        var second = ProxyParser.ParseWindowTo(body, ProxyProtocol.Http, first.NextOffset, 20, candidate =>
        {
            retried = candidate;
            return true;
        });
        Assert.True(second.Completed);
        Assert.Equal(1, second.Count);
        Assert.Equal(("1.1.1.1", 1080, ProxyProtocol.Socks5), retried.ToEndpoint());
    }

    [Fact]
    public void FullConsumerCanStopBeforeFirstCandidateWithoutLosingIt()
    {
        const string body = "8.8.8.8:80";
        Assert.Equal(new ProxyParseWindow(0, 0, false),
            ProxyParser.ParseWindowTo(body, ProxyProtocol.Http, 0, 1, _ => false));
    }

    [Fact]
    public void DuplicateTailDoesNotRequireAnotherWindow()
    {
        const string body = "8.8.8.8:80\n8.8.8.8:80\n8.8.8.8:80";
        var calls = 0;
        var window = ProxyParser.ParseWindowTo(body, ProxyProtocol.Http, 0, 1, _ => { calls++; return true; });
        Assert.Equal(new ProxyParseWindow(1, body.Length, true), window);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WindowRetainsNetworkSafetyAndTokenBoundaries()
    {
        const string body = "10.0.0.1:80\n010.0.0.1:80\n1.8.8.8.8:80\n8.8.8.8:123456\nuser:pass@8.8.8.8:80\n[2606:4700:4700::1111]:443";
        var accepted = new List<ProxyCandidateKey>();
        var window = ProxyParser.ParseWindowTo(body, ProxyProtocol.Https, 0, 1, candidate =>
        {
            accepted.Add(candidate);
            return true;
        });
        Assert.True(window.Completed);
        Assert.Equal(("2606:4700:4700::1111", 443, ProxyProtocol.Https), Assert.Single(accepted).ToEndpoint());
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(100, 1)]
    [InlineData(0, 0)]
    public void InvalidWindowBoundsAreRejected(int offset, int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProxyParser.ParseWindowTo("8.8.8.8:80", ProxyProtocol.Http, offset, limit, _ => true));

    [Fact]
    public void EndOfBodyIsAnEmptyCompletedWindow()
    {
        const string body = "8.8.8.8:80";
        Assert.Equal(new ProxyParseWindow(0, body.Length, true),
            ProxyParser.ParseWindowTo(body, ProxyProtocol.Http, body.Length, 1, _ => throw new InvalidOperationException()));
    }
}
