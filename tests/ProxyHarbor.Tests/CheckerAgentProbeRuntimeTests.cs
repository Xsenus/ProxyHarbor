using ProxyHarbor.CheckerAgent;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Tests;

public sealed class CheckerAgentProbeRuntimeTests
{
    [Fact]
    public void GetReusesRuntimeForUnchangedControlConfiguration()
    {
        using var runtime = new CheckerAgentProbeRuntime(new UnusedHttpClientFactory());
        var first = runtime.Get(Lease("api.ipify.org", 443, "/?format=json", 10));
        var second = runtime.Get(Lease("api.ipify.org", 443, "/?format=json", 10));

        Assert.Same(first, second);
    }

    [Theory]
    [InlineData("api64.ipify.org", 443, "/?format=json", 10)]
    [InlineData("api.ipify.org", 8443, "/?format=json", 10)]
    [InlineData("api.ipify.org", 443, "/ip", 10)]
    [InlineData("api.ipify.org", 443, "/?format=json", 11)]
    public void GetReplacesRuntimeWhenControlConfigurationChanges(
        string host, int port, string path, int timeoutSeconds)
    {
        using var runtime = new CheckerAgentProbeRuntime(new UnusedHttpClientFactory());
        var first = runtime.Get(Lease("api.ipify.org", 443, "/?format=json", 10));
        var second = runtime.Get(Lease(host, port, path, timeoutSeconds));

        Assert.NotSame(first, second);
    }

    private static CheckerLeaseResponse Lease(string host, int port, string path, int timeoutSeconds) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), 1, timeoutSeconds,
            host, port, path, []);

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Unit test must not resolve external HTTP clients.");
    }
}
