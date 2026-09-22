using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет fail-closed ограничения нового destination-based persistence слоя.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupDestinationSchemaIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FreshSchemaCreatesNoJobsAndRejectsInvalidDurableState()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_backup_destinations_{Guid.NewGuid():N}";
        var connectionBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connectionBuilder.ConnectionString)
                .Options;
            await using var db = new ProxyHarborDbContext(options);
            await DatabaseSeeder.MigrateSchemaAsync(db, CancellationToken.None);

            Assert.Empty(await db.BackupDestinations.ToArrayAsync());
            Assert.Empty(await db.BackupDeliveryJobs.ToArrayAsync());

            var now = DateTimeOffset.UtcNow;
            var run = new BackupRun
            {
                StartedAt = now.AddMinutes(-2),
                FinishedAt = now.AddMinutes(-1),
                Status = "completed",
                FileName = "schema-test.phbackup",
                SizeBytes = 123
            };
            var destination = new BackupDestination
            {
                Name = "schema-test-s3",
                Kind = "s3",
                Enabled = true,
                FailureDomain = "independent-a",
                CapabilitiesJson = "{\"operations\":[\"put\",\"verify\"]}",
                SettingsJson = "{}"
            };
            db.AddRange(run, destination);
            await db.SaveChangesAsync();

            db.BackupRuns.Add(new BackupRun
            {
                StartedAt = now,
                Status = "running",
                ContentSha256 = "not-a-sha256"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.BackupRuns.Add(new BackupRun
            {
                StartedAt = now,
                Status = "running",
                BackupPoolId = Guid.NewGuid(),
                ProtectionPolicyVersion = 2,
                RequiredVerifiedCopies = 2
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var protectedRun = new BackupRun
            {
                StartedAt = now,
                Status = "running",
                ContentSha256 = new string('c', 64),
                BackupPoolId = Guid.NewGuid(),
                ProtectionPolicyVersion = 2,
                RequiredVerifiedCopies = 2,
                DesiredVerifiedCopies = 3
            };
            db.BackupRuns.Add(protectedRun);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var persistedRun = await db.BackupRuns.SingleAsync(item => item.Id == protectedRun.Id);
            Assert.Equal(2, persistedRun.RequiredVerifiedCopies);
            Assert.Equal(3, persistedRun.DesiredVerifiedCopies);

            db.BackupCopies.Add(new BackupCopy
            {
                BackupRunId = run.Id,
                BackupDestinationId = destination.Id,
                ContentSha256 = new string('a', 64),
                SizeBytes = 123,
                State = "verified",
                VerifiedAt = now
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.BackupPools.Add(new BackupPool
            {
                Name = "invalid-policy",
                RequiredVerifiedCopies = 2,
                DesiredVerifiedCopies = 1
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var copy = new BackupCopy
            {
                BackupRunId = run.Id,
                BackupDestinationId = destination.Id,
                ContentSha256 = new string('b', 64),
                SizeBytes = 123,
                State = "planned"
            };
            db.BackupCopies.Add(copy);
            await db.SaveChangesAsync();

            db.BackupDeliveryJobs.Add(new BackupDeliveryJob
            {
                BackupCopyId = copy.Id,
                IdempotencyKey = "schema-test-job",
                State = "pending",
                NotBefore = now,
                LeaseId = Guid.NewGuid(),
                CreatedAt = now,
                UpdatedAt = now
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
