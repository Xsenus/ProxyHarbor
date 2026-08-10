using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует адаптивную частоту проверок и защиту от бесконечного роста backoff.</summary>
public sealed class ProxyCheckSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly CollectorOptions Options = new()
    {
        ValidationIntervalMinutes = 5,
        DeadRetryBaseMinutes = 15,
        DeadRetryMaxHours = 24
    };

    [Fact]
    public void AliveProxyResetsStreakAndUsesFastInterval()
    {
        var scheduled = ProxyCheckScheduler.Create(Result(alive: true), 9, Guid.NewGuid(), Now, Options);

        Assert.Equal(0, scheduled.FailureStreak);
        Assert.Equal(Now.AddMinutes(5), scheduled.NextCheckAt);
    }

    [Theory]
    [InlineData(0, 1, 15)]
    [InlineData(1, 2, 30)]
    [InlineData(2, 3, 60)]
    [InlineData(6, 7, 960)]
    [InlineData(20, 21, 1440)]
    public void DeadProxyUsesCappedExponentialBackoff(int previousStreak, int expectedStreak, int expectedMinutes)
    {
        var scheduled = ProxyCheckScheduler.Create(Result(alive: false), previousStreak, Guid.NewGuid(), Now, Options);

        Assert.Equal(expectedStreak, scheduled.FailureStreak);
        Assert.Equal(Now.AddMinutes(expectedMinutes), scheduled.NextCheckAt);
    }

    [Fact]
    public void DatabaseStringsAreTruncatedToSchemaLimits()
    {
        var result = new ProxyCheckResult(Guid.NewGuid(), false, null, new string('1', 100), false, new string('e', 800));

        var scheduled = ProxyCheckScheduler.Create(result, 0, Guid.NewGuid(), Now, Options);

        Assert.Equal(64, scheduled.ExitIp!.Length);
        Assert.Equal(500, scheduled.Error!.Length);
    }

    [Fact]
    public void DeferredProbePreservesQualityAndRetriesSoon()
    {
        var result = new ProxyCheckResult(
            Guid.NewGuid(), false, null, null, false, "control unavailable", IsDeferred: true);

        var scheduled = ProxyCheckScheduler.Create(result, 7, Guid.NewGuid(), Now, Options);

        Assert.Equal(ProxyCheckOutcome.Deferred, scheduled.Outcome);
        Assert.Equal(7, scheduled.FailureStreak);
        Assert.Equal(Now.AddMinutes(1), scheduled.NextCheckAt);
    }

    [Theory]
    [InlineData(1, 120)]
    [InlineData(30, 120)]
    [InlineData(31, 122)]
    [InlineData(120, 300)]
    [InlineData(int.MaxValue, 300)]
    public void ValidationLeaseDurationIsShortAndBounded(int probeTimeoutSeconds, int expectedSeconds)
    {
        var duration = ValidationLeasePolicy.Duration(probeTimeoutSeconds);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
        Assert.Equal(TimeSpan.FromTicks(duration.Ticks / 3), ValidationLeasePolicy.RenewalInterval(duration));
    }

    [Fact]
    public async Task LeaseHeartbeatRetriesWhenFailureObserverAlsoThrows()
    {
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        var failures = 0;

        await ProxyValidator.MaintainLeaseWithRetryAsync(
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMilliseconds(1),
            (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    return Task.FromException<int>(new IOException("transient database failure"));

                stop.Cancel();
                return Task.FromResult(1);
            },
            _ =>
            {
                Interlocked.Increment(ref failures);
                throw new InvalidOperationException("Deterministic logging provider failure.");
            },
            stop.Token).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, attempts);
        Assert.Equal(1, failures);
    }

    private static ProxyCheckResult Result(bool alive) =>
        new(Guid.NewGuid(), alive, alive ? 100 : null, alive ? "1.1.1.1" : null, alive, alive ? null : "failed");
}
