using System.Security.Cryptography;
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
        "CK_Proxies_AliveTimeline",
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
    private static readonly string[] IdentityConstraints =
    [
        "CK_AccessBlockRules_Kind",
        "CK_AspNetUsers_ActiveTimeline",
        "CK_AspNetUsers_PreferredLanguage",
        "CK_BackupConfigurations_Singleton",
        "CK_BackupRuns_Content",
        "CK_BackupRuns_ProtectionPolicy",
        "CK_BackupCopies_Content",
        "CK_BackupCopies_CatalogAttempt",
        "CK_BackupCopies_CatalogLease",
        "CK_BackupCopies_CatalogPublished",
        "CK_BackupCopies_CatalogState",
        "CK_BackupCopies_State",
        "CK_BackupCopies_Unknown",
        "CK_BackupCopies_Verified",
        "CK_BackupDeliveryJobs_Attempt",
        "CK_BackupDeliveryJobs_Lease",
        "CK_BackupDeliveryJobs_State",
        "CK_BackupDeliveryJobs_Timeline",
        "CK_BackupDestinationHealthOutcomes_Operation",
        "CK_BackupDestinationHealthOutcomes_ProbeOutcome",
        "CK_BackupDestinationHealthOutcomes_Result",
        "CK_BackupDestinations_Kind",
        "CK_BackupDestinations_Priority",
        "CK_BackupDestinations_Timeline",
        "CK_BackupPoolDestinations_Draining",
        "CK_BackupPoolDestinations_Operations",
        "CK_BackupPoolDestinations_Priority",
        "CK_BackupPoolDestinations_Role",
        "CK_BackupPools_Policy",
        "CK_BackupRestoreVerifications_Environment",
        "CK_BackupRestoreVerifications_Result",
        "CK_BackupRestoreVerifications_Timeline",
        "CK_FreeProxyExportGrants_Timeline",
        "CK_MetricsSnapshotStates_Key",
        "CK_PaymentConfigurations_Singleton",
        "CK_PaymentOrders_Amount",
        "CK_PaymentOrders_Currency",
        "CK_PaymentOrders_Plan",
        "CK_PaymentOrders_Status",
        "CK_PaymentOrders_Timeline",
        "CK_ProxyAccessBuckets_Counters",
        "CK_ProxySourceCredentials_Status",
        "CK_ProxySourceImportStates_Cursor",
        "CK_ProxySourceImportStates_Payload",
        "CK_VpnSourceImportStates_Cursor",
        "CK_VpnSourceImportStates_Payload",
        "CK_SourceApiCaptureStates_Owner",
        "CK_SourceApiCaptureStates_Payload",
        "CK_ReferralRelationships_DifferentUsers",
        "CK_ReferralRelationships_Slot",
        "CK_ReferralRewards_Days",
        "CK_ReferralRewards_Kind",
        "CK_SiteConfigurations_Singleton",
        "CK_Subscriptions_Plan",
        "CK_Subscriptions_Status",
        "CK_Subscriptions_Timeline",
        "CK_TelegramBotConfigurations_Singleton",
        "CK_TelegramConversationMessages_Direction",
        "CK_TelegramOutboundMessages_Attempts",
        "CK_TelegramOutboundMessages_Kind",
        "CK_TelegramOutboundMessages_Status",
        "CK_TelegramUpdateReceipts_Transport",
        "CK_UserApiTokens_Hash",
        "CK_UserApiTokens_Timeline",
        "CK_UserApiTokenRequests_Duration",
        "CK_UserApiTokenRequests_ItemCount",
        "CK_UserApiTokenRequests_Status",
        "CK_VpnConnectionProfiles_Identity",
        "CK_VpnConnectionProfiles_Settings",
        "CK_VpnConnectionProfiles_Timeline",
        "CK_VpnEndpoints_Counters",
        "CK_VpnEndpoints_DeferredAttempt",
        "CK_VpnEndpoints_Identity",
        "CK_VpnEndpoints_Timeline",
        "CK_VpnSources_Counters",
        "CK_VpnSources_ContentTimeline",
        "CK_VpnSources_FetchTimeline",
        "CK_VpnSources_ProtocolPriority"
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

        Assert.All(ExpectedConstraints, expected => Assert.Contains(expected, actual));
        Assert.All(IdentityConstraints, expected => Assert.Contains(expected, actual));
    }

    [Fact]
    public void MigrationsUseRestartableNonBlockingValidationForEveryConstraint()
    {
        var operations = new EnforceDataInvariants().UpOperations.OfType<SqlOperation>()
            .Concat(new AddSourceContentRefresh().UpOperations.OfType<SqlOperation>())
            .Concat(new EnforcePublishedProxyEvidence().UpOperations.OfType<SqlOperation>())
            .Concat(new TrackProxyAvailabilityHistory().UpOperations.OfType<SqlOperation>())
            .ToArray();

        Assert.Equal(ExpectedConstraints.Length * 2 + 2, operations.Length);
        Assert.All(operations, operation => Assert.True(operation.SuppressTransaction));
        Assert.Equal(ExpectedConstraints.Length, operations.Count(operation => operation.Sql.Contains("NOT VALID")));
        Assert.Equal(
            ExpectedConstraints.Length,
            operations.Count(operation => operation.Sql.Contains("VALIDATE CONSTRAINT")));
        Assert.All(ExpectedConstraints, name => Assert.Equal(2, operations.Count(operation => operation.Sql.Contains(name))));
        Assert.Single(operations, operation => operation.Sql.Contains("SET \"Status\" = 0"));
        Assert.Single(operations, operation => operation.Sql.Contains("SET \"FirstAliveAt\""));
        Assert.Contains("IF NOT EXISTS", Assert.Single(operations, operation =>
            operation.Sql.Contains("CK_Proxies_StatusEvidence") && operation.Sql.Contains("NOT VALID")).Sql);
    }

    [Fact]
    public void VpnConditionalFetchMigrationIsRestartableAndValidatesWithoutBlockingWrites()
    {
        var operations = new OptimizeVpnConditionalFetch().UpOperations.OfType<SqlOperation>().ToArray();

        Assert.Equal(3, operations.Length);
        Assert.All(operations, operation => Assert.True(operation.SuppressTransaction));
        Assert.Contains("ADD COLUMN IF NOT EXISTS", operations[0].Sql, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS", operations[1].Sql, StringComparison.Ordinal);
        Assert.Contains("NOT VALID", operations[1].Sql, StringComparison.Ordinal);
        Assert.Contains("VALIDATE CONSTRAINT", operations[2].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void VpnPublicFreshnessIndexMatchesThePublishedCountPredicate()
    {
        var operations = new OptimizeVpnPublicFreshness().UpOperations
            .OfType<SqlOperation>().ToArray();

        Assert.Equal(2, operations.Length);
        Assert.All(operations, operation => Assert.True(operation.SuppressTransaction));
        Assert.Contains("DROP INDEX CONCURRENTLY IF EXISTS", operations[0].Sql,
            StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX CONCURRENTLY", operations[1].Sql,
            StringComparison.Ordinal);
        Assert.Contains("\"LastCheckedAt\"", operations[1].Sql, StringComparison.Ordinal);
        Assert.Contains("INCLUDE (\"Protocol\", \"CountryCode\")", operations[1].Sql,
            StringComparison.Ordinal);
        Assert.Contains("\"Status\" = 1", operations[1].Sql, StringComparison.Ordinal);
        Assert.Contains("\"ConnectionUri\" IS NOT NULL", operations[1].Sql,
            StringComparison.Ordinal);
        Assert.Contains("\"CountryCode\" IS NOT NULL", operations[1].Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnusedVpnStatusScheduleIndexIsDroppedWithoutBlockingWrites()
    {
        var migration = new DropUnusedVpnStatusScheduleIndex();
        var up = migration.UpOperations.OfType<SqlOperation>().ToArray();
        var down = migration.DownOperations.OfType<SqlOperation>().ToArray();

        var drop = Assert.Single(up);
        Assert.True(drop.SuppressTransaction);
        Assert.Contains("DROP INDEX CONCURRENTLY IF EXISTS", drop.Sql, StringComparison.Ordinal);
        Assert.Contains("IX_VpnEndpoints_Status_NextCheckAt", drop.Sql, StringComparison.Ordinal);

        var restore = Assert.Single(down);
        Assert.True(restore.SuppressTransaction);
        Assert.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS", restore.Sql, StringComparison.Ordinal);
        Assert.Contains("\"Status\", \"NextCheckAt\"", restore.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProxyLastSeenIndexIsReplacedWithoutBlockingWrites()
    {
        var migration = new OptimizeProxyLastSeenWriteAmplification();
        var up = migration.UpOperations.OfType<SqlOperation>().ToArray();
        var down = migration.DownOperations.OfType<SqlOperation>().ToArray();

        Assert.Equal(2, up.Length);
        Assert.All(up, operation => Assert.True(operation.SuppressTransaction));
        Assert.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Proxies_Status\"",
            up[0].Sql, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Proxies_Status_LastSeenAt\"",
            up[1].Sql, StringComparison.Ordinal);

        Assert.Equal(2, down.Length);
        Assert.All(down, operation => Assert.True(operation.SuppressTransaction));
        Assert.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Proxies_Status_LastSeenAt\"",
            down[0].Sql, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Proxies_Status\"",
            down[1].Sql, StringComparison.Ordinal);
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
            var seenAt = DateTimeOffset.UtcNow;
            var aliveId = Guid.NewGuid();
            var deadId = Guid.NewGuid();
            // Здесь база намеренно остановлена на старой миграции. Новая EF-модель уже
            // знает о колонках истории доступности, поэтому legacy-строки добавляем SQL,
            // совместимым именно со старой схемой, которую и должен чинить следующий шаг.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Proxies"
                    ("Id", "Host", "Port", "Protocol", "Status", "IsAnonymous", "FirstSeenAt", "LastSeenAt",
                     "NextCheckAt", "SuccessfulChecks", "FailedChecks", "ConsecutiveFailedChecks",
                     "LastValidationDeferred")
                VALUES
                    ({aliveId}, '8.8.8.8', 8080, 0, 1, FALSE, {seenAt}, {seenAt},
                     {nextCheckAt}, 0, 0, 0, FALSE),
                    ({deadId}, '1.1.1.1', 8080, 0, 2, FALSE, {seenAt}, {seenAt},
                     {nextCheckAt}, 0, 0, 0, FALSE)
                """);

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
                Assert.Equal(ExpectedConstraints.Length + IdentityConstraints.Length, validatedCount);
            }

            await AssertRejectedAsync(options, db => db.Proxies.Add(new ProxyEndpoint
            {
                Host = "8.8.8.8",
                Port = 0
            }));
            await AssertRejectedAsync(options, db => db.VpnEndpoints.Add(new VpnEndpoint
            {
                Host = "8.8.8.8",
                Port = 443,
                LastValidationDeferred = true
            }));
            await AssertRejectedAsync(options, db => AddInvalidVpnProfile(db, profile => profile.Port = 0),
                "CK_VpnConnectionProfiles_Identity");
            await AssertRejectedAsync(options, db => AddInvalidVpnProfile(db, profile => profile.ConnectionUri = null),
                "CK_VpnConnectionProfiles_Settings");
            await AssertRejectedAsync(options, db => AddInvalidVpnProfile(db, profile => profile.LastSeenAt = profile.FirstSeenAt.AddSeconds(-1)),
                "CK_VpnConnectionProfiles_Timeline");
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
            var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:80", ProxyProtocol.Http);
            var hash = SHA256.HashData(snapshot.Payload);
            await AssertRejectedAsync(options, db => AddImportState(db, 0, 0, [], []), "CK_ProxySourceImportStates_Cursor");
            await AssertRejectedAsync(options, db => AddImportState(db, -1, 1, snapshot.Payload, hash), "CK_ProxySourceImportStates_Cursor");
            await AssertRejectedAsync(options, db => AddImportState(db, 0, 1, [], []), "CK_ProxySourceImportStates_Payload");
            await AssertRejectedAsync(options, db => AddImportState(db, 1, 1, snapshot.Payload, hash), "CK_ProxySourceImportStates_Payload");
            await AssertRejectedAsync(options, db => AddImportState(db, 0, 1, snapshot.Payload, hash[..^1]), "CK_ProxySourceImportStates_Payload");
            await AssertRejectedAsync(options, db => AddImportState(db, 0, 1,
                new byte[ProxyCandidateSnapshotCodec.MaxPayloadBytes + 1], hash), "CK_ProxySourceImportStates_Payload");
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
        Action<ProxyHarborDbContext> addInvalidEntity,
        string? expectedConstraint = null)
    {
        await using var db = new ProxyHarborDbContext(options);
        addInvalidEntity(db);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        if (expectedConstraint is not null) Assert.Equal(expectedConstraint, postgres.ConstraintName);
    }

    private static void AddInvalidVpnProfile(ProxyHarborDbContext db, Action<VpnConnectionProfile> invalidate)
    {
        var source = new VpnSource
        {
            Name = "Invalid profile",
            Provider = "Fixture",
            License = "MIT",
            Url = $"https://example.org/vpn/{Guid.NewGuid():N}"
        };
        db.VpnSources.Add(source);
        var profile = new VpnConnectionProfile
        {
            VpnSourceId = source.Id,
            ProfileHash = new string('a', 64),
            Host = "8.8.8.8",
            Port = 443,
            Protocol = VpnProtocol.Shadowsocks,
            Transport = "tcp",
            ConnectionUri = "ss://YWVzLTEyOC1nY206dGVzdA@8.8.8.8:443",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        invalidate(profile);
        db.VpnConnectionProfiles.Add(profile);
    }

    private static void AddImportState(ProxyHarborDbContext db, int nextIndex, int count, byte[] payload, byte[] hash)
    {
        var source = new ProxySource { Name = "Invalid import state", Url = $"https://example.org/import/{Guid.NewGuid():N}" };
        db.Sources.Add(source);
        db.ProxySourceImportStates.Add(new ProxySourceImportState
        {
            ProxySourceId = source.Id,
            SourceUrl = source.Url,
            CandidateCount = count,
            NextIndex = nextIndex,
            Payload = payload,
            PayloadHash = hash
        });
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
