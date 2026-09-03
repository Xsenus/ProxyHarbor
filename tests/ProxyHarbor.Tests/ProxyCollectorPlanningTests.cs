using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ProxyCollectorPlanningTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100_000, true)]
    [InlineData(100_001, false)]
    [InlineData(1_000_000, false)]
    public void IndexedRefreshPlanIsUsedOnlyForSmallNonEmptyImports(
        int candidateCount,
        bool expected)
    {
        Assert.Equal(expected, ProxyCollector.PreferIndexedLastSeenRefresh(candidateCount));
    }

    [Fact]
    public void IndexedRefreshPlanRejectsInvalidCandidateCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProxyCollector.PreferIndexedLastSeenRefresh(-1));
    }
}
