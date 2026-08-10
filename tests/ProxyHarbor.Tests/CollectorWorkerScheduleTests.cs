using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует cadence и anti-storm поведение фонового production-сборщика.</summary>
public sealed class CollectorWorkerScheduleTests
{
    [Fact]
    public void SuccessfulFastCyclePreservesStartToStartInterval() =>
        Assert.Equal(
            TimeSpan.FromMinutes(13),
            CollectorWorker.NextDelay(
                intervalMinutes: 15,
                CollectorWorker.CycleOutcome.Succeeded,
                elapsed: TimeSpan.FromMinutes(2)));

    [Fact]
    public void SuccessfulOverrunGetsCooldownInsteadOfImmediateRestart() =>
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            CollectorWorker.NextDelay(
                intervalMinutes: 15,
                CollectorWorker.CycleOutcome.Succeeded,
                elapsed: TimeSpan.FromMinutes(16)));

    [Fact]
    public void FailureRetriesAfterOneMinuteWithoutWaitingFullProductionInterval() =>
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            CollectorWorker.NextDelay(
                intervalMinutes: 15,
                CollectorWorker.CycleOutcome.Failed,
                elapsed: TimeSpan.FromSeconds(5)));

    [Fact]
    public void PeerOwnedClusterLockWaitsFullInterval() =>
        Assert.Equal(
            TimeSpan.FromMinutes(15),
            CollectorWorker.NextDelay(
                intervalMinutes: 15,
                CollectorWorker.CycleOutcome.PeerOwned,
                elapsed: TimeSpan.Zero));

    [Fact]
    public void MinimumConfiguredIntervalBoundsFailureRetry() =>
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            CollectorWorker.NextDelay(
                intervalMinutes: 0,
                CollectorWorker.CycleOutcome.Failed,
                elapsed: TimeSpan.Zero));

    [Fact]
    public void UnknownOutcomeFailsClosed() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CollectorWorker.NextDelay(
            intervalMinutes: 15,
            (CollectorWorker.CycleOutcome)999,
            elapsed: TimeSpan.Zero));
}
