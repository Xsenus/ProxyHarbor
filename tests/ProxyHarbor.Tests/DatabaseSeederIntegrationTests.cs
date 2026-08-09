using Microsoft.EntityFrameworkCore;
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
