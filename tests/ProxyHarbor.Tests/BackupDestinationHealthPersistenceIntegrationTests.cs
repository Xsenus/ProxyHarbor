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
            Assert.Single(await replica.BackupDestinationHealthOutcomes.ToArrayAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
