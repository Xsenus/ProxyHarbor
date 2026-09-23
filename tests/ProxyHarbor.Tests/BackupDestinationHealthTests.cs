using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class BackupDestinationHealthTests
{
    [Fact]
    public async Task TransientPutFailuresOpenOnlyPutAndHalfOpenClosesOnSuccess()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var gate = new BackupDestinationHealth(clock);
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();

        Assert.True((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        gate.RecordFailure(destinationId, BackupDestinationOperation.Put,
            BackupDestinationErrorCode.Unavailable);
        gate.RecordFailure(destinationId, BackupDestinationOperation.Put,
            BackupDestinationErrorCode.Timeout);
        Assert.False((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        Assert.True((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Verify, CancellationToken.None)).Allowed);

        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        Assert.True((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        Assert.False((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        gate.ReleaseWithoutOutcome(destinationId, BackupDestinationOperation.Put);
        Assert.True((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        gate.RecordSuccess(destinationId, BackupDestinationOperation.Put);
        Assert.True((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task DurableAuthenticationFailureOpensNewReplicaUntilHalfOpen()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();
        var copy = new BackupCopy
        {
            BackupDestinationId = destinationId,
            LastAttemptAt = clock.GetUtcNow().AddSeconds(-10),
            ContentSha256 = new string('a', 64),
            SizeBytes = 5
        };
        copy.Jobs.Add(new BackupDeliveryJob
        {
            State = "failed",
            LastErrorCode = BackupDestinationErrorCode.AuthenticationFailed.ToString(),
            IdempotencyKey = "durable-auth"
        });
        db.BackupCopies.Add(copy);
        await db.SaveChangesAsync();

        var firstReplica = new BackupDestinationHealth(clock);
        var secondReplica = new BackupDestinationHealth(clock);
        Assert.False((await firstReplica.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        Assert.False((await secondReplica.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await secondReplica.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        Assert.False((await secondReplica.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task StaleDurableFailureDoesNotReopenDestination()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();
        var copy = new BackupCopy
        {
            BackupDestinationId = destinationId,
            LastAttemptAt = clock.GetUtcNow().AddMinutes(-10),
            ContentSha256 = new string('a', 64),
            SizeBytes = 5
        };
        copy.Jobs.Add(new BackupDeliveryJob
        {
            State = "failed",
            LastErrorCode = BackupDestinationErrorCode.AuthenticationFailed.ToString(),
            IdempotencyKey = "stale-auth"
        });
        db.BackupCopies.Add(copy);
        await db.SaveChangesAsync();

        Assert.True((await new BackupDestinationHealth(clock).TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task TwoDurableTransientFailuresOpenNewReplica()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();
        foreach (var index in new[] { 1, 2 })
        {
            var copy = new BackupCopy
            {
                BackupDestinationId = destinationId,
                LastAttemptAt = clock.GetUtcNow().AddSeconds(-index * 10),
                ContentSha256 = new string('a', 64),
                SizeBytes = 5
            };
            copy.Jobs.Add(new BackupDeliveryJob
            {
                State = "failed",
                LastErrorCode = BackupDestinationErrorCode.Unavailable.ToString(),
                IdempotencyKey = $"transient-{index}"
            });
            db.BackupCopies.Add(copy);
        }
        await db.SaveChangesAsync();

        Assert.False((await new BackupDestinationHealth(clock).TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task DurablePutFailureIsNotHiddenByLaterCompletedJob()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();
        var copy = new BackupCopy
        {
            BackupDestinationId = destinationId,
            LastAttemptAt = clock.GetUtcNow(),
            ContentSha256 = new string('a', 64),
            SizeBytes = 5
        };
        copy.Jobs.Add(new BackupDeliveryJob { State = "completed", IdempotencyKey = "later-success" });
        db.BackupCopies.Add(copy);
        db.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
        {
            BackupDestinationId = destinationId,
            Operation = "put",
            Succeeded = false,
            ErrorCode = BackupDestinationErrorCode.AuthenticationFailed.ToString(),
            ObservedAt = clock.GetUtcNow().AddSeconds(-10)
        });
        await db.SaveChangesAsync();

        Assert.False((await new BackupDestinationHealth(clock).TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        Assert.True((await new BackupDestinationHealth(clock).TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Verify, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task LaterDurablePutSuccessClosesFailureOnNewReplica()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();
        db.BackupDestinationHealthOutcomes.AddRange(
            new BackupDestinationHealthOutcome
            {
                BackupDestinationId = destinationId,
                Operation = "put",
                Succeeded = false,
                ErrorCode = BackupDestinationErrorCode.AuthenticationFailed.ToString(),
                ObservedAt = clock.GetUtcNow().AddSeconds(-20)
            },
            new BackupDestinationHealthOutcome
            {
                BackupDestinationId = destinationId,
                Operation = "put",
                Succeeded = true,
                ObservedAt = clock.GetUtcNow().AddSeconds(-10)
            });
        await db.SaveChangesAsync();

        Assert.True((await new BackupDestinationHealth(clock).TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task DurableVerifyAuthenticationFailureOpensOnlyVerifyOnNewReplica()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();
        db.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
        {
            BackupDestinationId = destinationId,
            Succeeded = false,
            ErrorCode = BackupDestinationErrorCode.AuthenticationFailed.ToString(),
            ObservedAt = clock.GetUtcNow().AddSeconds(-10)
        });
        await db.SaveChangesAsync();

        var newReplica = new BackupDestinationHealth(clock);
        Assert.False((await newReplica.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Verify, CancellationToken.None)).Allowed);
        Assert.True((await newReplica.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await newReplica.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Verify, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task DurableVerifyTransientFailuresAndLaterSuccessSurviveReplicaChange()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();
        foreach (var secondsAgo in new[] { 20, 10 })
        {
            db.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
            {
                BackupDestinationId = destinationId,
                Succeeded = false,
                ErrorCode = BackupDestinationErrorCode.Unavailable.ToString(),
                ObservedAt = clock.GetUtcNow().AddSeconds(-secondsAgo)
            });
        }
        await db.SaveChangesAsync();
        Assert.False((await new BackupDestinationHealth(clock).TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Verify, CancellationToken.None)).Allowed);

        db.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
        {
            BackupDestinationId = destinationId,
            Succeeded = true,
            ObservedAt = clock.GetUtcNow().AddSeconds(-1)
        });
        await db.SaveChangesAsync();
        Assert.True((await new BackupDestinationHealth(clock).TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Verify, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task ContentCollisionDoesNotOpenDestinationBreaker()
    {
        var gate = new BackupDestinationHealth();
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();
        gate.RecordFailure(destinationId, BackupDestinationOperation.Put,
            BackupDestinationErrorCode.Collision);
        gate.RecordFailure(destinationId, BackupDestinationOperation.Put,
            BackupDestinationErrorCode.IntegrityMismatch);

        Assert.True((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
    }

    [Theory]
    [InlineData(BackupDestinationErrorCode.AuthenticationFailed)]
    [InlineData(BackupDestinationErrorCode.AuthorizationFailed)]
    [InlineData(BackupDestinationErrorCode.QuotaExceeded)]
    public async Task ConfigurationAndQuotaFailuresOpenOnlyAffectedOperation(
        BackupDestinationErrorCode error)
    {
        var gate = new BackupDestinationHealth();
        var destinationId = Guid.NewGuid();
        await using var db = EmptyDatabase();
        gate.RecordFailure(destinationId, BackupDestinationOperation.Put, error);

        Assert.False((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        Assert.True((await gate.TryEnterAsync(
            db, destinationId, BackupDestinationOperation.Verify, CancellationToken.None)).Allowed);
    }

    [Fact]
    public void OverallDeadlineReservesTimeForOtherDestinations()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new BackupDeliveryJob { CreatedAt = now };
        var pool = new BackupPool { OverallDeadlineSeconds = 30 };

        Assert.Equal(TimeSpan.FromSeconds(15),
            BackupDeliveryProcessor.RemainingOperationBudget(job, pool, 2, now));
        Assert.Equal(TimeSpan.FromSeconds(9),
            BackupDeliveryProcessor.RemainingOperationBudget(job, pool, 2, now.AddSeconds(21)));
        Assert.True(BackupDeliveryProcessor.RemainingOperationBudget(
            job, pool, 2, now.AddSeconds(31)) <= TimeSpan.Zero);
    }

    private static ProxyHarborDbContext EmptyDatabase() => new(
        new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"backup-health-{Guid.NewGuid():N}").Options);

    private sealed class AdjustableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
