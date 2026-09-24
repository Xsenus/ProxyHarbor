using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupDeliveryWorkerIntegrationTests
{
    [Fact]
    public void RetryDelayIsBoundedAndDeterministic()
    {
        var jobId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        Assert.Equal(
            BackupDeliveryProcessor.RetryDelay(jobId, 3),
            BackupDeliveryProcessor.RetryDelay(jobId, 3));
        Assert.InRange(BackupDeliveryProcessor.RetryDelay(jobId, 1),
            TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(17));
        Assert.InRange(BackupDeliveryProcessor.RetryDelay(jobId, 100),
            TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
    }

    [Fact]
    public async Task PlannerExcludesDrainingAndOperationIncompatibleRoutes()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"delivery-plan-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ProxyHarborDbContext(options);
        var pool = Pool();
        var run = Run(pool.Id, new string('a', 64), "planner.phbackup");
        var s3 = Destination("draining-s3", "domain-a", 10);
        var telegram = new BackupDestination
        {
            Name = "read-only-telegram",
            Kind = "telegram",
            Enabled = true,
            FailureDomain = "telegram",
            Priority = 20
        };
        db.AddRange(pool, run, s3, telegram);
        db.BackupPoolDestinations.AddRange(
            new BackupPoolDestination
            {
                BackupPoolId = pool.Id,
                BackupDestinationId = s3.Id,
                Enabled = false,
                Draining = true,
                AllowedOperations = "put,verify,read"
            },
            new BackupPoolDestination
            {
                BackupPoolId = pool.Id,
                BackupDestinationId = telegram.Id,
                Enabled = true,
                AllowedOperations = "read"
            });
        await db.SaveChangesAsync();
        var planner = new BackupDeliveryPlanner(Registry(
            new SuccessfulAdapter("s3"),
            new SuccessfulAdapter("telegram")));

        Assert.Equal(0, await planner.PlanAsync(
            db, run.Id, run.ContentSha256!, run.SizeBytes, CancellationToken.None));
        Assert.Empty(db.BackupDeliveryJobs);
    }

    [Fact]
    public async Task PlannerLimitsCatchUpToOneNewCopyPerPass()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"delivery-limit-{Guid.NewGuid():N}").Options;
        await using var db = new ProxyHarborDbContext(options);
        var pool = Pool();
        var run = Run(pool.Id, new string('a', 64), "limit.phbackup");
        var first = Destination("limit-first", "domain-a", 10);
        var second = Destination("limit-second", "domain-b", 20);
        db.AddRange(pool, run, first, second,
            Route(pool.Id, first.Id, 10), Route(pool.Id, second.Id, 20));
        await db.SaveChangesAsync();
        var planner = new BackupDeliveryPlanner(Registry(
            new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram")));

        Assert.Equal(1, await planner.PlanAsync(
            db, run.Id, run.ContentSha256!, run.SizeBytes, CancellationToken.None, maxNewCopies: 1));
        Assert.Equal(1, await planner.PlanAsync(
            db, run.Id, run.ContentSha256!, run.SizeBytes, CancellationToken.None, maxNewCopies: 1));
        Assert.Equal(0, await planner.PlanAsync(
            db, run.Id, run.ContentSha256!, run.SizeBytes, CancellationToken.None, maxNewCopies: 1));
        Assert.Equal(2, await db.BackupDeliveryJobs.CountAsync());
    }

    [Fact]
    public async Task PlannerRejectsChangedPoolPolicyBeforeCreatingCopies()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"delivery-stale-policy-{Guid.NewGuid():N}").Options;
        await using var db = new ProxyHarborDbContext(options);
        var pool = Pool();
        pool.PolicyVersion++;
        var run = Run(pool.Id, new string('a', 64), "stale.phbackup");
        var destination = Destination("stale-destination", "domain-a", 10);
        db.AddRange(pool, run, destination, Route(pool.Id, destination.Id, 10));
        await db.SaveChangesAsync();
        var planner = new BackupDeliveryPlanner(Registry(
            new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(
            db, run.Id, run.ContentSha256!, run.SizeBytes, CancellationToken.None));
        Assert.Empty(db.BackupCopies);
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("completed", false)]
    public async Task PlannerRequiresCompletedRunAndExistingPool(string status, bool includePool)
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"delivery-incomplete-{Guid.NewGuid():N}").Options;
        await using var db = new ProxyHarborDbContext(options);
        var pool = Pool();
        var run = Run(pool.Id, new string('a', 64), "incomplete.phbackup");
        run.Status = status;
        db.BackupRuns.Add(run);
        if (includePool) db.BackupPools.Add(pool);
        await db.SaveChangesAsync();
        var planner = new BackupDeliveryPlanner(Registry(
            new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(
            db, run.Id, run.ContentSha256!, run.SizeBytes, CancellationToken.None));
        Assert.Empty(db.BackupDeliveryJobs);
    }

    [Fact]
    public async Task PlannerRejectsNonPositivePerPassLimit()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"delivery-invalid-limit-{Guid.NewGuid():N}").Options;
        await using var db = new ProxyHarborDbContext(options);
        var planner = new BackupDeliveryPlanner(Registry(
            new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram")));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => planner.PlanAsync(
            db, Guid.NewGuid(), new string('a', 64), 5, CancellationToken.None, maxNewCopies: 0));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("not-verified", false)]
    [InlineData("no-verified-at", false)]
    [InlineData("no-locator", false)]
    [InlineData("wrong-policy", false)]
    [InlineData("wrong-hash", false)]
    [InlineData("wrong-size", false)]
    [InlineData("no-route", false)]
    [InlineData("no-read", false)]
    [InlineData("disabled-destination", false)]
    public void CatchUpRequiresExactReadableVerifiedSource(string scenario, bool expected)
    {
        var pool = Pool();
        var run = Run(pool.Id, new string('a', 64), "source.phbackup");
        var destination = Destination("read-source", "domain-a", 10);
        var copy = Copy(run.Id, destination.Id, run.ContentSha256!);
        copy.BackupDestination = destination;
        copy.State = "verified";
        copy.VerifiedAt = DateTimeOffset.UtcNow;
        copy.NativeLocator = "source/source.phbackup";
        var routes = new Dictionary<Guid, BackupPoolDestination>
        {
            [destination.Id] = Route(pool.Id, destination.Id, 10)
        };
        switch (scenario)
        {
            case "not-verified": copy.State = "quarantined"; break;
            case "no-verified-at": copy.VerifiedAt = null; break;
            case "no-locator": copy.NativeLocator = null; break;
            case "wrong-policy": copy.PolicyVersion++; break;
            case "wrong-hash": copy.ContentSha256 = new string('b', 64); break;
            case "wrong-size": copy.SizeBytes++; break;
            case "no-route": routes.Clear(); break;
            case "no-read": routes[destination.Id].AllowedOperations = "put,verify"; break;
            case "disabled-destination": destination.Enabled = false; break;
        }

        Assert.Equal(expected, BackupCatchUpPlanner.HasReadableSource(
            run, [copy], routes,
            Registry(new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram"))));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("put-started", false)]
    [InlineData("unknown", false)]
    [InlineData("job-not-failed", false)]
    [InlineData("policy-changed", false)]
    [InlineData("cooldown", false)]
    [InlineData("ambiguous-error", false)]
    [InlineData("attempt-marked", false)]
    [InlineData("native-evidence", false)]
    [InlineData("older-ambiguous-error", false)]
    [InlineData("no-route", false)]
    [InlineData("rearm-cap", false)]
    public void PrePutRearmRequiresDurableProofAndCurrentPolicy(string scenario, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var pool = Pool();
        var run = Run(pool.Id, new string('a', 64), "rearm.phbackup");
        var destination = Destination("rearm-target", "domain-b", 20);
        var route = Route(pool.Id, destination.Id, 20);
        var copy = Copy(run.Id, destination.Id, run.ContentSha256!);
        copy.BackupDestination = destination;
        copy.State = "permanent_failed";
        copy.LastErrorCode = BackupDestinationErrorCode.InvalidConfiguration.ToString();
        copy.Jobs.Add(new BackupDeliveryJob
        {
            State = "failed",
            LastErrorCode = copy.LastErrorCode,
            CreatedAt = now.AddMinutes(-21),
            UpdatedAt = now.AddMinutes(-20)
        });
        var routes = new Dictionary<Guid, BackupPoolDestination> { [destination.Id] = route };
        switch (scenario)
        {
            case "put-started": copy.LastAttemptAt = now.AddMinutes(-20); break;
            case "unknown": copy.UnknownSince = now.AddMinutes(-20); break;
            case "job-not-failed": copy.Jobs.Single().State = "reconciling"; break;
            case "policy-changed": pool.PolicyVersion++; break;
            case "cooldown": copy.Jobs.Single().UpdatedAt = now.AddMinutes(-1); break;
            case "ambiguous-error":
                copy.LastErrorCode = BackupDestinationErrorCode.UnknownOutcome.ToString();
                copy.Jobs.Single().LastErrorCode = copy.LastErrorCode;
                break;
            case "attempt-marked": copy.AttemptCount = 1; break;
            case "native-evidence": copy.NativeVersion = "provider-version"; break;
            case "older-ambiguous-error":
                copy.Jobs.Add(new BackupDeliveryJob
                {
                    State = "failed",
                    LastErrorCode = BackupDestinationErrorCode.UnknownOutcome.ToString(),
                    CreatedAt = now.AddMinutes(-22),
                    UpdatedAt = now.AddMinutes(-21)
                });
                break;
            case "no-route": routes.Clear(); break;
            case "rearm-cap":
                copy.Jobs.Add(new BackupDeliveryJob
                {
                    State = "failed",
                    LastErrorCode = copy.LastErrorCode,
                    CreatedAt = now.AddMinutes(-21),
                    UpdatedAt = now.AddMinutes(-20)
                });
                copy.Jobs.Add(new BackupDeliveryJob
                {
                    State = "failed",
                    LastErrorCode = copy.LastErrorCode,
                    CreatedAt = now.AddMinutes(-21),
                    UpdatedAt = now.AddMinutes(-20)
                });
                break;
        }

        Assert.Equal(expected, BackupCatchUpPlanner.CanRearmPrePutFailure(
            copy, run, pool, routes, now));
    }

    [Fact]
    public async Task PolicyVersionChangeFencesAlreadyPlannedJob()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"delivery-policy-{Guid.NewGuid():N}")
            .Options;
        var factory = new TestDbFactory(options);
        Guid jobId;
        var leaseId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var pool = Pool();
            pool.PolicyVersion = 4;
            var run = Run(pool.Id, new string('a', 64), "policy.phbackup");
            var destination = Destination("policy-s3", "domain-a", 10);
            var copy = Copy(run.Id, destination.Id, run.ContentSha256!);
            var job = Job(copy.Id, "policy-job");
            job.State = "processing";
            job.LeaseId = leaseId;
            job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            db.AddRange(pool, run, destination, Route(pool.Id, destination.Id, 10), copy, job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }
        var processor = Processor(
            factory,
            Registry(new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram")),
            Path.GetTempPath());

        await processor.ProcessAsync(new BackupDeliveryLease(jobId, leaseId), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var persisted = await verify.BackupDeliveryJobs.Include(job => job.BackupCopy).SingleAsync();
        Assert.Equal("failed", persisted.State);
        Assert.Equal("permanent_failed", persisted.BackupCopy.State);
        Assert.Equal(BackupDestinationErrorCode.InvalidConfiguration.ToString(), persisted.LastErrorCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingStagingOnlyUsesVerifiedCopyAndRemovesTemporaryFile(bool sourceVerified)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"delivery-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes = [1, 2, 3, 4, 5];
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase($"delivery-repair-{Guid.NewGuid():N}").Options;
            var factory = new TestDbFactory(options);
            var leaseId = Guid.NewGuid();
            Guid jobId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                var run = Run(pool.Id, hash, "expired-staging.phbackup");
                run.StartedAt = DateTimeOffset.UtcNow.AddDays(-2);
                var source = Destination("source", "domain-a", 10);
                var target = Destination("target", "domain-b", 20);
                var sourceCopy = Copy(run.Id, source.Id, hash);
                sourceCopy.State = sourceVerified ? "verified" : "permanent_failed";
                sourceCopy.VerifiedAt = sourceVerified ? DateTimeOffset.UtcNow : null;
                sourceCopy.NativeLocator = "source/expired-staging.phbackup";
                var targetCopy = Copy(run.Id, target.Id, hash);
                var job = Job(targetCopy.Id, "repair-job");
                job.State = "processing";
                job.LeaseId = leaseId;
                job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                db.AddRange(pool, run, source, target,
                    Route(pool.Id, source.Id, 10), Route(pool.Id, target.Id, 20),
                    sourceCopy, targetCopy, job);
                await db.SaveChangesAsync();
                jobId = job.Id;
            }
            var adapter = new RepairAdapter(bytes);
            var registry = Registry(adapter, new SuccessfulAdapter("telegram"));
            var health = new BackupDestinationHealth();
            var processor = new BackupDeliveryProcessor(
                factory, registry,
                Options.Create(new BackupOptions { Directory = directory, EncryptionKey = new string('k', 32) }),
                Options.Create(new BackupRoutingOptions { Enabled = true }),
                destinationHealth: health,
                copyMaterializer: new BackupCopyMaterializer(factory, registry, health));

            await processor.ProcessAsync(new BackupDeliveryLease(jobId, leaseId), CancellationToken.None);

            Assert.Equal(sourceVerified ? 1 : 0, adapter.ReadCalls);
            Assert.Equal(sourceVerified ? 1 : 0, adapter.PutCalls);
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Empty(Directory.GetDirectories(directory));
            await using var verify = await factory.CreateDbContextAsync();
            var persisted = await verify.BackupDeliveryJobs.Include(item => item.BackupCopy).SingleAsync();
            Assert.Equal(sourceVerified ? "completed" : "pending", persisted.State);
            Assert.Equal(sourceVerified ? "verified" : "planned", persisted.BackupCopy.State);
            if (!sourceVerified) Assert.Equal(0, persisted.Attempt);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RetryableFailureReturnsToPendingThenExhaustsPolicyBudget()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"delivery-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "retry.phbackup");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase($"delivery-retry-{Guid.NewGuid():N}")
                .Options;
            var factory = new TestDbFactory(options);
            var firstLease = Guid.NewGuid();
            Guid jobId;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                pool.MaxAttemptsPerCycle = 2;
                var run = Run(pool.Id, hash, Path.GetFileName(path));
                var destination = Destination("retry-s3", "domain-a", 10);
                var copy = Copy(run.Id, destination.Id, hash);
                var job = Job(copy.Id, "retry-job");
                job.State = "processing";
                job.Attempt = 1;
                job.LeaseId = firstLease;
                job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                seed.AddRange(pool, run, destination, Route(pool.Id, destination.Id, 10), copy, job);
                await seed.SaveChangesAsync();
                jobId = job.Id;
            }
            var registry = Registry(
                new FailingAdapter("s3", BackupDestinationFailureDisposition.Retryable),
                new SuccessfulAdapter("telegram"));
            var processor = Processor(factory, registry, directory);

            await processor.ProcessAsync(new BackupDeliveryLease(jobId, firstLease), CancellationToken.None);

            var secondLease = Guid.NewGuid();
            await using (var retry = await factory.CreateDbContextAsync())
            {
                var job = await retry.BackupDeliveryJobs.Include(item => item.BackupCopy).SingleAsync();
                Assert.Equal("pending", job.State);
                Assert.Equal("retryable_failed", job.BackupCopy.State);
                Assert.Null(job.LeaseId);
                Assert.True(job.NotBefore > DateTimeOffset.UtcNow);
                job.State = "processing";
                job.Attempt = 2;
                job.LeaseId = secondLease;
                job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                await retry.SaveChangesAsync();
            }

            await processor.ProcessAsync(new BackupDeliveryLease(jobId, secondLease), CancellationToken.None);

            await using var verify = await factory.CreateDbContextAsync();
            var exhausted = await verify.BackupDeliveryJobs.Include(item => item.BackupCopy).SingleAsync();
            Assert.Equal("failed", exhausted.State);
            Assert.Equal("permanent_failed", exhausted.BackupCopy.State);
            Assert.Equal(BackupDestinationErrorCode.Unavailable.ToString(), exhausted.LastErrorCode);
            var putOutcomes = await verify.BackupDestinationHealthOutcomes
                .OrderBy(item => item.ObservedAt).ToArrayAsync();
            Assert.Equal(2, putOutcomes.Length);
            Assert.All(putOutcomes, item =>
            {
                Assert.Equal("put", item.Operation);
                Assert.False(item.Succeeded);
                Assert.Equal(BackupDestinationErrorCode.Unavailable.ToString(), item.ErrorCode);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DestinationWithoutIndependentVerificationCompletesAsManualReview()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"delivery-manual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "manual.phbackup");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase($"delivery-manual-{Guid.NewGuid():N}")
                .Options;
            var factory = new TestDbFactory(options);
            var leaseId = Guid.NewGuid();
            Guid jobId;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                var run = Run(pool.Id, hash, Path.GetFileName(path));
                var destination = new BackupDestination
                {
                    Name = "manual-telegram",
                    Kind = "telegram",
                    Enabled = true,
                    FailureDomain = "telegram"
                };
                var copy = Copy(run.Id, destination.Id, hash);
                var job = Job(copy.Id, "manual-job");
                job.State = "processing";
                job.Attempt = 1;
                job.LeaseId = leaseId;
                job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                seed.AddRange(pool, run, destination, Route(pool.Id, destination.Id, 10), copy, job);
                await seed.SaveChangesAsync();
                jobId = job.Id;
            }
            var processor = Processor(
                factory,
                Registry(new SuccessfulAdapter("s3"), new UnverifiedAdapter("telegram")),
                directory);

            await processor.ProcessAsync(new BackupDeliveryLease(jobId, leaseId), CancellationToken.None);

            await using var verify = await factory.CreateDbContextAsync();
            var persistedJob = await verify.BackupDeliveryJobs
                .Include(item => item.BackupCopy).ThenInclude(copy => copy.BackupRun)
                .SingleAsync();
            Assert.Equal("completed", persistedJob.State);
            Assert.Equal("manual_review", persistedJob.BackupCopy.State);
            Assert.Null(persistedJob.BackupCopy.VerifiedAt);
            Assert.True(persistedJob.BackupCopy.BackupRun.SentToTelegram);
            Assert.False(persistedJob.BackupCopy.BackupRun.SentToObjectStorage);
            var outcome = Assert.Single(await verify.BackupDestinationHealthOutcomes.ToArrayAsync());
            Assert.Equal("put", outcome.Operation);
            Assert.False(outcome.Succeeded);
            Assert.Equal(BackupDestinationErrorCode.UnsupportedOperation.ToString(), outcome.ErrorCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task UnknownProviderOutcomeRequiresReconciliationInsteadOfBlindRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"delivery-unknown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "unknown.phbackup");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase($"delivery-unknown-{Guid.NewGuid():N}")
                .Options;
            var factory = new TestDbFactory(options);
            var leaseId = Guid.NewGuid();
            Guid jobId;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                var run = Run(pool.Id, hash, Path.GetFileName(path));
                var destination = Destination("unknown-s3", "domain-a", 10);
                var copy = Copy(run.Id, destination.Id, hash);
                var job = Job(copy.Id, "unknown-job");
                job.State = "processing";
                job.Attempt = 1;
                job.LeaseId = leaseId;
                job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                seed.AddRange(pool, run, destination, Route(pool.Id, destination.Id, 10), copy, job);
                await seed.SaveChangesAsync();
                jobId = job.Id;
            }
            var ambiguous = new AmbiguousThenMatchingAdapter();
            var processor = Processor(
                factory, Registry(ambiguous, new SuccessfulAdapter("telegram")), directory);

            await processor.ProcessAsync(new BackupDeliveryLease(jobId, leaseId), CancellationToken.None);

            await using var verify = await factory.CreateDbContextAsync();
            var persistedJob = await verify.BackupDeliveryJobs.Include(item => item.BackupCopy).SingleAsync();
            Assert.Equal("reconciling", persistedJob.State);
            Assert.Equal("unknown", persistedJob.BackupCopy.State);
            Assert.NotNull(persistedJob.BackupCopy.UnknownSince);
            Assert.Null(persistedJob.LeaseId);
            var putOutcome = Assert.Single(await verify.BackupDestinationHealthOutcomes.ToArrayAsync());
            Assert.Equal("put", putOutcome.Operation);
            Assert.False(putOutcome.Succeeded);
            Assert.Equal(BackupDestinationErrorCode.UnknownOutcome.ToString(), putOutcome.ErrorCode);
            var reconcileLease = Guid.NewGuid();
            persistedJob.State = "processing";
            persistedJob.LeaseId = reconcileLease;
            persistedJob.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            await verify.SaveChangesAsync();

            await processor.ProcessReconciliationAsync(
                new BackupDeliveryLease(jobId, reconcileLease), CancellationToken.None);
            await using var resolvedDb = await factory.CreateDbContextAsync();
            var resolved = await resolvedDb.BackupDeliveryJobs.Include(item => item.BackupCopy).SingleAsync();
            Assert.Equal("completed", resolved.State);
            Assert.Equal("verified", resolved.BackupCopy.State);
            Assert.Equal(1, ambiguous.PutCalls);
            Assert.Equal(1, ambiguous.ProbeCalls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(BackupDestinationProbeOutcome.Matching, true, "completed", "verified")]
    [InlineData(BackupDestinationProbeOutcome.Matching, false, "manual_review", "manual_review")]
    [InlineData(BackupDestinationProbeOutcome.Missing, true, "manual_review", "manual_review")]
    [InlineData(BackupDestinationProbeOutcome.Mismatching, true, "failed", "quarantined")]
    [InlineData(BackupDestinationProbeOutcome.Inconclusive, true, "reconciling", "unknown")]
    [InlineData(BackupDestinationProbeOutcome.Unsupported, true, "manual_review", "manual_review")]
    public async Task UnknownOutcomeProbeNeverRepeatsPut(
        BackupDestinationProbeOutcome outcome, bool includeLocator,
        string expectedJobState, string expectedCopyState)
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"delivery-probe-{Guid.NewGuid():N}")
            .Options;
        var factory = new TestDbFactory(options);
        var leaseId = Guid.NewGuid();
        Guid jobId;
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var pool = Pool();
            var run = Run(pool.Id, new string('a', 64), "probe.phbackup");
            var destination = Destination("probe-s3", "domain-a", 10);
            var copy = Copy(run.Id, destination.Id, run.ContentSha256!);
            copy.State = "unknown";
            copy.UnknownSince = DateTimeOffset.UtcNow.AddMinutes(-1);
            var job = Job(copy.Id, "probe-job");
            job.State = "processing";
            job.Attempt = 1;
            job.LeaseId = leaseId;
            job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            seed.AddRange(pool, run, destination, Route(pool.Id, destination.Id, 10), copy, job);
            await seed.SaveChangesAsync();
            jobId = job.Id;
        }
        var adapter = new ProbingAdapter(outcome, includeLocator: includeLocator);
        var processor = Processor(
            factory, Registry(adapter, new SuccessfulAdapter("telegram")), Path.GetTempPath());

        await processor.ProcessReconciliationAsync(new BackupDeliveryLease(jobId, leaseId), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var saved = await verify.BackupDeliveryJobs
            .Include(item => item.BackupCopy).ThenInclude(copy => copy.BackupRun).SingleAsync();
        Assert.Equal(expectedJobState, saved.State);
        Assert.Equal(expectedCopyState, saved.BackupCopy.State);
        Assert.Null(saved.LeaseId);
        Assert.Equal(1, adapter.ProbeCalls);
        Assert.Equal(0, adapter.PutCalls);
        Assert.Equal(expectedCopyState == "verified",
            saved.BackupCopy.BackupRun.SentToObjectStorage);
        Assert.Equal(expectedCopyState == "verified",
            saved.BackupCopy.VerifiedAt.HasValue);
        var healthOutcomes = await verify.BackupDestinationHealthOutcomes.ToArrayAsync();
        if (outcome == BackupDestinationProbeOutcome.Unsupported)
            Assert.Empty(healthOutcomes);
        else
        {
            var healthOutcome = Assert.Single(healthOutcomes);
            Assert.Equal(saved.BackupCopy.BackupDestinationId, healthOutcome.BackupDestinationId);
            Assert.Equal("verify", healthOutcome.Operation);
            Assert.Equal(outcome != BackupDestinationProbeOutcome.Inconclusive, healthOutcome.Succeeded);
            Assert.Equal(outcome == BackupDestinationProbeOutcome.Inconclusive
                ? BackupDestinationErrorCode.Unavailable.ToString() : null, healthOutcome.ErrorCode);
            Assert.Equal(outcome == BackupDestinationProbeOutcome.Matching && !includeLocator
                ? "invalid" : outcome.ToString().ToLowerInvariant(), healthOutcome.ProbeOutcome);
        }
        if (outcome == BackupDestinationProbeOutcome.Inconclusive)
            Assert.True(saved.NotBefore > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task InconclusiveProbeAfterWindowRequiresManualReview()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"delivery-probe-expired-{Guid.NewGuid():N}")
            .Options;
        var factory = new TestDbFactory(options);
        var leaseId = Guid.NewGuid();
        Guid jobId;
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var pool = Pool();
            var run = Run(pool.Id, new string('a', 64), "expired.phbackup");
            var destination = Destination("expired-s3", "domain-a", 10);
            var copy = Copy(run.Id, destination.Id, run.ContentSha256!);
            copy.State = "unknown";
            copy.UnknownSince = DateTimeOffset.UtcNow.AddHours(-2);
            var job = Job(copy.Id, "expired-probe-job");
            job.State = "processing";
            job.Attempt = 1;
            job.LeaseId = leaseId;
            job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            seed.AddRange(pool, run, destination, Route(pool.Id, destination.Id, 10), copy, job);
            await seed.SaveChangesAsync();
            jobId = job.Id;
        }
        var adapter = new ProbingAdapter(BackupDestinationProbeOutcome.Inconclusive);
        var processor = Processor(
            factory, Registry(adapter, new SuccessfulAdapter("telegram")), Path.GetTempPath());

        await processor.ProcessReconciliationAsync(new BackupDeliveryLease(jobId, leaseId), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var saved = await verify.BackupDeliveryJobs.Include(item => item.BackupCopy).SingleAsync();
        Assert.Equal("manual_review", saved.State);
        Assert.Equal("manual_review", saved.BackupCopy.State);
        Assert.NotNull(saved.BackupCopy.UnknownSince);
        Assert.Null(saved.BackupCopy.VerifiedAt);
        Assert.Equal(0, adapter.PutCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedProbeFailureIsDurableAndOpensOnlyVerifyOnNewReplica(
        bool adapterReturnsFailure)
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"delivery-probe-auth-{Guid.NewGuid():N}").Options;
        var factory = new TestDbFactory(options);
        var leaseId = Guid.NewGuid();
        Guid jobId;
        Guid destinationId;
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var pool = Pool();
            var run = Run(pool.Id, new string('a', 64), "auth-probe.phbackup");
            var destination = Destination("auth-probe", "domain-a", 10);
            destinationId = destination.Id;
            var copy = Copy(run.Id, destinationId, run.ContentSha256!);
            copy.State = "unknown";
            copy.UnknownSince = DateTimeOffset.UtcNow.AddMinutes(-1);
            var job = Job(copy.Id, "auth-probe-job");
            job.State = "processing";
            job.Attempt = 1;
            job.LeaseId = leaseId;
            job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            seed.AddRange(pool, run, destination, Route(pool.Id, destinationId, 10), copy, job);
            await seed.SaveChangesAsync();
            jobId = job.Id;
        }
        var adapter = new ProbingAdapter(BackupDestinationProbeOutcome.Inconclusive,
            BackupDestinationErrorCode.AuthenticationFailed,
            returnTypedFailure: adapterReturnsFailure);
        var processor = Processor(
            factory, Registry(adapter, new SuccessfulAdapter("telegram")), Path.GetTempPath());

        await processor.ProcessReconciliationAsync(
            new BackupDeliveryLease(jobId, leaseId), CancellationToken.None);

        await using var replica = await factory.CreateDbContextAsync();
        var outcome = Assert.Single(await replica.BackupDestinationHealthOutcomes.ToArrayAsync());
        Assert.False(outcome.Succeeded);
        Assert.Equal(BackupDestinationErrorCode.AuthenticationFailed.ToString(), outcome.ErrorCode);
        Assert.Equal("inconclusive", outcome.ProbeOutcome);
        var gate = new BackupDestinationHealth();
        Assert.False((await gate.TryEnterAsync(
            replica, destinationId, BackupDestinationOperation.Verify, CancellationToken.None)).Allowed);
        Assert.True((await gate.TryEnterAsync(
            replica, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        Assert.Equal(0, adapter.PutCalls);
    }

    [Fact]
    public async Task SlowPreferredPutLeavesDeadlineForVerifiedFallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"delivery-slow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "slow.phbackup");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase($"delivery-slow-{Guid.NewGuid():N}").Options;
            var factory = new TestDbFactory(options);
            var leases = new List<BackupDeliveryLease>();
            await using (var seed = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                pool.OverallDeadlineSeconds = 4;
                var run = Run(pool.Id, hash, Path.GetFileName(path));
                var preferred = Destination("a-slow", "domain-a", 10);
                var fallback = Destination("b-fast", "domain-b", 20);
                seed.AddRange(pool, run, preferred, fallback,
                    Route(pool.Id, preferred.Id, 10), Route(pool.Id, fallback.Id, 20));
                foreach (var destination in new[] { preferred, fallback })
                {
                    var copy = Copy(run.Id, destination.Id, hash);
                    var job = Job(copy.Id, $"slow-{destination.Name}");
                    job.State = "processing";
                    job.Attempt = 1;
                    job.LeaseId = Guid.NewGuid();
                    job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                    seed.AddRange(copy, job);
                    leases.Add(new BackupDeliveryLease(job.Id, job.LeaseId.Value));
                }
                await seed.SaveChangesAsync();
            }
            var adapter = new SlowPreferredAdapter();
            var processor = Processor(factory, Registry(adapter, new SuccessfulAdapter("telegram")), directory);

            await processor.ProcessAsync(leases[0], CancellationToken.None);
            await processor.ProcessAsync(leases[1], CancellationToken.None);

            await using var verify = await factory.CreateDbContextAsync();
            var copies = await verify.BackupCopies.Include(copy => copy.BackupDestination)
                .OrderBy(copy => copy.BackupDestination.Name).ToArrayAsync();
            Assert.Equal("unknown", copies[0].State);
            Assert.Equal("verified", copies[1].State);
            Assert.Equal(1, adapter.SlowCalls);
            Assert.Equal(1, adapter.FastCalls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ClaimPrefersPoolRoutePriorityWithinRunOverJobCreationOrder()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_delivery_priority_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var factory = await CreateFactoryAsync(builder.ConnectionString);
            var registry = Registry(new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram"));
            var processor = Processor(factory, registry, Path.GetTempPath());
            Guid preferredJobId;
            Guid fallbackJobId;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                var run = Run(pool.Id, new string('a', 64), "priority.phbackup");
                var preferred = Destination("preferred", "domain-a", 10);
                var fallback = Destination("fallback", "domain-b", 20);
                var preferredCopy = Copy(run.Id, preferred.Id, run.ContentSha256!);
                var fallbackCopy = Copy(run.Id, fallback.Id, run.ContentSha256!);
                var preferredJob = Job(preferredCopy.Id, "preferred-priority");
                var fallbackJob = Job(fallbackCopy.Id, "fallback-priority");
                preferredJob.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                fallbackJob.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
                preferredJobId = preferredJob.Id;
                fallbackJobId = fallbackJob.Id;
                seed.AddRange(pool, run, preferred, fallback,
                    Route(pool.Id, preferred.Id, 10), Route(pool.Id, fallback.Id, 20),
                    preferredCopy, fallbackCopy, preferredJob, fallbackJob);
                await seed.SaveChangesAsync();
            }

            var first = await processor.TryClaimAsync(CancellationToken.None);
            var second = await processor.TryClaimAsync(CancellationToken.None);
            Assert.Equal(preferredJobId, first?.JobId);
            Assert.Equal(fallbackJobId, second?.JobId);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PrimaryFailbackRequiresPutAndUnbrokenMatchingProbeWindow()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_failback_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var factory = await CreateFactoryAsync(builder.ConnectionString);
            var processor = Processor(factory,
                Registry(new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram")),
                Path.GetTempPath());
            var now = DateTimeOffset.UtcNow;
            Guid primaryJobId;
            Guid fallbackJobId;
            Guid primaryId;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                pool.FailbackHealthyForSeconds = 900;
                var run = Run(pool.Id, new string('a', 64), "failback.phbackup");
                var primary = Destination("primary", "domain-a", 10);
                var fallback = Destination("fallback", "domain-b", 20);
                primaryId = primary.Id;
                var primaryCopy = Copy(run.Id, primary.Id, run.ContentSha256!);
                var fallbackCopy = Copy(run.Id, fallback.Id, run.ContentSha256!);
                var primaryJob = Job(primaryCopy.Id, "failback-primary");
                var fallbackJob = Job(fallbackCopy.Id, "failback-secondary");
                primaryJobId = primaryJob.Id;
                fallbackJobId = fallbackJob.Id;
                var fallbackRoute = Route(pool.Id, fallback.Id, 20);
                fallbackRoute.Role = "fallback";
                seed.AddRange(pool, run, primary, fallback,
                    Route(pool.Id, primary.Id, 10), fallbackRoute,
                    primaryCopy, fallbackCopy, primaryJob, fallbackJob,
                    new BackupDestinationHealthOutcome
                    {
                        BackupDestinationId = primaryId,
                        Operation = "put",
                        Succeeded = false,
                        ErrorCode = BackupDestinationErrorCode.Unavailable.ToString(),
                        ObservedAt = now.AddMinutes(-30)
                    });
                await seed.SaveChangesAsync();
            }

            async Task ResetLeasesAsync()
            {
                await using var reset = await factory.CreateDbContextAsync();
                var jobs = await reset.BackupDeliveryJobs.ToArrayAsync();
                foreach (var job in jobs)
                {
                    job.State = "pending";
                    job.LeaseId = null;
                    job.LeaseUntil = null;
                }
                await reset.SaveChangesAsync();
            }

            Assert.Equal(fallbackJobId,
                (await processor.TryClaimAsync(CancellationToken.None))?.JobId);
            await ResetLeasesAsync();
            await using (var evidence = await factory.CreateDbContextAsync())
            {
                evidence.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = primaryId,
                    Operation = "put",
                    Succeeded = true,
                    ObservedAt = now.AddMinutes(-20)
                });
                await evidence.SaveChangesAsync();
            }
            Assert.Equal(fallbackJobId,
                (await processor.TryClaimAsync(CancellationToken.None))?.JobId);
            await ResetLeasesAsync();
            await using (var evidence = await factory.CreateDbContextAsync())
            {
                foreach (var minutesAgo in new[] { 20, 1 })
                    evidence.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
                    {
                        BackupDestinationId = primaryId,
                        Operation = "verify",
                        ProbeOutcome = "matching",
                        Succeeded = true,
                        ObservedAt = now.AddMinutes(-minutesAgo)
                    });
                await evidence.SaveChangesAsync();
            }
            Assert.Equal(fallbackJobId,
                (await processor.TryClaimAsync(CancellationToken.None))?.JobId);
            await ResetLeasesAsync();
            await using (var evidence = await factory.CreateDbContextAsync())
            {
                foreach (var minutesAgo in new[] { 15, 10, 5 })
                    evidence.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
                    {
                        BackupDestinationId = primaryId,
                        Operation = "verify",
                        ProbeOutcome = "matching",
                        Succeeded = true,
                        ObservedAt = now.AddMinutes(-minutesAgo)
                    });
                await evidence.SaveChangesAsync();
            }
            Assert.Equal(primaryJobId,
                (await processor.TryClaimAsync(CancellationToken.None))?.JobId);
            await ResetLeasesAsync();
            await using (var evidence = await factory.CreateDbContextAsync())
            {
                evidence.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = primaryId,
                    Operation = "verify",
                    ProbeOutcome = "mismatching",
                    Succeeded = true,
                    ObservedAt = DateTimeOffset.UtcNow.AddSeconds(-10)
                });
                await evidence.SaveChangesAsync();
            }
            Assert.Equal(fallbackJobId,
                (await processor.TryClaimAsync(CancellationToken.None))?.JobId);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task TwoWorkersLeaseOnceAndExpiredLeaseBecomesUnknown()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_delivery_lease_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var factory = await CreateFactoryAsync(builder.ConnectionString);
            var registry = Registry(new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram"));
            var processor = Processor(factory, registry, Path.GetTempPath());
            await SeedSingleJobAsync(factory);

            var claims = await Task.WhenAll(
                processor.TryClaimAsync(CancellationToken.None),
                processor.TryClaimAsync(CancellationToken.None));

            var lease = Assert.Single(claims, item => item is not null)!;
            _ = Assert.Single(claims, item => item is null);
            await using (var expire = await factory.CreateDbContextAsync())
            {
                await expire.BackupDeliveryJobs.Where(job => job.Id == lease.JobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
            }

            Assert.Equal(1, await processor.ReconcileExpiredLeasesAsync(CancellationToken.None));
            await using var verify = await factory.CreateDbContextAsync();
            var job = await verify.BackupDeliveryJobs.Include(item => item.BackupCopy).SingleAsync();
            Assert.Equal("reconciling", job.State);
            Assert.Null(job.LeaseId);
            Assert.Equal("unknown", job.BackupCopy.State);
            Assert.NotNull(job.BackupCopy.UnknownSince);

            await verify.BackupDeliveryJobs.Where(item => item.Id == lease.JobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.NotBefore, DateTimeOffset.UtcNow.AddSeconds(-1)));
            var probing = new ProbingAdapter(BackupDestinationProbeOutcome.Matching);
            var reconcileProcessor = Processor(
                factory, Registry(probing, new SuccessfulAdapter("telegram")), Path.GetTempPath());
            var reconciliationClaims = await Task.WhenAll(
                reconcileProcessor.TryClaimReconciliationAsync(CancellationToken.None),
                reconcileProcessor.TryClaimReconciliationAsync(CancellationToken.None));
            var reconciliation = Assert.Single(reconciliationClaims, item => item is not null)!;
            _ = Assert.Single(reconciliationClaims, item => item is null);
            await reconcileProcessor.ProcessReconciliationAsync(reconciliation, CancellationToken.None);

            await using var confirmed = await factory.CreateDbContextAsync();
            var resolved = await confirmed.BackupDeliveryJobs
                .Include(item => item.BackupCopy).SingleAsync();
            Assert.Equal("completed", resolved.State);
            Assert.Equal("verified", resolved.BackupCopy.State);
            Assert.Null(resolved.BackupCopy.UnknownSince);
            Assert.Equal(1, probing.ProbeCalls);
            Assert.Equal(0, probing.PutCalls);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CatchUpPlansOneMissingCopyOnceAndFencesDrainingPolicyAndUnreadableSource()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_catchup_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-catchup-{Guid.NewGuid():N}");
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        Directory.CreateDirectory(directory);
        try
        {
            var factory = await CreateFactoryAsync(builder.ConnectionString);
            byte[] bytes = [1, 2, 3, 4, 5];
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var adapter = new RepairAdapter(
                bytes, "catchup-source", "catchup-target", "catchup.phbackup");
            var registry = Registry(adapter, new SuccessfulAdapter("telegram"));
            var planner = new BackupCatchUpPlanner(factory, registry, new BackupDeliveryPlanner(registry));
            Guid poolId;
            Guid sourceRouteId;
            Guid targetRouteId;
            Guid runId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                var run = Run(pool.Id, hash, "catchup.phbackup");
                run.StartedAt = DateTimeOffset.UtcNow.AddDays(-2);
                var source = Destination("catchup-source", "domain-a", 10);
                var target = Destination("catchup-target", "domain-b", 20);
                var verified = Copy(run.Id, source.Id, run.ContentSha256!);
                verified.State = "verified";
                verified.VerifiedAt = DateTimeOffset.UtcNow;
                verified.NativeLocator = "source/catchup.phbackup";
                db.AddRange(pool, run, source, target,
                    Route(pool.Id, source.Id, 10), Route(pool.Id, target.Id, 20), verified);
                await db.SaveChangesAsync();
                poolId = pool.Id;
                sourceRouteId = source.Id;
                targetRouteId = target.Id;
                runId = run.Id;
            }

            var concurrent = await Task.WhenAll(
                planner.TryPlanAsync(false, CancellationToken.None),
                planner.TryPlanAsync(false, CancellationToken.None));
            Assert.Equal(1, concurrent.Sum());
            await using (var verify = await factory.CreateDbContextAsync())
            {
                Assert.Equal(2, await verify.BackupCopies.CountAsync(item => item.BackupRunId == runId));
                Assert.Single(await verify.BackupDeliveryJobs.ToArrayAsync());
            }
            Assert.Equal(0, await planner.TryPlanAsync(false, CancellationToken.None));
            var health = new BackupDestinationHealth();
            var processor = new BackupDeliveryProcessor(
                factory, registry,
                Options.Create(new BackupOptions { Directory = directory, EncryptionKey = new string('k', 32) }),
                Options.Create(new BackupRoutingOptions { Enabled = true }),
                destinationHealth: health,
                copyMaterializer: new BackupCopyMaterializer(factory, registry, health));
            var lease = await processor.TryClaimAsync(CancellationToken.None);
            Assert.NotNull(lease);
            await processor.ProcessAsync(lease, CancellationToken.None);
            Assert.Equal(1, adapter.ReadCalls);
            Assert.Equal(1, adapter.PutCalls);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
            await using (var verify = await factory.CreateDbContextAsync())
                Assert.Equal("verified", (await verify.BackupCopies.SingleAsync(item =>
                    item.BackupRunId == runId && item.BackupDestinationId == targetRouteId)).State);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var targetRoute = await db.BackupPoolDestinations.SingleAsync(item =>
                    item.BackupPoolId == poolId && item.BackupDestinationId == targetRouteId);
                targetRoute.Draining = true;
                targetRoute.Enabled = false;
                var second = Run(poolId, new string('b', 64), "draining.phbackup");
                var sourceCopy = Copy(second.Id, sourceRouteId, second.ContentSha256!);
                sourceCopy.State = "verified";
                sourceCopy.VerifiedAt = DateTimeOffset.UtcNow;
                sourceCopy.NativeLocator = "source/draining.phbackup";
                db.AddRange(second, sourceCopy);
                await db.SaveChangesAsync();
            }
            Assert.Equal(0, await planner.TryPlanAsync(false, CancellationToken.None));

            await using (var db = await factory.CreateDbContextAsync())
            {
                var targetRoute = await db.BackupPoolDestinations.SingleAsync(item =>
                    item.BackupPoolId == poolId && item.BackupDestinationId == targetRouteId);
                targetRoute.Draining = false;
                targetRoute.Enabled = true;
                var pool = await db.BackupPools.SingleAsync(item => item.Id == poolId);
                pool.PolicyVersion++;
                await db.SaveChangesAsync();
            }
            Assert.Equal(0, await planner.TryPlanAsync(false, CancellationToken.None));

            await using (var db = await factory.CreateDbContextAsync())
            {
                var pool = await db.BackupPools.SingleAsync(item => item.Id == poolId);
                pool.PolicyVersion--;
                var sourceRoute = await db.BackupPoolDestinations.SingleAsync(item =>
                    item.BackupPoolId == poolId && item.BackupDestinationId == sourceRouteId);
                sourceRoute.AllowedOperations = "put,verify";
                await db.SaveChangesAsync();
            }
            Assert.Equal(0, await planner.TryPlanAsync(false, CancellationToken.None));

            await using (var db = await factory.CreateDbContextAsync())
            {
                var sourceRoute = await db.BackupPoolDestinations.SingleAsync(item =>
                    item.BackupPoolId == poolId && item.BackupDestinationId == sourceRouteId);
                sourceRoute.AllowedOperations = "put,verify,read";
                await db.SaveChangesAsync();
            }
            Assert.Equal(1, await planner.TryPlanAsync(false, CancellationToken.None));

            await using (var db = await factory.CreateDbContextAsync())
            {
                var third = Run(poolId, new string('c', 64), "unknown.phbackup");
                var sourceCopy = Copy(third.Id, sourceRouteId, third.ContentSha256!);
                sourceCopy.State = "verified";
                sourceCopy.VerifiedAt = DateTimeOffset.UtcNow;
                sourceCopy.NativeLocator = "source/unknown.phbackup";
                var unknownTarget = Copy(third.Id, targetRouteId, third.ContentSha256!);
                unknownTarget.State = "unknown";
                unknownTarget.UnknownSince = DateTimeOffset.UtcNow;
                var unknownJob = Job(unknownTarget.Id, "unknown-catchup-job");
                unknownJob.State = "reconciling";
                db.AddRange(third, sourceCopy, unknownTarget, unknownJob);
                await db.SaveChangesAsync();
            }
            Assert.Equal(0, await planner.TryPlanAsync(false, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [Trait("Category", "PostgresIntegration")]
    [InlineData("disabled-route")]
    [InlineData("disabled-destination")]
    [InlineData("no-verify-route")]
    [InlineData("same-domain")]
    public async Task CatchUpPrioritizesUnderprotectedRunWithIneligibleVerifiedCopy(string scenario)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_catchup_priority_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var factory = await CreateFactoryAsync(builder.ConnectionString);
            var registry = Registry(new SuccessfulAdapter("s3"), new SuccessfulAdapter("telegram"));
            var planner = new BackupCatchUpPlanner(factory, registry, new BackupDeliveryPlanner(registry));
            Guid underprotectedRunId;
            Guid newerRunId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                pool.RequiredVerifiedCopies = 2;
                pool.DesiredVerifiedCopies = 3;
                var older = Run(pool.Id, new string('a', 64), "older.phbackup");
                older.StartedAt = DateTimeOffset.UtcNow.AddDays(-2);
                older.RequiredVerifiedCopies = 2;
                older.DesiredVerifiedCopies = 3;
                var newer = Run(pool.Id, new string('b', 64), "newer.phbackup");
                newer.StartedAt = DateTimeOffset.UtcNow.AddDays(-1);
                newer.RequiredVerifiedCopies = 2;
                newer.DesiredVerifiedCopies = 3;
                var source = Destination("source", "domain-a", 10);
                var disabled = Destination("disabled", "domain-b", 20);
                if (scenario == "disabled-destination") disabled.Enabled = false;
                if (scenario == "same-domain") disabled.FailureDomain = " DOMAIN-A ";
                var second = Destination("second", "domain-c", 30);
                var target = Destination("target", "domain-d", 40);
                var disabledRoute = Route(pool.Id, disabled.Id, 20);
                if (scenario == "disabled-route") disabledRoute.Enabled = false;
                if (scenario == "no-verify-route") disabledRoute.AllowedOperations = "read";
                db.AddRange(pool, older, newer, source, disabled, second, target,
                    Route(pool.Id, source.Id, 10), disabledRoute,
                    Route(pool.Id, second.Id, 30), Route(pool.Id, target.Id, 40));
                foreach (var (run, destination, locator) in new[]
                {
                    (older, source, "source/older.phbackup"),
                    (older, disabled, "disabled/older.phbackup"),
                    (newer, source, "source/newer.phbackup"),
                    (newer, second, "second/newer.phbackup")
                })
                {
                    var copy = Copy(run.Id, destination.Id, run.ContentSha256!);
                    copy.State = "verified";
                    copy.VerifiedAt = DateTimeOffset.UtcNow;
                    copy.NativeLocator = locator;
                    db.BackupCopies.Add(copy);
                }
                await db.SaveChangesAsync();
                underprotectedRunId = older.Id;
                newerRunId = newer.Id;
            }

            Assert.Equal(1, await planner.TryPlanAsync(false, CancellationToken.None));
            await using var verify = await factory.CreateDbContextAsync();
            var created = await verify.BackupCopies.SingleAsync(copy => copy.State == "planned");
            Assert.Equal(underprotectedRunId, created.BackupRunId);
            Assert.NotEqual(newerRunId, created.BackupRunId);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PrePutFailureRearmsOnceAcrossReplicasAndStopsAtDurableCap()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_rearm_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var factory = await CreateFactoryAsync(builder.ConnectionString);
            var hash = new string('a', 64);
            var adapter = new RepairAdapter([1, 2, 3, 4, 5], "rearm-source", "rearm-target",
                "rearm.phbackup");
            var registry = Registry(adapter, new SuccessfulAdapter("telegram"));
            var firstReplica = new BackupCatchUpPlanner(factory, registry,
                new BackupDeliveryPlanner(registry));
            var secondReplica = new BackupCatchUpPlanner(factory, registry,
                new BackupDeliveryPlanner(registry));
            Guid targetCopyId;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                var pool = Pool();
                var run = Run(pool.Id, hash, "rearm.phbackup");
                var source = Destination("rearm-source", "domain-a", 10);
                var target = Destination("rearm-target", "domain-b", 20);
                var verified = Copy(run.Id, source.Id, hash);
                verified.State = "verified";
                verified.VerifiedAt = DateTimeOffset.UtcNow;
                verified.NativeLocator = "source/rearm.phbackup";
                var failed = Copy(run.Id, target.Id, hash);
                failed.State = "permanent_failed";
                failed.LastErrorCode = BackupDestinationErrorCode.InvalidConfiguration.ToString();
                var oldJob = Job(failed.Id, "initial-preput-failure");
                oldJob.State = "failed";
                oldJob.LastErrorCode = failed.LastErrorCode;
                oldJob.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-21);
                oldJob.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
                seed.AddRange(pool, run, source, target,
                    Route(pool.Id, source.Id, 10), Route(pool.Id, target.Id, 20),
                    verified, failed, oldJob);
                await seed.SaveChangesAsync();
                targetCopyId = failed.Id;
            }

            var concurrent = await Task.WhenAll(
                firstReplica.TryRearmPrePutFailureAsync(CancellationToken.None),
                secondReplica.TryRearmPrePutFailureAsync(CancellationToken.None));
            Assert.Equal(1, concurrent.Sum());
            await using (var verify = await factory.CreateDbContextAsync())
            {
                var copy = await verify.BackupCopies.Include(item => item.Jobs)
                    .SingleAsync(item => item.Id == targetCopyId);
                Assert.Equal("planned", copy.State);
                Assert.Null(copy.LastAttemptAt);
                Assert.Equal(2, copy.Jobs.Count);
                Assert.Single(copy.Jobs, item => item.State == "pending");
            }
            Assert.Equal(0, adapter.PutCalls);
            Assert.Equal(0, adapter.ReadCalls);

            await using (var update = await factory.CreateDbContextAsync())
            {
                var copy = await update.BackupCopies.Include(item => item.Jobs)
                    .SingleAsync(item => item.Id == targetCopyId);
                var latest = copy.Jobs.Single(item => item.State == "pending");
                latest.State = "failed";
                latest.LastErrorCode = BackupDestinationErrorCode.Timeout.ToString();
                latest.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-21);
                latest.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
                copy.State = "permanent_failed";
                copy.LastErrorCode = latest.LastErrorCode;
                await update.SaveChangesAsync();
            }
            Assert.Equal(1, await firstReplica.TryRearmPrePutFailureAsync(CancellationToken.None));
            await using (var update = await factory.CreateDbContextAsync())
            {
                var copy = await update.BackupCopies.Include(item => item.Jobs)
                    .SingleAsync(item => item.Id == targetCopyId);
                Assert.Equal(3, copy.Jobs.Count);
                var latest = copy.Jobs.Single(item => item.State == "pending");
                latest.State = "failed";
                latest.LastErrorCode = BackupDestinationErrorCode.Timeout.ToString();
                latest.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-21);
                latest.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
                copy.State = "permanent_failed";
                copy.LastErrorCode = latest.LastErrorCode;
                await update.SaveChangesAsync();
            }
            Assert.Equal(0, await secondReplica.TryRearmPrePutFailureAsync(CancellationToken.None));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task NewReplicasObserveDurableDestinationAuthenticationFailure()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_delivery_health_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var factory = await CreateFactoryAsync(builder.ConnectionString);
            await SeedSingleJobAsync(factory);
            Guid destinationId;
            await using (var failed = await factory.CreateDbContextAsync())
            {
                var copy = await failed.BackupCopies.Include(item => item.Jobs).SingleAsync();
                destinationId = copy.BackupDestinationId;
                copy.LastAttemptAt = DateTimeOffset.UtcNow;
                copy.Jobs.Single().State = "failed";
                copy.Jobs.Single().LastErrorCode = BackupDestinationErrorCode.AuthenticationFailed.ToString();
                await failed.SaveChangesAsync();
            }
            var first = new BackupDestinationHealth();
            var second = new BackupDestinationHealth();
            await using var verify = await factory.CreateDbContextAsync();
            Assert.False((await first.TryEnterAsync(
                verify, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
            Assert.False((await second.TryEnterAsync(
                verify, destinationId, BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FailedPreferredDestinationDoesNotPreventVerifiedFallback()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_delivery_fallback_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-delivery-{Guid.NewGuid():N}");
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "fallback.phbackup");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var factory = await CreateFactoryAsync(builder.ConnectionString);
            var registry = Registry(new DestinationAwareS3Adapter(), new SuccessfulAdapter("telegram"));
            var runId = await SeedFallbackGraphAsync(factory, hash);
            var planner = new BackupDeliveryPlanner(registry);
            await using (var planDb = await factory.CreateDbContextAsync())
                Assert.Equal(2, await planner.PlanAsync(planDb, runId, hash, 5, CancellationToken.None));
            await using (var planDb = await factory.CreateDbContextAsync())
                Assert.Equal(0, await planner.PlanAsync(planDb, runId, hash, 5, CancellationToken.None));
            var processor = Processor(factory, registry, directory);

            for (var index = 0; index < 2; index++)
            {
                var lease = await processor.TryClaimAsync(CancellationToken.None);
                Assert.NotNull(lease);
                await processor.ProcessAsync(lease, CancellationToken.None);
            }

            await using var verify = await factory.CreateDbContextAsync();
            var copies = await verify.BackupCopies.Include(copy => copy.BackupDestination)
                .OrderBy(copy => copy.BackupDestination.Name).ToArrayAsync();
            Assert.Equal("permanent_failed", copies[0].State);
            Assert.Equal("verified", copies[1].State);
            Assert.Equal("fallback/fallback.phbackup", copies[1].NativeLocator);
            var outcomes = await verify.BackupDestinationHealthOutcomes
                .OrderBy(item => item.ObservedAt).ToArrayAsync();
            Assert.Equal(2, outcomes.Length);
            Assert.All(outcomes, item => Assert.Equal("put", item.Operation));
            Assert.Contains(outcomes, item => !item.Succeeded &&
                item.ErrorCode == BackupDestinationErrorCode.Unavailable.ToString());
            Assert.Contains(outcomes, item => item.Succeeded && item.ErrorCode is null);
            var run = await verify.BackupRuns.SingleAsync(item => item.Id == runId);
            Assert.True(run.SentToObjectStorage);
            Assert.Equal("fallback/fallback.phbackup", run.ObjectStorageKey);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task AllFailedDestinationsNeverSatisfyProtection()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_delivery_all_down_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-all-down-{Guid.NewGuid():N}");
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "fallback.phbackup");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var factory = await CreateFactoryAsync(builder.ConnectionString);
            var registry = Registry(
                new FailingAdapter("s3", BackupDestinationFailureDisposition.Permanent),
                new SuccessfulAdapter("telegram"));
            var runId = await SeedFallbackGraphAsync(factory, hash);
            await using (var plan = await factory.CreateDbContextAsync())
                Assert.Equal(2, await new BackupDeliveryPlanner(registry).PlanAsync(
                    plan, runId, hash, 5, CancellationToken.None));
            var processor = Processor(factory, registry, directory);
            for (var index = 0; index < 2; index++)
            {
                var lease = await processor.TryClaimAsync(CancellationToken.None);
                Assert.NotNull(lease);
                await processor.ProcessAsync(lease, CancellationToken.None);
            }

            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(2, await verify.BackupCopies.CountAsync(copy => copy.State == "permanent_failed"));
            Assert.False((await verify.BackupRuns.SingleAsync(item => item.Id == runId)).SentToObjectStorage);
            var protection = await new BackupProtectionEvaluator(factory, registry)
                .EvaluateAsync(runId, hasDurableLocalStaging: true, CancellationToken.None);
            Assert.Equal(BackupProtectionState.Unavailable, protection.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<TestDbFactory> CreateFactoryAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var factory = new TestDbFactory(options);
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        return factory;
    }

    private static BackupDeliveryProcessor Processor(
        IDbContextFactory<ProxyHarborDbContext> factory,
        BackupDestinationRegistry registry,
        string directory) => new(
            factory,
            registry,
            Options.Create(new BackupOptions { Directory = directory, EncryptionKey = new string('k', 32) }),
            Options.Create(new BackupRoutingOptions { Enabled = true }));

    private static BackupDestinationRegistry Registry(
        IBackupDestinationAdapter s3,
        IBackupDestinationAdapter telegram) => new([s3, telegram]);

    private static async Task SeedSingleJobAsync(TestDbFactory factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var pool = Pool();
        var run = Run(pool.Id, new string('a', 64), "lease.phbackup");
        var destination = Destination("lease-s3", "domain-a", 10);
        db.AddRange(pool, run, destination);
        db.BackupPoolDestinations.Add(Route(pool.Id, destination.Id, 10));
        var copy = Copy(run.Id, destination.Id, run.ContentSha256!);
        db.BackupCopies.Add(copy);
        db.BackupDeliveryJobs.Add(Job(copy.Id, "lease-job"));
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedFallbackGraphAsync(TestDbFactory factory, string hash)
    {
        await using var db = await factory.CreateDbContextAsync();
        var pool = Pool();
        var run = Run(pool.Id, hash, "fallback.phbackup");
        var primary = Destination("a-primary-fails", "domain-a", 10);
        var fallback = Destination("b-fallback", "domain-b", 20);
        db.AddRange(pool, run, primary, fallback);
        db.BackupPoolDestinations.AddRange(
            Route(pool.Id, primary.Id, 10),
            Route(pool.Id, fallback.Id, 20));
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static BackupPool Pool() => new()
    {
        Name = $"pool-{Guid.NewGuid():N}",
        RequiredVerifiedCopies = 1,
        DesiredVerifiedCopies = 2,
        MaxAttemptsPerCycle = 1,
        OverallDeadlineSeconds = 30,
        PolicyVersion = 3
    };

    private static BackupRun Run(Guid poolId, string hash, string fileName) => new()
    {
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        FinishedAt = DateTimeOffset.UtcNow,
        Status = "completed",
        FileName = fileName,
        SizeBytes = 5,
        ContentSha256 = hash,
        BackupPoolId = poolId,
        ProtectionPolicyVersion = 3,
        RequiredVerifiedCopies = 1,
        DesiredVerifiedCopies = 2,
        ObjectStorageConfigured = true
    };

    private static BackupDestination Destination(string name, string failureDomain, int priority) => new()
    {
        Name = name,
        Kind = "s3",
        Enabled = true,
        FailureDomain = failureDomain,
        Priority = priority
    };

    private static BackupPoolDestination Route(Guid poolId, Guid destinationId, int priority) => new()
    {
        BackupPoolId = poolId,
        BackupDestinationId = destinationId,
        Priority = priority,
        AllowedOperations = "put,verify,read",
        Enabled = true
    };

    private static BackupCopy Copy(Guid runId, Guid destinationId, string hash) => new()
    {
        BackupRunId = runId,
        BackupDestinationId = destinationId,
        ContentSha256 = hash,
        SizeBytes = 5,
        PolicyVersion = 3
    };

    private static BackupDeliveryJob Job(Guid copyId, string key) => new()
    {
        BackupCopyId = copyId,
        IdempotencyKey = key,
        State = "pending",
        NotBefore = DateTimeOffset.UtcNow
    };

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class SuccessfulAdapter(string kind) : IBackupDestinationAdapter
    {
        public string Kind { get; } = kind;
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), false, false, false);

        public Task<BackupDestinationWriteResult> PutAsync(
            BackupDestination destination,
            string path,
            string expectedSha256,
            long expectedSize,
            CancellationToken token) => Task.FromResult(new BackupDestinationWriteResult(
                $"{destination.Name}/{Path.GetFileName(path)}", null, expectedSha256, true));
    }

    private sealed class RepairAdapter(
        byte[] bytes,
        string sourceName = "source",
        string targetName = "target",
        string fileName = "expired-staging.phbackup") : IBackupDestinationAdapter
    {
        public string Kind => "s3";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), false, false, false);
        public int ReadCalls { get; private set; }
        public int PutCalls { get; private set; }

        public async Task<BackupDestinationMaterializationResult> MaterializeAsync(
            BackupDestination destination, string fileName, string nativeLocator,
            string path, string expectedSha256, long expectedSize, CancellationToken token)
        {
            ReadCalls++;
            Assert.Equal(sourceName, destination.Name);
            await File.WriteAllBytesAsync(path, bytes, token);
            return new BackupDestinationMaterializationResult(
                path, bytes.Length, expectedSha256, null, null);
        }

        public async Task<BackupDestinationWriteResult> PutAsync(
            BackupDestination destination, string path, string expectedSha256,
            long expectedSize, CancellationToken token)
        {
            PutCalls++;
            Assert.Equal(targetName, destination.Name);
            Assert.Equal(fileName, Path.GetFileName(path));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, token));
            return new BackupDestinationWriteResult(
                $"target/{Path.GetFileName(path)}", null, expectedSha256, true);
        }
    }

    private sealed class DestinationAwareS3Adapter : IBackupDestinationAdapter
    {
        public string Kind => "s3";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), false, false, false);

        public Task<BackupDestinationWriteResult> PutAsync(
            BackupDestination destination,
            string path,
            string expectedSha256,
            long expectedSize,
            CancellationToken token)
        {
            if (destination.Name.Contains("fails", StringComparison.Ordinal))
                throw new BackupDestinationOperationException(
                    new BackupDestinationFailure(
                        BackupDestinationErrorCode.Unavailable,
                        BackupDestinationFailureDisposition.Retryable),
                    "synthetic unavailable");
            return Task.FromResult(new BackupDestinationWriteResult(
                $"fallback/{Path.GetFileName(path)}", null, expectedSha256, true));
        }
    }

    private sealed class UnverifiedAdapter(string kind) : IBackupDestinationAdapter
    {
        public string Kind { get; } = kind;
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(false), new(false), false, false, false);

        public Task<BackupDestinationWriteResult> PutAsync(
            BackupDestination destination,
            string path,
            string expectedSha256,
            long expectedSize,
            CancellationToken token) => Task.FromResult(
                new BackupDestinationWriteResult(null, null, null, IndependentlyVerified: false));
    }

    private sealed class FailingAdapter(
        string kind,
        BackupDestinationFailureDisposition disposition) : IBackupDestinationAdapter
    {
        public string Kind { get; } = kind;
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), false, false, false);

        public Task<BackupDestinationWriteResult> PutAsync(
            BackupDestination destination,
            string path,
            string expectedSha256,
            long expectedSize,
            CancellationToken token) => Task.FromException<BackupDestinationWriteResult>(
                new BackupDestinationOperationException(
                    new BackupDestinationFailure(BackupDestinationErrorCode.Unavailable, disposition),
                    "synthetic failure"));
    }

    private sealed class ProbingAdapter(
        BackupDestinationProbeOutcome outcome,
        BackupDestinationErrorCode? failureCode = null,
        bool includeLocator = true,
        bool returnTypedFailure = false) : IBackupDestinationAdapter
    {
        public string Kind => "s3";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), false, false, false);
        public int PutCalls { get; private set; }
        public int ProbeCalls { get; private set; }

        public Task<BackupDestinationWriteResult> PutAsync(
            BackupDestination destination, string path, string expectedSha256,
            long expectedSize, CancellationToken token)
        {
            PutCalls++;
            throw new InvalidOperationException("Reconciliation must not call PUT.");
        }

        public Task<BackupDestinationProbeResult> ProbeWriteOutcomeAsync(
            BackupDestination destination, string fileName, string expectedSha256,
            long expectedSize, CancellationToken token)
        {
            ProbeCalls++;
            if (failureCode is { } code && !returnTypedFailure)
                throw new BackupDestinationOperationException(
                    new BackupDestinationFailure(code, BackupDestinationFailureDisposition.Permanent),
                    "synthetic probe failure");
            return Task.FromResult(new BackupDestinationProbeResult(
                outcome,
                outcome == BackupDestinationProbeOutcome.Matching && includeLocator
                    ? $"safe/{fileName}" : null,
                null,
                outcome == BackupDestinationProbeOutcome.Matching ? expectedSha256 : null,
                returnTypedFailure ? failureCode : null));
        }
    }

    private sealed class AmbiguousThenMatchingAdapter : IBackupDestinationAdapter
    {
        public string Kind => "s3";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), false, false, false);
        public int PutCalls { get; private set; }
        public int ProbeCalls { get; private set; }

        public Task<BackupDestinationWriteResult> PutAsync(
            BackupDestination destination, string path, string expectedSha256,
            long expectedSize, CancellationToken token)
        {
            PutCalls++;
            throw new BackupDestinationOperationException(
                new BackupDestinationFailure(
                    BackupDestinationErrorCode.UnknownOutcome,
                    BackupDestinationFailureDisposition.UnknownOutcome),
                "synthetic response lost after PUT");
        }

        public Task<BackupDestinationProbeResult> ProbeWriteOutcomeAsync(
            BackupDestination destination, string fileName, string expectedSha256,
            long expectedSize, CancellationToken token)
        {
            ProbeCalls++;
            return Task.FromResult(new BackupDestinationProbeResult(
                BackupDestinationProbeOutcome.Matching, $"safe/{fileName}", null, expectedSha256));
        }
    }

    private sealed class SlowPreferredAdapter : IBackupDestinationAdapter
    {
        public string Kind => "s3";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), false, false, false);
        public int SlowCalls { get; private set; }
        public int FastCalls { get; private set; }

        public async Task<BackupDestinationWriteResult> PutAsync(
            BackupDestination destination, string path, string expectedSha256,
            long expectedSize, CancellationToken token)
        {
            if (destination.Name == "a-slow")
            {
                SlowCalls++;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            FastCalls++;
            return new BackupDestinationWriteResult(
                $"fallback/{Path.GetFileName(path)}", null, expectedSha256, true);
        }
    }
}
