using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет безопасный одновременный startup нескольких реплик на чистой схеме.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class DatabaseSeederIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RepeatedStartupPreservesEveryActiveCatalogSourceAndPendingSnapshot()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_catalog_restart_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString).Options;
            var observedAt = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
            var catalogUrls = BuiltInSourceCatalog.Sources.Select(source => source.Url).ToArray();
            Dictionary<string, Guid> previousIds;
            Guid snapshotId;
            const string stormUrl = "https://raw.githubusercontent.com/stormsia/proxy-list/main/http.txt";
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                var sources = await first.Sources.Where(source => catalogUrls.Contains(source.Url)).ToArrayAsync();
                Assert.Equal(catalogUrls.Length, sources.Length);
                previousIds = sources.ToDictionary(source => source.Url, source => source.Id, StringComparer.Ordinal);
                foreach (var source in sources)
                {
                    source.Enabled = false;
                    source.LastFetchedAt = observedAt;
                    source.LastSucceededAt = observedAt.AddMinutes(-10);
                    source.LastContentFetchedAt = observedAt.AddMinutes(-20);
                    source.HttpETag = "\"saved-body\"";
                    source.LastItemCount = 200;
                    source.LastResultTruncated = true;
                    source.ConsecutiveFailures = 2;
                    source.NextFetchAt = observedAt.AddHours(1);
                    source.LastError = "temporary feed failure";
                }
                var snapshot = ProxyCandidateSnapshotCodec.Encode("1.1.1.1:8080\n8.8.8.8:1080", ProxyProtocol.Http);
                var state = new ProxySourceImportState
                {
                    ProxySourceId = previousIds[stormUrl],
                    SourceUrl = stormUrl,
                    SourceProtocol = ProxyProtocol.Http,
                    CandidateCount = snapshot.Count,
                    NextIndex = 1,
                    Payload = snapshot.Payload,
                    PayloadHash = System.Security.Cryptography.SHA256.HashData(snapshot.Payload),
                    CreatedAt = observedAt,
                    LastProgressAt = observedAt
                };
                snapshotId = state.SnapshotId;
                first.ProxySourceImportStates.Add(state);
                await first.SaveChangesAsync();
            }

            for (var pass = 0; pass < 2; pass++)
            {
                await using var restart = new ProxyHarborDbContext(options);
                await DatabaseSeeder.InitializeAsync(restart);
                var sources = await restart.Sources.Where(source => catalogUrls.Contains(source.Url)).ToArrayAsync();
                Assert.Equal(previousIds.Count, sources.Length);
                foreach (var source in sources)
                {
                    Assert.Equal(previousIds[source.Url], source.Id);
                    Assert.False(source.Enabled);
                    Assert.Equal(observedAt, source.LastFetchedAt);
                    Assert.Equal(observedAt.AddMinutes(-10), source.LastSucceededAt);
                    Assert.Equal(observedAt.AddMinutes(-20), source.LastContentFetchedAt);
                    Assert.Equal("\"saved-body\"", source.HttpETag);
                    Assert.Equal(200, source.LastItemCount);
                    Assert.True(source.LastResultTruncated);
                    Assert.Equal(2, source.ConsecutiveFailures);
                    Assert.Equal(observedAt.AddHours(1), source.NextFetchAt);
                    Assert.Equal("temporary feed failure", source.LastError);
                }
                var state = await restart.ProxySourceImportStates.SingleAsync();
                Assert.Equal(previousIds[stormUrl], state.ProxySourceId);
                Assert.Equal(snapshotId, state.SnapshotId);
                Assert.Equal(2, state.CandidateCount);
                Assert.Equal(1, state.NextIndex);
                Assert.Equal(observedAt, state.LastProgressAt);
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task StartupUpgradesProxiflyTlsProfilesWithoutResettingSourceIdentityOrHistory()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_proxifly_tls_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString).Options;
            var fetchedAt = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
            var previousIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var customId = Guid.NewGuid();
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                var sources = await first.Sources.Where(source =>
                    source.Name == "Proxifly HTTPS" || source.Name.StartsWith("Proxifly country ")).ToArrayAsync();
                Assert.Equal(36, sources.Length);
                foreach (var source in sources)
                {
                    previousIds.Add(source.Url, source.Id);
                    source.DefaultProtocol = source.Name == "Proxifly HTTPS" ? ProxyProtocol.Https : ProxyProtocol.Http;
                    source.Enabled = false;
                    source.LastFetchedAt = fetchedAt;
                    source.LastSucceededAt = fetchedAt.AddMinutes(-10);
                    source.LastContentFetchedAt = fetchedAt.AddMinutes(-20);
                    source.NextFetchAt = fetchedAt.AddHours(1);
                    source.HttpETag = "\"existing-body\"";
                    source.HttpLastModifiedAt = fetchedAt.AddMinutes(-30);
                    source.LastItemCount = 123;
                    source.LastResultTruncated = true;
                    source.ConsecutiveFailures = 2;
                    source.LastError = "source temporarily unavailable";
                }
                first.Sources.Add(new ProxySource
                {
                    Id = customId,
                    Name = "custom HTTPS",
                    Url = "https://example.com/custom-https.txt",
                    DefaultProtocol = ProxyProtocol.Https,
                    Enabled = false
                });
                await first.SaveChangesAsync();
            }

            // Exercise repeated startup, including a database already upgraded once.
            for (var pass = 0; pass < 2; pass++)
            {
                await using var restart = new ProxyHarborDbContext(options);
                await DatabaseSeeder.InitializeAsync(restart);
            }

            await using var verify = new ProxyHarborDbContext(options);
            foreach (var (url, id) in previousIds)
            {
                var source = await verify.Sources.AsNoTracking().SingleAsync(source => source.Url == url);
                Assert.Equal(id, source.Id);
                Assert.Equal(ProxyProtocol.HttpTlsUnverified, source.DefaultProtocol);
                Assert.False(source.Enabled);
                Assert.Equal(fetchedAt, source.LastFetchedAt);
                Assert.Equal(fetchedAt.AddMinutes(-10), source.LastSucceededAt);
                Assert.Equal(fetchedAt.AddMinutes(-20), source.LastContentFetchedAt);
                Assert.Equal(fetchedAt.AddHours(1), source.NextFetchAt);
                Assert.Equal("\"existing-body\"", source.HttpETag);
                Assert.Equal(fetchedAt.AddMinutes(-30), source.HttpLastModifiedAt);
                Assert.Equal(123, source.LastItemCount);
                Assert.True(source.LastResultTruncated);
                Assert.Equal(2, source.ConsecutiveFailures);
                Assert.Equal("source temporarily unavailable", source.LastError);
            }
            var custom = await verify.Sources.AsNoTracking().SingleAsync(source => source.Id == customId);
            Assert.Equal(ProxyProtocol.Https, custom.DefaultProtocol);
            Assert.False(custom.Enabled);
            Assert.Equal(BuiltInSourceCatalog.Sources.Count + 2, await verify.Sources.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task LeaseMigrationPreservesInFlightOwnershipAndSupportsRollback()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_lease_migration_{Guid.NewGuid():N}";
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
            var proxyId = Guid.NewGuid();
            var leaseId = Guid.NewGuid();
            var leaseUntil = new DateTimeOffset(2026, 9, 2, 12, 34, 56, TimeSpan.Zero);
            await using var db = new ProxyHarborDbContext(options);
            var migrator = db.GetService<IMigrator>();
            const string previousMigration = "20260902050000_OptimizeAccessRegistryLookup";
            await migrator.MigrateAsync(previousMigration);
            db.Proxies.Add(new ProxyEndpoint
            {
                Id = proxyId,
                Host = "198.51.100.250",
                Port = 8250,
                CheckLeaseId = leaseId,
                CheckLeaseUntil = leaseUntil
            });
            await db.SaveChangesAsync();

            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();
            var migratedProxy = await db.Proxies.AsNoTracking().SingleAsync(proxy => proxy.Id == proxyId);
            var migratedLease = await db.ProxyValidationLeases.AsNoTracking()
                .SingleAsync(lease => lease.ProxyId == proxyId);
            Assert.Null(migratedProxy.CheckLeaseId);
            Assert.Null(migratedProxy.CheckLeaseUntil);
            Assert.Equal(leaseId, migratedLease.LeaseId);
            Assert.Equal(leaseUntil, migratedLease.LeaseUntil);

            await migrator.MigrateAsync(previousMigration);
            db.ChangeTracker.Clear();
            var rolledBack = await db.Proxies.AsNoTracking().SingleAsync(proxy => proxy.Id == proxyId);
            Assert.Equal(leaseId, rolledBack.CheckLeaseId);
            Assert.Equal(leaseUntil, rolledBack.CheckLeaseUntil);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ValidationQueueIndexesMatchExactPriorityAndDueOrders()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_claim_index_{Guid.NewGuid():N}";
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
            await using (var db = new ProxyHarborDbContext(options))
                await db.Database.MigrateAsync();

            await using var inspect = new NpgsqlCommand(
                """
                SELECT indexdef
                FROM pg_indexes
                WHERE schemaname = @schema
                  AND indexname = 'IX_Proxies_ValidationQueueOrder'
                """,
                admin);
            inspect.Parameters.AddWithValue("schema", schema);
            var definition = Assert.IsType<string>(await inspect.ExecuteScalarAsync());

            Assert.Contains("CASE \"Status\"", definition, StringComparison.Ordinal);
            Assert.Contains("WHEN 1 THEN 0", definition, StringComparison.Ordinal);
            Assert.Contains("WHEN 0 THEN 1", definition, StringComparison.Ordinal);
            Assert.Contains("\"NextCheckAt\" NULLS FIRST", definition, StringComparison.Ordinal);
            Assert.Contains("\"LastCheckedAt\" NULLS FIRST", definition, StringComparison.Ordinal);
            await using var inspectPaidPriority = new NpgsqlCommand(
                """
                SELECT indexdef
                FROM pg_indexes
                WHERE schemaname = @schema
                  AND indexname = 'IX_Proxies_PaidValidationPriority'
                """,
                admin);
            inspectPaidPriority.Parameters.AddWithValue("schema", schema);
            var paidDefinition = Assert.IsType<string>(await inspectPaidPriority.ExecuteScalarAsync());
            Assert.Contains("CASE \"Status\"", paidDefinition, StringComparison.Ordinal);
            Assert.Contains("\"LastCheckedAt\" NULLS FIRST", paidDefinition, StringComparison.Ordinal);
            Assert.Contains("1970-01-01", paidDefinition, StringComparison.Ordinal);
            await using var inspectLeaseTable = new NpgsqlCommand(
                """
                SELECT c.relpersistence,
                       EXISTS (
                           SELECT 1 FROM pg_indexes
                           WHERE schemaname = @schema
                             AND indexname = 'IX_ProxyValidationLeases_LeaseId'),
                       EXISTS (
                           SELECT 1 FROM pg_indexes
                           WHERE schemaname = @schema
                             AND indexname = 'IX_ProxyValidationLeases_LeaseUntil'),
                       c.reloptions
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = 'ProxyValidationLeases'
                """,
                admin);
            inspectLeaseTable.Parameters.AddWithValue("schema", schema);
            await using (var leaseReader = await inspectLeaseTable.ExecuteReaderAsync())
            {
                Assert.True(await leaseReader.ReadAsync());
                Assert.Equal('u', leaseReader.GetChar(0));
                Assert.True(leaseReader.GetBoolean(1));
                Assert.True(leaseReader.GetBoolean(2));
                var relationOptions = leaseReader.GetFieldValue<string[]>(3);
                Assert.Contains("fillfactor=90", relationOptions);
                Assert.Contains("autovacuum_vacuum_scale_factor=0.02", relationOptions);
                Assert.Contains("autovacuum_vacuum_threshold=500", relationOptions);
                Assert.Contains("autovacuum_analyze_scale_factor=0.02", relationOptions);
                Assert.Contains("autovacuum_analyze_threshold=250", relationOptions);
            }

            await using var inspectRetired = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_indexes
                    WHERE schemaname = @schema
                      AND indexname IN (
                          'IX_Proxies_ValidationClaimOrder',
                          'IX_Proxies_ValidationClaimUnleased',
                          'IX_Proxies_ExpiredLeaseClaim',
                          'IX_Proxies_CheckLeaseId',
                          'IX_Proxies_NextCheckAt_CheckLeaseUntil'))
                """,
                admin);
            inspectRetired.Parameters.AddWithValue("schema", schema);
            Assert.False(Assert.IsType<bool>(await inspectRetired.ExecuteScalarAsync()));

            await using var inspectVpn = new NpgsqlCommand(
                """
                SELECT indexdef
                FROM pg_indexes
                WHERE schemaname = @schema
                  AND indexname = 'IX_VpnEndpoints_ValidationOrder'
                """,
                admin);
            inspectVpn.Parameters.AddWithValue("schema", schema);
            var vpnDefinition = Assert.IsType<string>(await inspectVpn.ExecuteScalarAsync());
            Assert.Contains("\"NextCheckAt\", \"LastCheckedAt\", \"Id\"", vpnDefinition,
                StringComparison.Ordinal);
            Assert.DoesNotContain("NULLS FIRST", vpnDefinition, StringComparison.Ordinal);

            await using var inspectVpnNull = new NpgsqlCommand(
                """
                SELECT indexdef
                FROM pg_indexes
                WHERE schemaname = @schema
                  AND indexname = 'IX_VpnEndpoints_ValidationNullOrder'
                """,
                admin);
            inspectVpnNull.Parameters.AddWithValue("schema", schema);
            var vpnNullDefinition = Assert.IsType<string>(await inspectVpnNull.ExecuteScalarAsync());
            Assert.Contains("\"LastCheckedAt\", \"Id\"", vpnNullDefinition,
                StringComparison.Ordinal);
            Assert.Contains("WHERE (\"NextCheckAt\" IS NULL)", vpnNullDefinition,
                StringComparison.Ordinal);

        }
        finally
        {
            // schema состоит только из фиксированного prefix и N-format GUID, поэтому identifier безопасен.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CleanupFailuresPreservePrimaryStartupFailureAndDiscardLockSession()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_startup_cleanup_{Guid.NewGuid():N}";
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
            var primaryFailure = new InvalidOperationException("Deterministic primary startup failure.");

            InvalidOperationException thrown;
            await using (var db = new ProxyHarborDbContext(options))
            {
                thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DatabaseSeeder.InitializeAsync(
                        db,
                        new DatabaseSeederExecutionHooks(
                            AfterMigrationLockAcquired: () => throw primaryFailure,
                            BeforeMigrationLockRelease: () =>
                                throw new IOException("Deterministic unlock failure."),
                            BeforeConnectionClose: () =>
                                throw new IOException("Deterministic close failure.")),
                        CancellationToken.None));
            }

            Assert.Same(primaryFailure, thrown);
            var cleanupEvidence = Assert.IsType<string>(
                thrown.Data[DatabaseSeeder.StartupCleanupFailureDataKey]);
            Assert.Equal("unlock: IOException | close: IOException", cleanupEvidence);

            // Pooling=false гарантирует новую физическую backend-сессию. Если failed cleanup
            // вернул прежнего владельца lock в pool или не закрыл его, try-lock здесь вернёт false.
            var probeBuilder = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Pooling = false };
            await using var probe = new NpgsqlConnection(probeBuilder.ConnectionString);
            await probe.OpenAsync();
            await using var lockCommand = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(@key)", probe);
            lockCommand.Parameters.AddWithValue("key", DatabaseSeeder.MigrationLockKey);
            Assert.True((bool)(await lockCommand.ExecuteScalarAsync() ?? false));
            await using var unlockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(@key)", probe);
            unlockCommand.Parameters.AddWithValue("key", DatabaseSeeder.MigrationLockKey);
            Assert.True((bool)(await unlockCommand.ExecuteScalarAsync() ?? false));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ValidationTelemetryMigrationBackfillsHistoricalChecks()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_telemetry_{Guid.NewGuid():N}";
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
            await using (var oldSchema = new ProxyHarborDbContext(options))
                await oldSchema.Database.MigrateAsync("20260809124639_CollectionCompletenessAudit");

            var proxyId = Guid.NewGuid();
            var checkedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await using (var oldConnection = new NpgsqlConnection(builder.ConnectionString))
            {
                await oldConnection.OpenAsync();
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO "Proxies"
                        ("Id", "Host", "Port", "Protocol", "Status", "IsAnonymous",
                         "FirstSeenAt", "LastSeenAt", "LastCheckedAt", "SuccessfulChecks",
                         "FailedChecks", "ConsecutiveFailedChecks")
                    VALUES
                        (@id, '198.51.100.25', 8080, 0, 1, TRUE,
                         @checkedAt, @checkedAt, @checkedAt, 1, 0, 0)
                    """,
                    oldConnection);
                insert.Parameters.AddWithValue("id", proxyId);
                insert.Parameters.AddWithValue("checkedAt", checkedAt);
                await insert.ExecuteNonQueryAsync();
            }

            await using (var upgraded = new ProxyHarborDbContext(options))
                await upgraded.Database.MigrateAsync();

            await using var verify = new ProxyHarborDbContext(options);
            var proxy = await verify.Proxies.SingleAsync(item => item.Id == proxyId);
            Assert.Equal(proxy.LastCheckedAt, proxy.LastValidationAttemptAt);
            Assert.InRange(
                Math.Abs((proxy.LastCheckedAt!.Value - checkedAt).TotalMilliseconds),
                0,
                0.001);
            Assert.False(proxy.LastValidationDeferred);
        }
        finally
        {
            // schema состоит только из фиксированного prefix и N-format GUID, поэтому identifier безопасен.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task StartupPreservesCustomSourcesWhosePathsDifferOnlyByCase()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_case_{Guid.NewGuid():N}";
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
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                first.Sources.AddRange(
                    new ProxySource { Name = "Upper path", Url = "https://8.8.8.8/Feed.txt" },
                    new ProxySource { Name = "Lower path", Url = "https://8.8.8.8/feed.txt" });
                await first.SaveChangesAsync();
            }

            await using (var second = new ProxyHarborDbContext(options))
                await DatabaseSeeder.InitializeAsync(second);

            await using var verify = new ProxyHarborDbContext(options);
            Assert.Equal(2, await verify.Sources.CountAsync(source => source.Url.StartsWith("https://8.8.8.8/")));
            Assert.Equal(BuiltInSourceCatalog.Sources.Count + 3, await verify.Sources.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task StartupRemovesRetiredDynamicCountryFeedAndKeepsStableAggregate()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_retired_source_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        const string retiredUrl =
            "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/gr/data.txt";
        const string aggregateUrl =
            "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/all.txt";
        const string customUrl = "https://example.com/operator-proxies.txt";

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                first.Sources.AddRange(
                    new ProxySource
                    {
                        Name = "Retired XYZS996 GR",
                        Url = retiredUrl,
                        DefaultProtocol = ProxyProtocol.Http
                    },
                    new ProxySource
                    {
                        Name = "Operator source",
                        Url = customUrl,
                        DefaultProtocol = ProxyProtocol.Http
                    });
                await first.SaveChangesAsync();
            }

            await using (var second = new ProxyHarborDbContext(options))
                await DatabaseSeeder.InitializeAsync(second);

            await using var verify = new ProxyHarborDbContext(options);
            Assert.False(await verify.Sources.AnyAsync(source => source.Url == retiredUrl));
            Assert.True(await verify.Sources.AnyAsync(source => source.Url == aggregateUrl));
            Assert.True(await verify.Sources.AnyAsync(source => source.Url == customUrl));
            Assert.Equal(BuiltInSourceCatalog.Sources.Count + 2, await verify.Sources.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task StartupRemovesRetiredBuiltInProxyAndVpnFeeds()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_retired_feeds_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        string[] retiredProxyUrls =
        [
            "https://raw.githubusercontent.com/Akshay7273/ProxyMan-free-proxy-list/main/protocols/http.txt",
            "https://raw.githubusercontent.com/Akshay7273/ProxyMan-free-proxy-list/main/protocols/socks4.txt",
            "https://raw.githubusercontent.com/Akshay7273/ProxyMan-free-proxy-list/main/protocols/socks5.txt",
            "https://raw.githubusercontent.com/iamthebestm85/Proxy-Scraper-And-Checker/main/proxy.txt",
            "https://raw.githubusercontent.com/just-not-google/full-free-proxy/main/http.txt",
            "https://raw.githubusercontent.com/Allaux/fresh-proxy-list/main/http.txt",
            "https://raw.githubusercontent.com/ProTechEx/PROXY-List/master/http.txt",
            "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/DZ.txt",
            "https://raw.githubusercontent.com/CelestialBrain/worldpool/main/proxies/http.txt",
            "https://raw.githubusercontent.com/gproxynet/free-proxy-list/main/http.txt",
            "https://raw.githubusercontent.com/lanzm/MetaFetch/master/list.txt",
            "https://raw.githubusercontent.com/KhaiNguyenDuc/proxy-generator/main/proxies.txt",
            "https://cyber-gateway.net/get-proxy/free-proxy/24-free-http-proxy",
            "https://raw.githubusercontent.com/BuntheaTaing/Proxy-Scraper/main/proxy.txt",
            "https://raw.githubusercontent.com/CrateC/proxy_list/main/proxies.txt",
            "https://raw.githubusercontent.com/du0ngtrunghieu/proxy-scraper/main/http.txt",
            "https://raw.githubusercontent.com/Yogazyy/PROXY-List/main/http.txt",
            "https://raw.githubusercontent.com/mishakorzik/100000-Proxy/main/proxy.txt",
            "https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/countries/MD/data.txt",
            "https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/countries/UZ/data.txt",
            "https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/countries/CY/data.txt",
            "https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/countries/LU/data.txt"
        ];
        string[] retiredVpnUrls =
        [
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Splitted-By-Protocol/ss.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Splitted-By-Protocol/vless.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Splitted-By-Protocol/vmess.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub1.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Splitted-By-Protocol/trojan.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub3.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub2.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub4.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub5.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub6.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub7.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub9.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub10.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub8.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub11.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub12.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub13.txt",
            "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub14.txt",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/country/UZ/v2ray-base64-0001.txt",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/country/MD/v2ray-base64-0001.txt",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/country/DK/v2ray-base64-0001.txt",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/country/MO/v2ray-base64-0001.txt",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0009.txt",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/country/AF/v2ray-base64-0001.txt",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/country/MU/v2ray-base64-0001.txt",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/country/BE/v2ray-base64-0001.txt",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/country/EG/v2ray-base64-0001.txt"
        ];

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                first.Sources.AddRange(retiredProxyUrls.Select((url, index) => new ProxySource
                {
                    Name = $"Retired proxy {index}",
                    Url = url,
                    DefaultProtocol = ProxyProtocol.Http
                }));
                first.VpnSources.AddRange(retiredVpnUrls.Select((url, index) => new VpnSource
                {
                    Name = $"Retired VPN {index}",
                    Provider = "Au1rxx/free-vpn-subscriptions",
                    Url = url,
                    DefaultProtocol = VpnProtocol.Vless,
                    License = "MIT"
                }));
                first.VpnSources.Add(new VpnSource
                {
                    Name = "Custom VPN retained",
                    Provider = "Custom",
                    Url = "https://example.com/custom-vpn-subscription.txt",
                    DefaultProtocol = VpnProtocol.Vless,
                    License = "custom"
                });
                await first.SaveChangesAsync();
            }

            await using (var second = new ProxyHarborDbContext(options))
                await DatabaseSeeder.InitializeAsync(second);

            await using var verify = new ProxyHarborDbContext(options);
            Assert.False(await verify.Sources.AnyAsync(source => retiredProxyUrls.Contains(source.Url)));
            Assert.False(await verify.VpnSources.AnyAsync(source => retiredVpnUrls.Contains(source.Url)));
            Assert.Equal(BuiltInSourceCatalog.Sources.Count + 1, await verify.Sources.CountAsync());
            Assert.True(await verify.VpnSources.AnyAsync(source => source.Url == "https://example.com/custom-vpn-subscription.txt"));
            Assert.Equal(BuiltInVpnSourceCatalog.Sources.Count + 1, await verify.VpnSources.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData(
        "TheSpeedX HTTP",
        "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/refs/heads/master/http.txt")]
    [InlineData(
        "ProxyGenerator Cloudflare SOCKS4",
        "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/MostStable/socks4.txt")]
    [InlineData(
        "Fyvri HTTP",
        "https://raw.githubusercontent.com/fyvri/fresh-proxy-list/archive/storage/classic/http.txt")]
    [InlineData(
        "ProxyScrape V4 Mixed",
        "https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=text")]
    [InlineData(
        "Proxy List Gamt HTTP",
        "https://raw.githubusercontent.com/Denisyoya/Proxy-List-Gamt/main/proxy/http.txt")]
    [Trait("Category", "PostgresIntegration")]
    public async Task StartupMigratesReplacedBuiltInUrlWithoutLosingSourceHistory(
        string canonicalName,
        string replacedUrl)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_source_url_{Guid.NewGuid():N}";
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
            var canonical = BuiltInSourceCatalog.Sources.Single(source => source.Name == canonicalName);
            var sourceId = Guid.Empty;
            var lastFetchedAt = DateTimeOffset.UtcNow;
            var lastSucceededAt = lastFetchedAt.AddHours(-1);
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                var source = await first.Sources.SingleAsync(item => item.Url == canonical.Url);
                sourceId = source.Id;
                source.Url = replacedUrl;
                source.Enabled = false;
                source.LastFetchedAt = lastFetchedAt;
                source.LastSucceededAt = lastSucceededAt;
                source.LastItemCount = 123;
                source.HttpETag = "stale-etag";
                source.HttpLastModifiedAt = lastSucceededAt;
                source.ConsecutiveFailures = 2;
                source.NextFetchAt = DateTimeOffset.UtcNow.AddHours(1);
                source.LastError = "HTTP 400";
                await first.SaveChangesAsync();
            }

            await using (var second = new ProxyHarborDbContext(options))
                await DatabaseSeeder.InitializeAsync(second);

            await using var verify = new ProxyHarborDbContext(options);
            var migrated = await verify.Sources.SingleAsync(source => source.Url == canonical.Url);
            Assert.Equal(sourceId, migrated.Id);
            Assert.False(migrated.Enabled);
            Assert.InRange(
                Math.Abs((migrated.LastSucceededAt!.Value - lastSucceededAt).TotalMilliseconds),
                0,
                0.001);
            Assert.Equal(123, migrated.LastItemCount);
            Assert.Null(migrated.HttpETag);
            Assert.Null(migrated.HttpLastModifiedAt);
            Assert.Equal(0, migrated.ConsecutiveFailures);
            Assert.Null(migrated.NextFetchAt);
            Assert.Null(migrated.LastError);
            Assert.Equal(BuiltInSourceCatalog.Sources.Count + 1, await verify.Sources.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("Auto OVPN catalog", "https://www.vpngate.net/api/iphone/")]
    [InlineData("Auto OVPN catalog", "https://raw.githubusercontent.com/9xN/auto-ovpn/main/configs/server_0_JP.ovpn")]
    [InlineData("Black Crow VLESS", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/vless.txt")]
    [InlineData("Black Crow VMess", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/vmess.txt")]
    [InlineData("Black Crow Shadowsocks", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/ss.txt")]
    [InlineData("Black Crow Trojan", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/trojan.txt")]
    [InlineData("Black Crow mixed", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/hysteria2.txt")]
    [Trait("Category", "PostgresIntegration")]
    public async Task StartupMigratesReplacedVpnSourceAndResetsResourceState(string canonicalName, string replacedUrl)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_vpn_source_url_{Guid.NewGuid():N}";
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
            var canonical = BuiltInVpnSourceCatalog.Sources.Single(source =>
                source.Name == canonicalName);
            var sourceId = Guid.Empty;
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                var source = await first.VpnSources.SingleAsync(item => item.Url == canonical.Url);
                sourceId = source.Id;
                source.Url = replacedUrl;
                source.Name = "Legacy source name";
                source.License = "Legacy publication terms";
                if (replacedUrl.EndsWith("/hysteria2.txt", StringComparison.Ordinal))
                    source.DefaultProtocol = VpnProtocol.Hysteria2;
                source.Enabled = false;
                source.LastFetchedAt = DateTimeOffset.UtcNow;
                source.LastSucceededAt = source.LastFetchedAt.Value.AddHours(-1);
                source.LastContentFetchedAt = source.LastSucceededAt;
                source.LastItemCount = 123;
                source.HttpETag = "stale-etag";
                source.HttpLastModifiedAt = source.LastSucceededAt;
                source.ConsecutiveFailures = 492;
                source.NextFetchAt = DateTimeOffset.UtcNow.AddDays(1);
                source.LastError = "timeout";
                await first.SaveChangesAsync();
            }

            await using (var second = new ProxyHarborDbContext(options))
                await DatabaseSeeder.InitializeAsync(second);

            await using var verify = new ProxyHarborDbContext(options);
            var migrated = await verify.VpnSources.SingleAsync(source => source.Url == canonical.Url);
            Assert.Equal(sourceId, migrated.Id);
            Assert.False(migrated.Enabled);
            Assert.Equal(canonical.Name, migrated.Name);
            Assert.Equal(canonical.Provider, migrated.Provider);
            Assert.Equal(canonical.Protocol, migrated.DefaultProtocol);
            Assert.Equal(canonical.License, migrated.License);
            Assert.Null(migrated.LastFetchedAt);
            Assert.Null(migrated.LastSucceededAt);
            Assert.Null(migrated.LastContentFetchedAt);
            Assert.Equal(0, migrated.LastItemCount);
            Assert.Null(migrated.HttpETag);
            Assert.Null(migrated.HttpLastModifiedAt);
            Assert.Equal(0, migrated.ConsecutiveFailures);
            Assert.Null(migrated.NextFetchAt);
            Assert.Null(migrated.LastError);
            Assert.Equal(BuiltInVpnSourceCatalog.Sources.Count, await verify.VpnSources.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ConcurrentInitializationSerializesMigrationsAndSeed()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_test_{Guid.NewGuid():N}";
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
            await using var first = new ProxyHarborDbContext(options);
            await using var second = new ProxyHarborDbContext(options);

            await Task.WhenAll(
                DatabaseSeeder.InitializeAsync(first),
                DatabaseSeeder.InitializeAsync(second));

            await using var verify = new ProxyHarborDbContext(options);
            Assert.Empty(await verify.Database.GetPendingMigrationsAsync());
            Assert.Equal(BuiltInSourceCatalog.Sources.Count + 1, await verify.Sources.CountAsync());
            Assert.Equal(
                BuiltInSourceCatalog.Sources.Count + 1,
                await verify.Sources.Select(source => source.Url).Distinct().CountAsync());
            var paid = await verify.Sources.SingleAsync(source =>
                source.Url == PaidProxySourceCatalog.BestProxiesUrl);
            Assert.Equal(PaidProxySourceCatalog.BestProxiesName, paid.Name);
            Assert.Equal(PaidProxySourceCatalog.BestProxiesPriority, paid.Priority);
            Assert.False(paid.Enabled);
        }
        finally
        {
            // schema состоит только из фиксированного prefix и N-format GUID, поэтому identifier безопасен.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
