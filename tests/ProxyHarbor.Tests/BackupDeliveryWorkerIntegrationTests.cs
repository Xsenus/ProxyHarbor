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
            var processor = Processor(
                factory,
                Registry(
                    new FailingAdapter("s3", BackupDestinationFailureDisposition.UnknownOutcome),
                    new SuccessfulAdapter("telegram")),
                directory);

            await processor.ProcessAsync(new BackupDeliveryLease(jobId, leaseId), CancellationToken.None);

            await using var verify = await factory.CreateDbContextAsync();
            var persistedJob = await verify.BackupDeliveryJobs.Include(item => item.BackupCopy).SingleAsync();
            Assert.Equal("reconciling", persistedJob.State);
            Assert.Equal("unknown", persistedJob.BackupCopy.State);
            Assert.NotNull(persistedJob.BackupCopy.UnknownSince);
            Assert.Null(persistedJob.LeaseId);
        }
        finally { Directory.Delete(directory, recursive: true); }
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
}
