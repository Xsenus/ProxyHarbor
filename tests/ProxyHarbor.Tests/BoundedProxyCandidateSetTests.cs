using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует точную семантику глобального лимита при параллельном сборе.</summary>
public sealed class BoundedProxyCandidateSetTests
{
    [Fact]
    public void ExactCapacityAndDuplicatesDoNotReportTruncation()
    {
        var candidates = new BoundedProxyCandidateSet(2);
        var first = ("1.1.1.1", 80, ProxyProtocol.Http);
        var second = ("8.8.8.8", 443, ProxyProtocol.Https);

        Assert.True(candidates.TryAdd(first));
        Assert.True(candidates.TryAdd(second));
        Assert.False(candidates.TryAdd(first));

        Assert.Equal(2, candidates.Count);
        Assert.False(candidates.LimitReached);
        Assert.Equal([first, second], candidates.Items.OrderBy(item => item.Host).ToArray());
    }

    [Fact]
    public void FirstDiscardedUniqueCandidateReportsTruncationWithoutGrowingSet()
    {
        var candidates = new BoundedProxyCandidateSet(1);
        var retained = ("1.1.1.1", 80, ProxyProtocol.Http);
        var discarded = ("8.8.8.8", 1080, ProxyProtocol.Socks5);

        Assert.True(candidates.TryAdd(retained));
        Assert.False(candidates.TryAdd(discarded));

        Assert.Equal(1, candidates.Count);
        Assert.True(candidates.LimitReached);
        Assert.Equal([retained], candidates.Items);
    }

    [Fact]
    public void ParallelUniqueCandidatesNeverExceedCapacity()
    {
        const int limit = 1_000;
        const int attempts = 25_000;
        var candidates = new BoundedProxyCandidateSet(limit);

        Parallel.For(0, attempts, index =>
        {
            var secondOctet = index / (254 * 254);
            var thirdOctet = index / 254 % 254 + 1;
            var fourthOctet = index % 254 + 1;
            _ = candidates.TryAdd(($"11.{secondOctet}.{thirdOctet}.{fourthOctet}", 10_000 + index % 50_000, ProxyProtocol.Socks4));
        });

        Assert.Equal(limit, candidates.Count);
        Assert.Equal(limit, candidates.Items.Count());
        Assert.Equal(limit, candidates.Items.Distinct().Count());
        Assert.True(candidates.LimitReached);
    }

    [Fact]
    public void ParallelDuplicatesAtExactCapacityDoNotCreateFalseAlarm()
    {
        const int limit = 250;
        var candidates = new BoundedProxyCandidateSet(limit);

        Parallel.For(0, 20_000, index =>
        {
            var endpoint = index % limit;
            _ = candidates.TryAdd(($"12.0.0.{endpoint + 1}", 8080, ProxyProtocol.Http));
        });

        Assert.Equal(limit, candidates.Count);
        Assert.False(candidates.LimitReached);
    }
}
