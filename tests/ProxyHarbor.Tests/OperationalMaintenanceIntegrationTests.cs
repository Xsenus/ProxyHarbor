using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Доказывает bounded cleanup и cluster ownership на настоящей PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class OperationalMaintenanceIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RunHistoryCleanupIsBoundedPerTable()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        await WithSchemaAsync(baseConnectionString, async factory =>
        {
            var now = DateTimeOffset.UtcNow;
            var old = now.AddDays(-40);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Runs.AddRange(Enumerable.Range(0, 3)
                    .Select(index => CollectionRun(old.AddMinutes(index), "completed")));
                seed.ValidationRuns.AddRange(Enumerable.Range(0, 3)
                    .Select(index => ValidationRun(old.AddMinutes(index), "completed")));
                await seed.SaveChangesAsync();
            }

            await using (var cleanup = await factory.CreateDbContextAsync())
            {
                var deleted = await OperationalRetention.PruneRunHistoryAsync(
                    cleanup, now, collectionRetentionDays: 30, validationRetentionHours: 24,
                    CancellationToken.None, maximumRowsPerTable: 2);
                Assert.Equal(2, deleted.CollectionRuns);
                Assert.Equal(2, deleted.ValidationRuns);
            }

            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(1, await verify.Runs.CountAsync());
            Assert.Equal(1, await verify.ValidationRuns.CountAsync());
        });
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PrunesEveryFinishedHistoryAndOnlyUnownedStaleProxyMembership()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        await WithSchemaAsync(baseConnectionString, async factory =>
        {
            var now = DateTimeOffset.UtcNow;
            var old = now.AddDays(-400);
            var validationExpired = now.AddDays(-2);
            var fresh = now.AddHours(-1);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                var expiredLeaseProxy = Proxy("4.2.2.11", ProxyStatus.Dead, old, failedChecks: 1);
                var activeLeaseProxy = Proxy("4.2.2.13", ProxyStatus.Pending, old);
                seed.Proxies.AddRange(
                    Proxy("4.2.2.10", ProxyStatus.Pending, old),
                    expiredLeaseProxy,
                    Proxy("4.2.2.12", ProxyStatus.Alive, old, successfulChecks: 1),
                    // Исторический endpoint однажды работал, но сейчас Dead: membership
                    // обязана сохраниться независимо от LastSeen retention.
                    Proxy("4.2.2.16", ProxyStatus.Dead, old, successfulChecks: 1, failedChecks: 1),
                    activeLeaseProxy,
                    Proxy("4.2.2.14", ProxyStatus.Pending, fresh));
                seed.ProxyValidationLeases.AddRange(
                    new ProxyValidationLease
                    {
                        ProxyId = expiredLeaseProxy.Id,
                        LeaseId = Guid.NewGuid(),
                        LeaseUntil = now.AddMinutes(-1)
                    },
                    new ProxyValidationLease
                    {
                        ProxyId = activeLeaseProxy.Id,
                        LeaseId = Guid.NewGuid(),
                        LeaseUntil = now.AddMinutes(5)
                    });
                seed.Runs.AddRange(
                    CollectionRun(old, "completed"),
                    CollectionRun(old, "failed"),
                    CollectionRun(old, "running"),
                    CollectionRun(validationExpired, "completed"),
                    CollectionRun(fresh, "completed"));
                seed.ValidationRuns.AddRange(
                    ValidationRun(old, "completed"),
                    ValidationRun(old, "failed"),
                    ValidationRun(old, "running"),
                    ValidationRun(validationExpired, "completed"),
                    ValidationRun(fresh, "completed"));
                seed.BackupRuns.AddRange(
                    BackupRun(old, "completed"),
                    BackupRun(old, "failed"),
                    BackupRun(old, "running"),
                    BackupRun(fresh, "completed"));
                await seed.SaveChangesAsync();
            }

            var maintenance = CreateService(factory);
            var result = await maintenance.RunOnceAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(1, result.Proxies);
            Assert.Equal(3, result.CollectionRuns);
            Assert.Equal(4, result.ValidationRuns);
            Assert.Equal(3, result.BackupRuns);
            Assert.Equal(1, result.RecoveredCollectionRuns);
            Assert.Equal(1, result.RecoveredValidationRuns);
            Assert.Equal(1, result.RecoveredBackupRuns);
            Assert.Equal(11, result.TotalDeleted);
            Assert.Equal(3, result.TotalRecovered);
            Assert.Equal(11, maintenance.LastDeletedRows);
            Assert.Equal(3, maintenance.LastRecoveredRows);
            Assert.True(maintenance.LastSuccessUnixSeconds > 0);
            Assert.Equal(0, maintenance.LastFailureUnixSeconds);
            Assert.Equal(1, maintenance.Status);

            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(5, await verify.Proxies.CountAsync());
            Assert.True(await verify.Proxies.AnyAsync(proxy => proxy.Host == "4.2.2.11"));
            Assert.True(await verify.Proxies.AnyAsync(proxy => proxy.Host == "4.2.2.12"));
            Assert.True(await verify.Proxies.AnyAsync(proxy => proxy.Host == "4.2.2.13"));
            Assert.True(await verify.Proxies.AnyAsync(proxy => proxy.Host == "4.2.2.14"));
            Assert.True(await verify.Proxies.AnyAsync(proxy => proxy.Host == "4.2.2.16"));
            Assert.Equal(2, await verify.Runs.CountAsync());
            Assert.Equal(1, await verify.ValidationRuns.CountAsync());
            Assert.Equal(1, await verify.BackupRuns.CountAsync());
            Assert.False(await verify.Runs.AnyAsync(run => run.Status == "running"));
            Assert.False(await verify.ValidationRuns.AnyAsync(run => run.Status == "running"));
            Assert.False(await verify.BackupRuns.AnyAsync(run => run.Status == "running"));

            var metricsController = new MetricsController(
                factory,
                Options.Create(new CollectorOptions()),
                Options.Create(new BackupOptions()),
                new ProbeControlHealth(),
                maintenance);
            var metricsResult = Assert.IsType<ContentResult>(
                await metricsController.Get(CancellationToken.None));
            Assert.Contains($"proxyharbor_maintenance_last_success_timestamp_seconds {maintenance.LastSuccessUnixSeconds}",
                metricsResult.Content, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_maintenance_last_deleted_rows 11",
                metricsResult.Content, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_maintenance_last_recovered_rows 3",
                metricsResult.Content, StringComparison.Ordinal);

            verify.Proxies.Add(Proxy("4.2.2.15", ProxyStatus.Pending, old));
            await verify.SaveChangesAsync();
            await verify.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_operational_maintenance() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'operational maintenance failure canary';
                END;
                $$;
                CREATE TRIGGER fail_operational_maintenance
                BEFORE DELETE ON "Proxies"
                FOR EACH STATEMENT EXECUTE FUNCTION fail_operational_maintenance();
                """);

            var failure = await Assert.ThrowsAsync<PostgresException>(
                () => maintenance.RunOnceAsync(CancellationToken.None));
            Assert.Contains("operational maintenance failure canary", failure.Message, StringComparison.Ordinal);
            Assert.True(maintenance.LastFailureUnixSeconds > 0);
            Assert.True(maintenance.LastSuccessUnixSeconds > 0);
            Assert.Equal(0, maintenance.Status);

            var failedMetrics = Assert.IsType<ContentResult>(
                await metricsController.Get(CancellationToken.None));
            Assert.Contains($"proxyharbor_maintenance_last_failure_timestamp_seconds {maintenance.LastFailureUnixSeconds}",
                failedMetrics.Content, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_maintenance_healthy 0",
                failedMetrics.Content, StringComparison.Ordinal);
        });
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ActiveOperationLocksAndValidationLeaseProtectRunningOwnership()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        await WithSchemaAsync(baseConnectionString, async factory =>
        {
            var old = DateTimeOffset.UtcNow.AddDays(-400);
            var leaseId = Guid.NewGuid();
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Runs.Add(CollectionRun(old, "running"));
                seed.ValidationRuns.Add(new ValidationRun
                {
                    LeaseId = leaseId,
                    StartedAt = old,
                    Status = "running"
                });
                seed.BackupRuns.Add(BackupRun(old, "running"));
                var leasedProxy = new ProxyEndpoint
                {
                    Host = "4.2.2.20",
                    Port = 8080,
                    Status = ProxyStatus.Pending,
                    FirstSeenAt = old.AddDays(-1),
                    LastSeenAt = old
                };
                seed.Proxies.Add(leasedProxy);
                seed.ProxyValidationLeases.Add(new ProxyValidationLease
                {
                    ProxyId = leasedProxy.Id,
                    LeaseId = leaseId,
                    LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5)
                });
                await seed.SaveChangesAsync();
            }
            await using var collectionOwner = await PostgresAdvisoryLock.TryAcquireAsync(
                factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None);
            await using var backupOwner = await PostgresAdvisoryLock.TryAcquireAsync(
                factory, PostgresAdvisoryLock.BackupKey, CancellationToken.None);
            Assert.NotNull(collectionOwner);
            Assert.NotNull(backupOwner);

            var result = await CreateService(factory).RunOnceAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(0, result.TotalRecovered);
            Assert.Equal(0, result.TotalDeleted);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(1, await verify.Runs.CountAsync(run => run.Status == "running"));
            Assert.Equal(1, await verify.ValidationRuns.CountAsync(run => run.Status == "running"));
            Assert.Equal(1, await verify.BackupRuns.CountAsync(run => run.Status == "running"));
            Assert.Equal(1, await verify.Proxies.CountAsync());
        });
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PeerOwnedMaintenanceSkipsWithoutDeletingRows()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        await WithSchemaAsync(baseConnectionString, async factory =>
        {
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Runs.Add(CollectionRun(DateTimeOffset.UtcNow.AddDays(-400), "completed"));
                await seed.SaveChangesAsync();
            }
            await using var owner = await PostgresAdvisoryLock.TryAcquireAsync(
                factory, PostgresAdvisoryLock.MaintenanceKey, CancellationToken.None);
            Assert.NotNull(owner);
            var maintenance = CreateService(factory);

            var result = await maintenance.RunOnceAsync(CancellationToken.None);

            Assert.Null(result);
            Assert.Equal(0, maintenance.LastSuccessUnixSeconds);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(1, await verify.Runs.CountAsync());
        });
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RetentionAndClaimSerializeBeforeProxyDeleteOrLeaseInsert()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var schema = $"proxyharbor_retention_claim_{suffix}";
        var cleanupApplication = $"proxyharbor-retention-{suffix}";
        var claimApplication = $"proxyharbor-claim-{suffix}";
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            static DbContextOptions<ProxyHarborDbContext> Options(
                string connectionString,
                string searchPath,
                string applicationName)
            {
                var builder = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    SearchPath = searchPath,
                    ApplicationName = applicationName
                };
                return new DbContextOptionsBuilder<ProxyHarborDbContext>()
                    .UseNpgsql(builder.ConnectionString, postgres => postgres.EnableRetryOnFailure())
                    .Options;
            }

            var setupOptions = Options(baseConnectionString, schema, $"proxyharbor-setup-{suffix}");
            await using (var migration = new ProxyHarborDbContext(setupOptions))
                await migration.Database.MigrateAsync();
            var now = DateTimeOffset.UtcNow;
            var proxy = Proxy("4.2.2.30", ProxyStatus.Pending, now.AddDays(-30));
            await using (var seed = new ProxyHarborDbContext(setupOptions))
            {
                seed.Proxies.Add(proxy);
                await seed.SaveChangesAsync();
            }

            var blockerBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schema,
                ApplicationName = $"proxyharbor-retention-blocker-{suffix}"
            };
            await using var blocker = new NpgsqlConnection(blockerBuilder.ConnectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var lockProxy = new NpgsqlCommand(
                "SELECT \"Id\" FROM \"Proxies\" WHERE \"Id\" = @id FOR UPDATE",
                blocker,
                blockerTransaction))
            {
                lockProxy.Parameters.AddWithValue("id", proxy.Id);
                Assert.Equal(proxy.Id, await lockProxy.ExecuteScalarAsync());
            }

            await using var cleanupDb = new ProxyHarborDbContext(
                Options(baseConnectionString, schema, cleanupApplication));
            var cleanupTask = OperationalRetention.PruneProxyMembershipAsync(
                cleanupDb, now, retentionDays: 3, CancellationToken.None);
            Assert.True(await WaitForLockAsync(admin, cleanupApplication, "%DELETE FROM \"Proxies\"%"));

            var claimBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schema,
                ApplicationName = claimApplication
            };
            await using var claimDb = new ProxyHarborDbContext(
                new DbContextOptionsBuilder<ProxyHarborDbContext>()
                    .UseNpgsql(claimBuilder.ConnectionString)
                    .Options);
            await using var claimTransaction = await claimDb.Database.BeginTransactionAsync();
            var claimTask = ValidationQueueClaim.ClaimAndLeaseAsync(
                claimDb,
                batchSize: 1,
                now,
                now.AddMinutes(5),
                Guid.NewGuid(),
                CancellationToken.None);
            Assert.True(await WaitForLockAsync(admin, claimApplication, "%pg_advisory_xact_lock%"));

            await blockerTransaction.CommitAsync();
            Assert.Equal(1, await cleanupTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(await claimTask.WaitAsync(TimeSpan.FromSeconds(10)));
            await claimTransaction.CommitAsync();

            await using var verify = new ProxyHarborDbContext(setupOptions);
            Assert.False(await verify.Proxies.AnyAsync(item => item.Id == proxy.Id));
            Assert.False(await verify.ProxyValidationLeases.AnyAsync(item => item.ProxyId == proxy.Id));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> WaitForLockAsync(
        NpgsqlConnection admin,
        string applicationName,
        string queryPattern)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var activity = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @application_name
                      AND wait_event_type = 'Lock'
                      AND query LIKE @query_pattern)
                """, admin);
            activity.Parameters.AddWithValue("application_name", applicationName);
            activity.Parameters.AddWithValue("query_pattern", queryPattern);
            if ((bool)(await activity.ExecuteScalarAsync())!) return true;
            await Task.Delay(50);
        }
        return false;
    }

    private static OperationalMaintenanceService CreateService(TestDbFactory factory) => new(
        factory,
        Options.Create(new CollectorOptions
        {
            DeadRetentionDays = 3,
            RunRetentionDays = 30,
            ValidationRunRetentionHours = 24
        }),
        Options.Create(new BackupOptions { HistoryRetentionDays = 365 }));

    private static ProxyEndpoint Proxy(
        string host,
        ProxyStatus status,
        DateTimeOffset seenAt,
        int successfulChecks = 0,
        int failedChecks = 0) => new()
        {
            Host = host,
            Port = 8080,
            Status = status,
            FirstSeenAt = seenAt.AddDays(-1),
            LastSeenAt = seenAt,
            LastCheckedAt = successfulChecks + failedChecks > 0 ? seenAt : null,
            FirstAliveAt = successfulChecks > 0 ? seenAt.AddHours(-1) : null,
            LastAliveAt = successfulChecks > 0 ? seenAt : null,
            CurrentAliveSince = status == ProxyStatus.Alive ? seenAt : null,
            LatencyMs = successfulChecks > 0 ? 250 : null,
            SuccessfulChecks = successfulChecks,
            FailedChecks = failedChecks,
            ConsecutiveFailedChecks = failedChecks
        };

    private static CollectionRun CollectionRun(DateTimeOffset startedAt, string status) => new()
    {
        StartedAt = startedAt,
        FinishedAt = status == "running" ? null : startedAt.AddMinutes(1),
        Status = status
    };

    private static ValidationRun ValidationRun(DateTimeOffset startedAt, string status) => new()
    {
        LeaseId = Guid.NewGuid(),
        StartedAt = startedAt,
        FinishedAt = status == "running" ? null : startedAt.AddMinutes(1),
        Status = status
    };

    private static BackupRun BackupRun(DateTimeOffset startedAt, string status) => new()
    {
        StartedAt = startedAt,
        FinishedAt = status == "running" ? null : startedAt.AddMinutes(1),
        Status = status
    };

    private static async Task WithSchemaAsync(
        string baseConnectionString,
        Func<TestDbFactory, Task> test)
    {
        var schema = $"proxyharbor_maintenance_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(options);
            await using (var migration = await factory.CreateDbContextAsync())
                await migration.Database.MigrateAsync();
            await test(factory);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
