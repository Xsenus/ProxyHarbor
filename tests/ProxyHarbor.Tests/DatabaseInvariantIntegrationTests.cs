using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using ProxyHarbor.Infrastructure.Persistence.Migrations;

namespace ProxyHarbor.Tests;

/// <summary>Доказывает наличие и реальное применение database-level trust boundary.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class DatabaseInvariantIntegrationTests
{
    private static readonly string[] ExpectedConstraints =
    [
        "CK_BackupRuns_Result",
        "CK_BackupRuns_State",
        "CK_Proxies_CheckCounters",
        "CK_Proxies_DeferredAttempt",
        "CK_Proxies_Identity",
        "CK_Proxies_Latency",
        "CK_Proxies_Lease",
        "CK_Proxies_StatusEvidence",
        "CK_Proxies_Timeline",
        "CK_Runs_Counters",
        "CK_Runs_State",
        "CK_Sources_ContentTimeline",
        "CK_Sources_Counters",
        "CK_Sources_FetchTimeline",
        "CK_Sources_ProtocolPriority",
        "CK_ValidationRuns_Counters",
        "CK_ValidationRuns_State"
    ];

    [Fact]
    public void EfModelContainsEveryRequiredConstraint()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql("Host=localhost;Database=proxyharbor_model_contract")
            .Options;
        using var db = new ProxyHarborDbContext(options);

        var actual = db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .SelectMany(entity => entity.GetCheckConstraints())
            .Select(constraint => constraint.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedConstraints, actual);
    }

    [Fact]
    public void MigrationsUseRestartableNonBlockingValidationForEveryConstraint()
    {
        var operations = new EnforceDataInvariants().UpOperations.OfType<SqlOperation>()
            .Concat(new AddSourceContentRefresh().UpOperations.OfType<SqlOperation>())
            .Concat(new EnforcePublishedProxyEvidence().UpOperations.OfType<SqlOperation>())
            .ToArray();

        Assert.Equal(ExpectedConstraints.Length * 2 + 1, operations.Length);
        Assert.All(operations, operation => Assert.True(operation.SuppressTransaction));
        Assert.Equal(ExpectedConstraints.Length, operations.Count(operation => operation.Sql.Contains("NOT VALID")));
        Assert.Equal(
            ExpectedConstraints.Length,
            operations.Count(operation => operation.Sql.Contains("VALIDATE CONSTRAINT")));
        Assert.All(ExpectedConstraints, name => Assert.Equal(2, operations.Count(operation => operation.Sql.Contains(name))));
        Assert.Single(operations, operation => operation.Sql.Contains("SET \"Status\" = 0"));
        Assert.Contains("IF NOT EXISTS", Assert.Single(operations, operation =>
            operation.Sql.Contains("CK_Proxies_StatusEvidence") && operation.Sql.Contains("NOT VALID")).Sql);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task StatusEvidenceMigrationRepairsLegacyRowsBeforeValidation()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_evidence_{Guid.NewGuid():N}";
        var connectionString = WithSearchPath(baseConnectionString, schema);
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await ExecuteSchemaCommandAsync(admin, $"CREATE SCHEMA \"{schema}\"");
        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using var db = new ProxyHarborDbContext(options);
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260810022141_AddSourceContentRefresh");

            var nextCheckAt = DateTimeOffset.UtcNow.AddHours(1);
            var aliveId = Guid.NewGuid();
            var deadId = Guid.NewGuid();
            db.Proxies.AddRange(
                new ProxyEndpoint
                {
                    Id = aliveId,
                    Host = "8.8.8.8",
                    Port = 8080,
                    Status = ProxyStatus.Alive,
                    NextCheckAt = nextCheckAt
                },
                new ProxyEndpoint
                {
                    Id = deadId,
                    Host = "1.1.1.1",
                    Port = 8080,
                    Status = ProxyStatus.Dead,
                    NextCheckAt = nextCheckAt
                });
            await db.SaveChangesAsync();

            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();

            var repaired = await db.Proxies
                .Where(proxy => proxy.Id == aliveId || proxy.Id == deadId)
                .OrderBy(proxy => proxy.Host)
                .ToArrayAsync();
            Assert.Equal(2, repaired.Length);
            Assert.All(repaired, proxy =>
            {
                Assert.Equal(ProxyStatus.Pending, proxy.Status);
                Assert.Null(proxy.NextCheckAt);
            });

            await AssertRejectedAsync(options, invalidDb => invalidDb.Proxies.Add(new ProxyEndpoint
            {
                Host = "9.9.9.9",
                Port = 8080,
                Status = ProxyStatus.Alive
            }));
        }
        finally
        {
            await ExecuteSchemaCommandAsync(admin, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PostgreSqlValidatesConstraintsAndRejectsInvalidRows()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_invariants_{Guid.NewGuid():N}";
        var connectionString = WithSearchPath(baseConnectionString, schema);
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await ExecuteSchemaCommandAsync(admin, $"CREATE SCHEMA \"{schema}\"");
        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var migrationDb = new ProxyHarborDbContext(options))
                await migrationDb.Database.MigrateAsync();

            await using (var verify = new ProxyHarborDbContext(options))
            {
                var validatedCount = await verify.Database.SqlQueryRaw<int>("""
                    SELECT count(*)::integer AS "Value"
                    FROM pg_constraint constraint_row
                    JOIN pg_class table_row ON table_row.oid = constraint_row.conrelid
                    WHERE constraint_row.contype = 'c'
                      AND constraint_row.convalidated
                      AND constraint_row.conname LIKE 'CK\_%' ESCAPE '\'
                      AND table_row.relnamespace = current_schema()::regnamespace
                    """).SingleAsync();
                Assert.Equal(ExpectedConstraints.Length, validatedCount);
            }

            await AssertRejectedAsync(options, db => db.Proxies.Add(new ProxyEndpoint
            {
                Host = "8.8.8.8",
                Port = 0
            }));
            await AssertRejectedAsync(options, db => db.Sources.Add(new ProxySource
            {
                Name = "Invalid source",
                Url = "https://example.com/proxies.txt",
                Priority = 10_001
            }));
            await AssertRejectedAsync(options, db => db.Sources.Add(new ProxySource
            {
                Name = "Invalid source timeline",
                Url = "https://example.net/proxies.txt",
                LastFetchedAt = DateTimeOffset.UtcNow.AddHours(-2),
                LastSucceededAt = DateTimeOffset.UtcNow.AddHours(-2),
                LastContentFetchedAt = DateTimeOffset.UtcNow
            }));
            await AssertRejectedAsync(options, db => db.Runs.Add(new CollectionRun
            {
                Status = "completed"
            }));
            await AssertRejectedAsync(options, db => db.ValidationRuns.Add(new ValidationRun
            {
                LeaseId = Guid.NewGuid(),
                Claimed = 0,
                Checked = 1
            }));
            await AssertRejectedAsync(options, db => db.BackupRuns.Add(new BackupRun
            {
                FinishedAt = DateTimeOffset.UtcNow,
                Status = "completed",
                TelegramConfigured = true,
                SentToTelegram = false
            }));
        }
        finally
        {
            await ExecuteSchemaCommandAsync(admin, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    private static async Task AssertRejectedAsync(
        DbContextOptions<ProxyHarborDbContext> options,
        Action<ProxyHarborDbContext> addInvalidEntity)
    {
        await using var db = new ProxyHarborDbContext(options);
        addInvalidEntity(db);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
    }

    private static string WithSearchPath(string connectionString, string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
        return builder.ConnectionString;
    }

    private static async Task ExecuteSchemaCommandAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
