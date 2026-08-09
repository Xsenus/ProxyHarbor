using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет точные rolling counters и прогноз drain независимо от EF provider.</summary>
public sealed class ValidationTelemetryTests
{
    [Fact]
    public void AggregatesEveryBatchIncludingRepeatedAndDeferredAttempts()
    {
        var now = new DateTimeOffset(2026, 8, 9, 13, 30, 0, TimeSpan.Zero);
        var runs = new[]
        {
            Completed(now.AddSeconds(-6), 8, 2, 2, 4),
            Completed(now, 20, 0, 1, 6),
            Completed(now.AddMinutes(-6), 999, 999, 0, 99),
            new ValidationRun
            {
                LeaseId = Guid.NewGuid(),
                StartedAt = now.AddMinutes(-1),
                FinishedAt = now.AddSeconds(-30),
                Status = "failed"
            },
            new ValidationRun { LeaseId = Guid.NewGuid(), StartedAt = now, Status = "running" }
        };

        var snapshot = ValidationTelemetry.Calculate(runs, now.AddMinutes(-5), due: 90);

        Assert.Equal(30, snapshot.Attempts);
        Assert.Equal(28, snapshot.Checked);
        Assert.Equal(3, snapshot.Alive);
        Assert.Equal(2, snapshot.Deferred);
        Assert.Equal(1, snapshot.FailedRuns);
        Assert.Equal(1, snapshot.ActiveRuns);
        Assert.Equal(3, snapshot.ChecksPerSecond);
        Assert.Equal(30, snapshot.EstimatedDrainSeconds);
    }

    [Fact]
    public void EmptyWindowDoesNotInventRateOrEta()
    {
        var snapshot = ValidationTelemetry.Calculate([], DateTimeOffset.UtcNow.AddMinutes(-5), due: 100);

        Assert.Equal(0, snapshot.Attempts);
        Assert.Equal(0, snapshot.ChecksPerSecond);
        Assert.Null(snapshot.EstimatedDrainSeconds);
    }

    [Fact]
    public void OverlappingReplicaBatchesContributeCombinedThroughput()
    {
        var finishedAt = DateTimeOffset.UtcNow;
        var runs = new[]
        {
            Completed(finishedAt, 10, 0, 1, 10),
            Completed(finishedAt, 10, 0, 2, 10)
        };

        var snapshot = ValidationTelemetry.Calculate(runs, finishedAt.AddMinutes(-5), due: 100);

        Assert.Equal(20, snapshot.Attempts);
        Assert.Equal(2, snapshot.ChecksPerSecond);
        Assert.Equal(50, snapshot.EstimatedDrainSeconds);
    }

    private static ValidationRun Completed(
        DateTimeOffset finishedAt,
        int checkedCount,
        int deferred,
        int alive,
        double durationSeconds) => new()
        {
            LeaseId = Guid.NewGuid(),
            StartedAt = finishedAt.AddSeconds(-durationSeconds),
            FinishedAt = finishedAt,
            Claimed = checkedCount + deferred,
            Checked = checkedCount,
            Deferred = deferred,
            Alive = alive,
            Status = "completed"
        };
}
