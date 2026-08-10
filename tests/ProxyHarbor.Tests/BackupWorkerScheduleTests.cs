using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует recovery cadence планового зашифрованного backup.</summary>
public sealed class BackupWorkerScheduleTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SuccessfulBackupWaitsConfiguredInterval() =>
        Assert.Equal(TimeSpan.FromHours(24), BackupWorker.NextDelay(24, BackupWorker.CycleOutcome.Succeeded));

    [Fact]
    public void FailedDailyBackupRetriesAfterFifteenMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(15), BackupWorker.NextDelay(24, BackupWorker.CycleOutcome.Failed));

    [Fact]
    public void ClusterLockOwnedByPeerDoesNotCreateRetryStorm() =>
        Assert.Equal(TimeSpan.FromHours(24), BackupWorker.NextDelay(24, BackupWorker.CycleOutcome.PeerOwned));

    [Fact]
    public void MissingSuccessfulHistoryRunsImmediately() =>
        Assert.Equal(TimeSpan.Zero, BackupWorker.InitialDelay(24, lastCompletedAt: null, Now));

    [Fact]
    public void RestartResumesRemainingConfiguredInterval() =>
        Assert.Equal(
            TimeSpan.FromHours(18),
            BackupWorker.InitialDelay(24, Now.AddHours(-6), Now));

    [Fact]
    public void OverdueBackupRunsImmediately() =>
        Assert.Equal(
            TimeSpan.Zero,
            BackupWorker.InitialDelay(24, Now.AddHours(-25), Now));

    [Fact]
    public void FutureAuditIsBoundedToOneInterval() =>
        Assert.Equal(
            TimeSpan.FromHours(24),
            BackupWorker.InitialDelay(24, Now.AddHours(1), Now));

    [Fact]
    public void YearlyIntervalIsSplitIntoPortableDailyTimerChunks() =>
        Assert.Equal(
            TimeSpan.FromDays(1),
            BackupWorker.WaitChunk(TimeSpan.FromDays(365)));

    [Fact]
    public void ShortRecoveryDelayIsNotExtended() =>
        Assert.Equal(
            TimeSpan.FromMinutes(15),
            BackupWorker.WaitChunk(TimeSpan.FromMinutes(15)));

    [Fact]
    public async Task PersistentScheduleIgnoresFailedAndRunningAudits()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"backup-worker-schedule-{Guid.NewGuid():N}")
            .Options;
        var expected = Now.AddHours(-6);
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.BackupRuns.AddRange(
                new BackupRun { StartedAt = Now.AddHours(-8), FinishedAt = expected, Status = "completed" },
                new BackupRun { StartedAt = Now.AddHours(-2), FinishedAt = Now.AddHours(-1), Status = "failed" },
                new BackupRun { StartedAt = Now, Status = "running" });
            await seed.SaveChangesAsync();
        }

        var completedAt = await BackupWorker.ReadLastCompletedAtAsync(
            new TestDbFactory(options), CancellationToken.None);

        Assert.Equal(expected, completedAt);
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
