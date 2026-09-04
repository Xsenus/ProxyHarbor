using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет readiness против реальной PostgreSQL-схемы, а не только открытого socket.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class DatabaseReadinessProbeIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RequiresEveryOperationalTableAndCurrentColumnContract()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_readiness_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(options);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();
            var probe = new DatabaseReadinessProbe(factory);

            Assert.True(await probe.CheckAsync(CancellationToken.None));
            await using (var damage = await factory.CreateDbContextAsync())
                await damage.Database.ExecuteSqlRawAsync("""ALTER TABLE "VpnEndpoints" RENAME COLUMN "LastValidationDeferred" TO "MissingDeferredColumn";""");
            Assert.False(await probe.CheckAsync(CancellationToken.None));
            await using (var repair = await factory.CreateDbContextAsync())
                await repair.Database.ExecuteSqlRawAsync("""ALTER TABLE "VpnEndpoints" RENAME COLUMN "MissingDeferredColumn" TO "LastValidationDeferred";""");
            Assert.True(await probe.CheckAsync(CancellationToken.None));
            using (var cancellation = new CancellationTokenSource())
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => probe.CheckAsync(cancellation.Token));
            }

            await using (var damage = await factory.CreateDbContextAsync())
                await damage.Database.ExecuteSqlRawAsync("""DROP TABLE "Sources";""");

            await using (var connectivity = await factory.CreateDbContextAsync())
                Assert.True(await connectivity.Database.CanConnectAsync());
            Assert.False(await probe.CheckAsync(CancellationToken.None));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
