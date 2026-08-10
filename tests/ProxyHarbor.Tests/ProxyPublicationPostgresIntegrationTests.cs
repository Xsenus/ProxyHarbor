using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Api;
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
    public async Task SeekListAndStreamingExportTraverseDeterministicOrderOnPostgres()
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

            await using var firstOutput = new MemoryStream();
            var firstExport = ExportController(factory, firstOutput);
            Assert.IsType<EmptyResult>(await firstExport.ExportSeek(
                "txt", null, null, null, CancellationToken.None, limit: 2));
            Assert.Equal(
                ["http://1.1.1.1:8080", "http://8.8.8.8:8080"],
                Encoding.UTF8.GetString(firstOutput.ToArray())
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            var exportCursor = firstExport.Response.Headers["X-Next-Cursor"].ToString();
            Assert.Equal(PublicationCursor.EncodedLength, exportCursor.Length);

            await using var secondOutput = new MemoryStream();
            var secondExport = ExportController(factory, secondOutput);
            Assert.IsType<EmptyResult>(await secondExport.ExportSeek(
                "txt", null, null, null, CancellationToken.None, limit: 2, after: exportCursor));
            Assert.Equal("http://9.9.9.9:8080", Encoding.UTF8.GetString(secondOutput.ToArray()).Trim());
            Assert.Equal("false", secondExport.Response.Headers["X-Export-Truncated"]);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ProxiesController ExportController(
        IDbContextFactory<ProxyHarborDbContext> factory,
        Stream output)
    {
        var controller = new ProxiesController(
            factory, Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = output } }
        };
        return controller;
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
