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
    public async Task StartupRemovesRetiredBuiltInFeedAndSeedsItsReplacement()
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
        const string replacementUrl =
            "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/us/data.txt";

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                first.Sources.Add(new ProxySource
                {
                    Name = "Retired XYZS996 GR",
                    Url = retiredUrl,
                    DefaultProtocol = ProxyProtocol.Http
                });
                await first.SaveChangesAsync();
            }

            await using (var second = new ProxyHarborDbContext(options))
                await DatabaseSeeder.InitializeAsync(second);

            await using var verify = new ProxyHarborDbContext(options);
            Assert.False(await verify.Sources.AnyAsync(source => source.Url == retiredUrl));
            Assert.True(await verify.Sources.AnyAsync(source => source.Url == replacementUrl));
            Assert.Equal(BuiltInSourceCatalog.Sources.Count, await verify.Sources.CountAsync());
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
        "XYZS996 LV",
        "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/bt/data.txt")]
    [InlineData(
        "XYZS996 LU",
        "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/lt/data.txt")]
    [InlineData(
        "XYZS996 TH",
        "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/gq/data.txt")]
    [InlineData(
        "XYZS996 TR",
        "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/dk/data.txt")]
    [InlineData(
        "ProxyGenerator Cloudflare SOCKS4",
        "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/MostStable/socks4.txt")]
    [InlineData(
        "Fyvri HTTP",
        "https://raw.githubusercontent.com/fyvri/fresh-proxy-list/archive/storage/classic/http.txt")]
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
            Assert.Equal(BuiltInSourceCatalog.Sources.Count, await verify.Sources.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task StartupMigratesUnreachableVpnGateSourceAndResetsResourceState()
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
                source.Name == "Auto OVPN catalog");
            var sourceId = Guid.Empty;
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                var source = await first.VpnSources.SingleAsync(item => item.Url == canonical.Url);
                sourceId = source.Id;
                source.Url = "https://www.vpngate.net/api/iphone/";
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
            Assert.Equal(BuiltInSourceCatalog.Sources.Count, await verify.Sources.CountAsync());
            Assert.Equal(
                BuiltInSourceCatalog.Sources.Count,
                await verify.Sources.Select(source => source.Url).Distinct().CountAsync());
        }
        finally
        {
            // schema состоит только из фиксированного prefix и N-format GUID, поэтому identifier безопасен.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
