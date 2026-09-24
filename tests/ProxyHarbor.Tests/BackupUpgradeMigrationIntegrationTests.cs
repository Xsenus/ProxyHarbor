using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupUpgradeMigrationIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task UpgradeFromDeployedV7SchemaPreservesLegacyBackupAndDurableRows()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        // This is the last migration in the production checkout observed on 2026-09-24.
        const string deployedMigration = "20260921032945_AddPaidProxySourceCredentials";
        var schema = $"proxyharbor_backup_upgrade_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString).Options;
            await using (var deployed = new ProxyHarborDbContext(options))
            {
                await deployed.Database.MigrateAsync(deployedMigration);
                // Seed through SQL because the current entity contains columns absent from v7.
                await deployed.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "BackupRuns"
                      ("Id", "StartedAt", "FinishedAt", "Status", "FileName", "SizeBytes",
                       "TelegramConfigured", "SentToTelegram", "ObjectStorageConfigured",
                       "SentToObjectStorage")
                    VALUES
                      ('00000000-0000-4000-8000-000000000001', now() - interval '1 hour', now(),
                       'completed', 'proxyharbor-synthetic.phbackup', 17, true, true, false, false)
                    """);
                await deployed.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "MetricsSnapshotStates" ("Key", "PayloadJson", "CapturedAt", "UpdatedAt")
                    VALUES ('proxy', '{{}}'::jsonb, now(), now())
                    """);
            }

            await using (var upgrade = new ProxyHarborDbContext(options))
                await upgrade.Database.MigrateAsync();

            await using var verify = new ProxyHarborDbContext(options);
            Assert.Empty(await verify.Database.GetPendingMigrationsAsync());
            var run = await verify.BackupRuns.SingleAsync();
            Assert.Equal("completed", run.Status);
            Assert.Equal("proxyharbor-synthetic.phbackup", run.FileName);
            Assert.Equal(17, run.SizeBytes);
            Assert.True(run.TelegramConfigured);
            Assert.True(run.SentToTelegram);
            Assert.False(run.ObjectStorageConfigured);
            Assert.False(run.SentToObjectStorage);
            Assert.Null(run.BackupPoolId);
            Assert.Null(run.ContentSha256);
            var snapshot = await verify.MetricsSnapshotStates.SingleAsync();
            Assert.Equal("proxy", snapshot.Key);
            Assert.Equal("{}", snapshot.PayloadJson);
            Assert.Empty(await verify.BackupDestinations.ToArrayAsync());
            Assert.Empty(await verify.BackupCopies.ToArrayAsync());
            Assert.Empty(await verify.BackupDeliveryJobs.ToArrayAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
