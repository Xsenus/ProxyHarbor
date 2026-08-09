using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет перевод keyset-предиката настоящим Npgsql, а не LINQ-to-Objects.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyPublicationPostgresIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SeekPredicateTraversesDeterministicOrderOnPostgres()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_seek_{Guid.NewGuid():N}";
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
            var factory = new TestDbFactory(options);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Proxies.AddRange(
                    Endpoint("1.1.1.1", "00000000-0000-0000-0000-000000000001", 100, 5, now),
                    Endpoint("8.8.8.8", "00000000-0000-0000-0000-000000000002", 100, 5, now),
                    Endpoint("9.9.9.9", "00000000-0000-0000-0000-000000000003", 200, 9, now));
                await seed.SaveChangesAsync();
            }

            var controller = new ProxiesController(
                factory, Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
            var firstAction = await controller.Seek(
                null, null, null, null, 2, CancellationToken.None);
            var firstPage = Assert.IsType<CursorPagedResult<ProxyDto>>(
                Assert.IsType<OkObjectResult>(firstAction.Result).Value);
            Assert.Equal(["1.1.1.1", "8.8.8.8"], firstPage.Items.Select(x => x.Host));

            var secondAction = await controller.Seek(
                null, null, null, firstPage.NextCursor, 2, CancellationToken.None);
            var secondPage = Assert.IsType<CursorPagedResult<ProxyDto>>(
                Assert.IsType<OkObjectResult>(secondAction.Result).Value);
            Assert.Equal("9.9.9.9", Assert.Single(secondPage.Items).Host);
            Assert.False(secondPage.HasMore);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ProxyEndpoint Endpoint(
        string host, string id, int latencyMs, int successfulChecks, DateTimeOffset now) => new()
        {
            Id = Guid.Parse(id),
            Host = host,
            Port = 8080,
            Protocol = ProxyProtocol.Http,
            Status = ProxyStatus.Alive,
            LatencyMs = latencyMs,
            SuccessfulChecks = successfulChecks,
            LastCheckedAt = now,
            NextCheckAt = now.AddMinutes(5)
        };

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
