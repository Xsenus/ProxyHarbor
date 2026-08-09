using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует recovery cadence планового зашифрованного backup.</summary>
public sealed class BackupWorkerScheduleTests
{
    [Fact]
    public void SuccessfulBackupWaitsConfiguredInterval() =>
        Assert.Equal(TimeSpan.FromHours(24), BackupWorker.NextDelay(24, BackupWorker.CycleOutcome.Succeeded));

    [Fact]
    public void FailedDailyBackupRetriesAfterFifteenMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(15), BackupWorker.NextDelay(24, BackupWorker.CycleOutcome.Failed));

    [Fact]
    public void ClusterLockOwnedByPeerDoesNotCreateRetryStorm() =>
        Assert.Equal(TimeSpan.FromHours(24), BackupWorker.NextDelay(24, BackupWorker.CycleOutcome.PeerOwned));
}
