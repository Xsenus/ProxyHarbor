using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет cadence и bounded retry фонового VPN collection.</summary>
public sealed class VpnCollectorWorkerScheduleTests
{
    [Fact]
    public void SuccessfulCyclePreservesStartToStartInterval() =>
        Assert.Equal(
            TimeSpan.FromMinutes(4.5),
            VpnCollectorWorker.NextDelay(
                intervalMinutes: 5,
                VpnCollectorWorker.CycleOutcome.Succeeded,
                elapsed: TimeSpan.FromSeconds(30)));

    [Fact]
    public void SuccessfulOverrunGetsCooldownInsteadOfImmediateRestart() =>
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            VpnCollectorWorker.NextDelay(
                intervalMinutes: 5,
                VpnCollectorWorker.CycleOutcome.Succeeded,
                elapsed: TimeSpan.FromMinutes(6)));

    [Fact]
    public void FailureRetriesAfterOneMinute() =>
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            VpnCollectorWorker.NextDelay(
                intervalMinutes: 5,
                VpnCollectorWorker.CycleOutcome.Failed,
                elapsed: TimeSpan.FromSeconds(15)));

    [Fact]
    public void PeerOwnedLockWaitsFullInterval() =>
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            VpnCollectorWorker.NextDelay(
                intervalMinutes: 5,
                VpnCollectorWorker.CycleOutcome.PeerOwned,
                elapsed: TimeSpan.FromSeconds(15)));

    [Fact]
    public void MinimumIntervalBoundsFailureRetry() =>
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            VpnCollectorWorker.NextDelay(
                intervalMinutes: 0,
                VpnCollectorWorker.CycleOutcome.Failed,
                elapsed: TimeSpan.Zero));

    [Fact]
    public void UnknownOutcomeFailsClosed() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VpnCollectorWorker.NextDelay(
            intervalMinutes: 5,
            (VpnCollectorWorker.CycleOutcome)999,
            elapsed: TimeSpan.Zero));
}
