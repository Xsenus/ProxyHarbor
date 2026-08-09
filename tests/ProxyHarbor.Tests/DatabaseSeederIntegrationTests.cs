using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет безопасный одновременный startup нескольких реплик на чистой схеме.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class DatabaseSeederIntegrationTests
{
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
