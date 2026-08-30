using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет расписание VPN-feed на настоящей PostgreSQL вместе с advisory lock.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class VpnCatalogIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task BulkImportDeduplicatesEndpointsAndUsesConfiguredSourcePriority()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_vpn_bulk_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(dbOptions);
            Guid preferredSourceId;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                var preferred = new VpnSource
                {
                    Name = "Preferred VPN feed",
                    Provider = "Integration test",
                    Url = "https://8.8.8.8/preferred.txt",
                    DefaultProtocol = VpnProtocol.Vless,
                    Priority = 10,
                    License = "MIT"
                };
                seed.VpnSources.AddRange(preferred, new VpnSource
                {
                    Name = "Secondary VPN feed",
                    Provider = "Integration test",
                    Url = "https://8.8.4.4/secondary.txt",
                    DefaultProtocol = VpnProtocol.Vless,
                    Priority = 20,
                    License = "MIT"
                });
                await seed.SaveChangesAsync();
                preferredSourceId = preferred.Id;
            }

            using var clients = new TestHttpClientFactory(new DelegateHandler(request =>
            {
                var marker = request.RequestUri?.AbsolutePath.Contains("preferred", StringComparison.Ordinal) == true
                    ? "preferred"
                    : "secondary";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"vless://{marker}@8.8.4.4:443?type=tcp#{marker}")
                };
            }));
            var service = new VpnCatalogService(factory, clients, Options.Create(new CollectorOptions
            {
                SourceConcurrency = 2,
                SourceTimeoutSeconds = 5
            }), NullLogger<VpnCatalogService>.Instance);

            var result = await service.CollectAsync(forceAllSources: true);

            Assert.Equal(2, result.Sources);
            Assert.Equal(2, result.Succeeded);
            Assert.Equal(2, result.Candidates);
            Assert.Equal(1, result.Added);
            await using var verify = await factory.CreateDbContextAsync();
            var endpoint = await verify.VpnEndpoints.AsNoTracking().SingleAsync();
            Assert.Equal(preferredSourceId, endpoint.FirstSourceId);
            Assert.Contains("preferred", endpoint.ConnectionUri, StringComparison.Ordinal);
            Assert.Equal(2, await verify.VpnEndpointSources.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FailedSourceBacksOffAutomaticRunsAndManualRunCanRecoverIt()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_vpn_backoff_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                seed.VpnSources.Add(new VpnSource
                {
                    Name = "Recovering VPN feed",
                    Provider = "Integration test",
                    Url = "https://8.8.8.8/vpn.txt",
                    DefaultProtocol = VpnProtocol.Vless,
                    License = "MIT"
                });
                await seed.SaveChangesAsync();
            }

            var handler = new SequencedHandler();
            using var clients = new TestHttpClientFactory(handler);
            var service = new VpnCatalogService(factory, clients, Options.Create(new CollectorOptions
            {
                SourceConcurrency = 1,
                SourceTimeoutSeconds = 5,
                SourceFailureBackoffBaseMinutes = 15,
                SourceFailureBackoffMaxHours = 24
            }), NullLogger<VpnCatalogService>.Instance);

            var failed = await service.CollectAsync(forceAllSources: true);
            var paused = await service.CollectAsync();
            var recovered = await service.CollectAsync(forceAllSources: true);

            Assert.Equal(1, failed.Sources);
            Assert.Equal(0, failed.Succeeded);
            Assert.Equal(0, paused.Sources);
            Assert.Equal(1, recovered.Succeeded);
            Assert.Equal(2, handler.Requests);
            Guid sourceId;
            Guid endpointId;
            await using (var verify = await factory.CreateDbContextAsync())
            {
                var source = await verify.VpnSources.AsNoTracking().SingleAsync();
                var endpoint = await verify.VpnEndpoints.AsNoTracking().SingleAsync();
                Assert.Equal(0, source.ConsecutiveFailures);
                Assert.Null(source.LastError);
                Assert.Null(source.NextFetchAt);
                Assert.NotNull(source.LastSucceededAt);
                sourceId = source.Id;
                endpointId = endpoint.Id;
                Assert.Single(await verify.VpnEndpointSources.AsNoTracking().ToArrayAsync());
            }

            var versionsBefore = await ReadCatalogVersionsAsync(
                builder.ConnectionString, endpointId, sourceId);
            var repeated = await service.CollectAsync(forceAllSources: true);
            var versionsAfter = await ReadCatalogVersionsAsync(
                builder.ConnectionString, endpointId, sourceId);

            Assert.Equal(0, repeated.Added);
            Assert.Equal(3, handler.Requests);
            // Повтор идентичного feed внутри LastSeenRefreshMinutes не создаёт новые
            // MVCC-версии двух самых горячих таблиц каталога.
            Assert.Equal(versionsBefore, versionsAfter);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<(string Endpoint, string Provenance)> ReadCatalogVersionsAsync(
        string connectionString,
        Guid endpointId,
        Guid sourceId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT endpoint.xmin::text, provenance.xmin::text
            FROM "VpnEndpoints" endpoint
            JOIN "VpnEndpointSources" provenance
              ON provenance."VpnEndpointId" = endpoint."Id"
            WHERE endpoint."Id" = @endpoint_id
              AND provenance."VpnSourceId" = @source_id
            """, connection);
        command.Parameters.AddWithValue("endpoint_id", endpointId);
        command.Parameters.AddWithValue("source_id", sourceId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        public HttpClient CreateClient(string name) => client;
        public void Dispose() => client.Dispose();
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private int requests;
        internal int Requests => Volatile.Read(ref requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref requests);
            return Task.FromResult(requestNumber == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("vless://test-id@8.8.4.4:443?type=tcp#integration")
                });
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
