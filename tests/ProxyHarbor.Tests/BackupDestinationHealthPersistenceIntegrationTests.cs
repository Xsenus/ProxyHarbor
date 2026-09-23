using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupDestinationHealthPersistenceIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ProbeOutcomeMigrationPreservesLegacyRowsWithoutClaimingMatchingEvidence()
    {
        var baseConnection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;

        var schema = $"proxyharbor_probe_migration_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var scopedConnection = new NpgsqlConnectionStringBuilder(baseConnection)
            {
                SearchPath = schema
            };
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(scopedConnection.ConnectionString).Options;
            await using var db = new ProxyHarborDbContext(options);
            await db.Database.MigrateAsync("20260923103233_AddBackupDestinationHealthOutcomes");
            var destination = new BackupDestination
            {
                Name = "legacy-probe",
                Kind = "s3",
                Enabled = true,
                FailureDomain = "migration-test"
            };
            db.BackupDestinations.Add(destination);
            await db.SaveChangesAsync();
            var oldId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BackupDestinationHealthOutcomes"
                    ("Id", "BackupDestinationId", "Operation", "Succeeded", "ObservedAt")
                VALUES ({oldId}, {destination.Id}, 'verify', TRUE, {DateTimeOffset.UtcNow})
                """);

            await db.Database.MigrateAsync();
            var legacy = await db.BackupDestinationHealthOutcomes.AsNoTracking()
                .SingleAsync(item => item.Id == oldId);
            Assert.True(legacy.Succeeded);
            Assert.Null(legacy.ProbeOutcome);

            db.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
            {
                BackupDestinationId = destination.Id,
                ProbeOutcome = "matching",
                Succeeded = true
            });
            db.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
            {
                BackupDestinationId = destination.Id,
                Operation = "put",
                Succeeded = true
            });
            await db.SaveChangesAsync();
            Assert.Equal(3, await db.BackupDestinationHealthOutcomes.CountAsync());
            var invalidId = Guid.NewGuid();
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BackupDestinationHealthOutcomes"
                    ("Id", "BackupDestinationId", "Operation", "Succeeded", "ProbeOutcome", "ObservedAt")
                VALUES ({invalidId}, {destination.Id}, 'verify', TRUE, 'unsupported', {DateTimeOffset.UtcNow})
                """));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task VerifyFailureSurvivesReplicaAndOldObservationsArePruned()
    {
        var baseConnection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;

        var schema = $"proxyharbor_health_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var scopedConnection = new NpgsqlConnectionStringBuilder(baseConnection)
            {
                SearchPath = schema
            };
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(scopedConnection.ConnectionString).Options;
            var destination = new BackupDestination
            {
                Name = "health-probe",
                Kind = "s3",
                Enabled = true,
                FailureDomain = "health-test"
            };
            await using (var seed = new ProxyHarborDbContext(options))
            {
                await seed.Database.MigrateAsync();
                seed.BackupDestinations.Add(destination);
                seed.BackupDestinationHealthOutcomes.AddRange(
                    new BackupDestinationHealthOutcome
                    {
                        BackupDestinationId = destination.Id,
                        Succeeded = false,
                        ErrorCode = BackupDestinationErrorCode.Unavailable.ToString(),
                        ObservedAt = DateTimeOffset.UtcNow.AddDays(-9)
                    },
                    new BackupDestinationHealthOutcome
                    {
                        BackupDestinationId = destination.Id,
                        Operation = "put",
                        Succeeded = true,
                        ObservedAt = DateTimeOffset.UtcNow.AddDays(-2)
                    },
                    new BackupDestinationHealthOutcome
                    {
                        BackupDestinationId = destination.Id,
                        Succeeded = false,
                        ErrorCode = BackupDestinationErrorCode.AuthenticationFailed.ToString(),
                        ObservedAt = DateTimeOffset.UtcNow.AddSeconds(-10)
                    });
                await seed.SaveChangesAsync();
            }

            await using var replica = new ProxyHarborDbContext(options);
            var gate = new BackupDestinationHealth();
            Assert.False((await gate.TryEnterAsync(replica, destination.Id,
                BackupDestinationOperation.Verify, CancellationToken.None)).Allowed);
            Assert.True((await gate.TryEnterAsync(replica, destination.Id,
                BackupDestinationOperation.Put, CancellationToken.None)).Allowed);
            Assert.Equal(1, await BackupDestinationHealth.PruneOldOutcomesAsync(
                replica, CancellationToken.None));
            Assert.Equal(2, await replica.BackupDestinationHealthOutcomes.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
