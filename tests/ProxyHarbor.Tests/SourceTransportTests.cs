using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class SourceTransportTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(32, 32)]
    [InlineData(100, 32)]
    public void ConnectionLimitMatchesBoundedCollectorConcurrency(int configured, int expected) =>
        Assert.Equal(expected, ServiceCollectionExtensions.SourceConnectionsPerServer(configured));
}
