using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет нейтральное сохранение deferred-результата на настоящей PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyValidationPersistenceIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DeferredResultReleasesLeaseWithoutDamagingProxyQuality()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var factory = new TestDbFactory(dbOptions);
        await using (var migrationDb = await factory.CreateDbContextAsync())
            await migrationDb.Database.MigrateAsync();

        var proxyId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var checkedAt = new DateTimeOffset(2026, 8, 9, 7, 0, 0, TimeSpan.Zero);
        var proxy = new ProxyEndpoint
        {
            Id = proxyId,
            Host = $"11.22.{proxyId.ToByteArray()[0]}.{proxyId.ToByteArray()[1]}",
            Port = 50_000 + proxyId.ToByteArray()[2],
            Protocol = ProxyProtocol.Http,
            Status = ProxyStatus.Alive,
            LatencyMs = 123,
            ExitIp = "8.8.8.8",
            IsAnonymous = true,
            LastCheckedAt = checkedAt,
            NextCheckAt = checkedAt,
            CheckLeaseId = leaseId,
            CheckLeaseUntil = checkedAt.AddMinutes(5),
            SuccessfulChecks = 5,
            FailedChecks = 2,
            ConsecutiveFailedChecks = 3
        };
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Proxies.Add(proxy);
            await seed.SaveChangesAsync();
        }

        var settings = new CollectorOptions();
        using var clients = new StubHttpClientFactory();
        using var origin = new OriginIpProvider(clients, Options.Create(settings), new ProbeControlHealth());
        using var validator = new ProxyValidator(
            factory,
            new ProxyProbeService(Options.Create(settings), origin),
            Options.Create(settings),
            NullLogger<ProxyValidator>.Instance);
        var deferred = ProxyCheckScheduler.Create(
            new ProxyCheckResult(proxyId, false, null, null, false, "control unavailable", IsDeferred: true),
            proxy.ConsecutiveFailedChecks,
            leaseId,
            checkedAt.AddMinutes(1),
            settings);

        try
        {
            var result = await validator.PersistResultsAsync([deferred], CancellationToken.None);

            Assert.Equal((0, 0, 1), result);
            await using var verify = await factory.CreateDbContextAsync();
            var saved = await verify.Proxies.AsNoTracking().SingleAsync(x => x.Id == proxyId);
            Assert.Equal(ProxyStatus.Alive, saved.Status);
            Assert.Equal(123, saved.LatencyMs);
            Assert.Equal("8.8.8.8", saved.ExitIp);
            Assert.True(saved.IsAnonymous);
            Assert.Equal(checkedAt, saved.LastCheckedAt);
            Assert.Equal(5, saved.SuccessfulChecks);
            Assert.Equal(2, saved.FailedChecks);
            Assert.Equal(3, saved.ConsecutiveFailedChecks);
            Assert.Null(saved.CheckLeaseId);
            Assert.Null(saved.CheckLeaseUntil);
            Assert.Equal(checkedAt.AddMinutes(2), saved.NextCheckAt);
            Assert.Equal("control unavailable", saved.LastError);
        }
        finally
        {
            await using var cleanup = await factory.CreateDbContextAsync();
            await cleanup.Proxies.Where(x => x.Id == proxyId).ExecuteDeleteAsync();
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new StubHandler());
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ip\":\"8.8.8.8\"}")
            });
    }
}
