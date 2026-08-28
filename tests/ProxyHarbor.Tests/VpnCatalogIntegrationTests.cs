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
            await using var verify = await factory.CreateDbContextAsync();
            var source = await verify.VpnSources.AsNoTracking().SingleAsync();
            Assert.Equal(0, source.ConsecutiveFailures);
            Assert.Null(source.LastError);
            Assert.Null(source.NextFetchAt);
            Assert.NotNull(source.LastSucceededAt);
            Assert.Single(await verify.VpnEndpoints.AsNoTracking().ToArrayAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
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
}
