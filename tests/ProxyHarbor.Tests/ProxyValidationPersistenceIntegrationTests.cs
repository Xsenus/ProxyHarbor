using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет нейтральное сохранение deferred-результата на настоящей PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyValidationPersistenceIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CancellationAfterClaimImmediatelyReleasesLeaseWithoutChangingQuality()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_validation_cancel_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverStop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(serverStop.Token);
            accepted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, serverStop.Token);
        });

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();
            var proxyId = Guid.NewGuid();
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Proxies.Add(new ProxyEndpoint
                {
                    Id = proxyId,
                    Host = IPAddress.Loopback.ToString(),
                    Port = port,
                    Protocol = ProxyProtocol.Http,
                    Status = ProxyStatus.Alive,
                    SuccessfulChecks = 7,
                    FailedChecks = 2,
                    ConsecutiveFailedChecks = 1
                });
                await seed.SaveChangesAsync();
            }

            var settings = new CollectorOptions
            {
                ValidationBatchSize = 1,
                ValidationConcurrency = 1,
                ProbeTimeoutSeconds = 10
            };
            using var clients = new StubHttpClientFactory();
            using var origin = new OriginIpProvider(clients, Options.Create(settings), new ProbeControlHealth());
            using var validator = new ProxyValidator(
                factory,
                new ProxyProbeService(Options.Create(settings), origin),
                Options.Create(settings),
                NullLogger<ProxyValidator>.Instance);
            using var cancellation = new CancellationTokenSource();

            var validation = validator.ValidateBatchAsync(cancellation.Token);
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);

            await using var verify = await factory.CreateDbContextAsync();
            var saved = await verify.Proxies.AsNoTracking().SingleAsync(proxy => proxy.Id == proxyId);
            Assert.Null(saved.CheckLeaseId);
            Assert.Null(saved.CheckLeaseUntil);
            Assert.Null(saved.LastCheckedAt);
            Assert.Equal(ProxyStatus.Alive, saved.Status);
            Assert.Equal(7, saved.SuccessfulChecks);
            Assert.Equal(2, saved.FailedChecks);
            Assert.Equal(1, saved.ConsecutiveFailedChecks);
        }
        finally
        {
            await serverStop.CancelAsync();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task LeaseHeartbeatAndCleanupAffectOnlyExactOwnerToken()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var factory = new TestDbFactory(dbOptions);
        await using (var migrationDb = await factory.CreateDbContextAsync())
            await migrationDb.Database.MigrateAsync();

        var ownedId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var ownedLease = Guid.NewGuid();
        var foreignLease = Guid.NewGuid();
        var initialExpiry = new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);
        var renewedExpiry = initialExpiry.AddMinutes(5);
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Proxies.AddRange(
                new ProxyEndpoint
                {
                    Id = ownedId,
                    Host = $"12.34.{ownedId.ToByteArray()[0]}.{ownedId.ToByteArray()[1]}",
                    Port = 40_000 + ownedId.ToByteArray()[2],
                    CheckLeaseId = ownedLease,
                    CheckLeaseUntil = initialExpiry
                },
                new ProxyEndpoint
                {
                    Id = foreignId,
                    Host = $"13.35.{foreignId.ToByteArray()[0]}.{foreignId.ToByteArray()[1]}",
                    Port = 40_000 + foreignId.ToByteArray()[2],
                    CheckLeaseId = foreignLease,
                    CheckLeaseUntil = initialExpiry
                });
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

        try
        {
            Assert.Equal(1, await validator.RenewLeaseAsync(ownedLease, renewedExpiry, CancellationToken.None));
            await using (var renewed = await factory.CreateDbContextAsync())
            {
                Assert.Equal(renewedExpiry,
                    await renewed.Proxies.Where(proxy => proxy.Id == ownedId).Select(proxy => proxy.CheckLeaseUntil).SingleAsync());
                Assert.Equal(initialExpiry,
                    await renewed.Proxies.Where(proxy => proxy.Id == foreignId).Select(proxy => proxy.CheckLeaseUntil).SingleAsync());
            }

            Assert.Equal(1, await validator.ReleaseLeaseAsync(ownedLease, CancellationToken.None));
            await using var released = await factory.CreateDbContextAsync();
            var owned = await released.Proxies.AsNoTracking().SingleAsync(proxy => proxy.Id == ownedId);
            var foreign = await released.Proxies.AsNoTracking().SingleAsync(proxy => proxy.Id == foreignId);
            Assert.Null(owned.CheckLeaseId);
            Assert.Null(owned.CheckLeaseUntil);
            Assert.Equal(foreignLease, foreign.CheckLeaseId);
            Assert.Equal(initialExpiry, foreign.CheckLeaseUntil);
        }
        finally
        {
            await using var cleanup = await factory.CreateDbContextAsync();
            await cleanup.Proxies.Where(proxy => proxy.Id == ownedId || proxy.Id == foreignId).ExecuteDeleteAsync();
        }
    }

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
