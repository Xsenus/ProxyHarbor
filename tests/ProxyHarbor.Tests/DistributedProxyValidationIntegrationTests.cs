using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет ownership, возврат потерянной партии и завершение внешним узлом на PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class DistributedProxyValidationIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task NodesReceiveDisjointLeasesAndExpiredBatchIsReclaimed()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_distributed_validation_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                // Production enables retry-on-failure. The distributed completion owns
                // an explicit transaction and therefore must execute inside that strategy.
                .UseNpgsql(connection.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var firstNode = Node("first", batchSize: 6);
            var secondNode = Node("second", batchSize: 6);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.CheckerNodes.AddRange(firstNode, secondNode);
                seed.Proxies.AddRange(Enumerable.Range(1, 20).Select(index => new ProxyEndpoint
                {
                    Host = $"198.51.100.{index}",
                    Port = 8000 + index,
                    Protocol = ProxyProtocol.Http,
                    Status = index <= 4 ? ProxyStatus.Alive : index <= 12 ? ProxyStatus.Pending : ProxyStatus.Dead,
                    LastCheckedAt = index <= 4 || index > 12 ? DateTimeOffset.UtcNow : null,
                    LatencyMs = index <= 4 ? 50 + index : null,
                    SuccessfulChecks = index <= 4 ? 1 : 0,
                    FailedChecks = index > 12 ? 1 : 0,
                    ConsecutiveFailedChecks = index > 12 ? 1 : 0,
                    // NULL входит в отдельный первый index-range и не должен
                    // пересечься с явно просроченной частью очереди.
                    NextCheckAt = index == 1 ? null : DateTimeOffset.UtcNow.AddMinutes(-1)
                }));
                await seed.SaveChangesAsync();
            }

            var settings = new CollectorOptions { ProbeTimeoutSeconds = 5 };
            using var clients = new StubHttpClientFactory();
            using var origin = new OriginIpProvider(clients, Options.Create(settings), new ProbeControlHealth());
            using var validator = new ProxyValidator(factory,
                new ProxyProbeService(Options.Create(settings), origin), Options.Create(settings),
                NullLogger<ProxyValidator>.Instance);
            var dispatcher = new DistributedProxyValidationService(factory, validator, Options.Create(settings));

            var claims = await Task.WhenAll(
                dispatcher.ClaimAsync(firstNode.Id, CancellationToken.None),
                dispatcher.ClaimAsync(secondNode.Id, CancellationToken.None));
            Assert.All(claims, claim => Assert.NotNull(claim));
            Assert.Equal(6, claims[0]!.Items.Count);
            Assert.Equal(6, claims[1]!.Items.Count);
            Assert.Empty(claims[0]!.Items.Select(x => x.Id).Intersect(claims[1]!.Items.Select(x => x.Id)));
            await using (var priorityCheck = await factory.CreateDbContextAsync())
            {
                var aliveIds = await priorityCheck.Proxies.AsNoTracking()
                    .Where(x => x.Status == ProxyStatus.Alive).Select(x => x.Id).ToArrayAsync();
                var deadIds = await priorityCheck.Proxies.AsNoTracking()
                    .Where(x => x.Status == ProxyStatus.Dead).Select(x => x.Id).ToArrayAsync();
                var claimedIds = claims.SelectMany(claim => claim!.Items).Select(item => item.Id).ToHashSet();
                Assert.All(aliveIds, id => Assert.Contains(id, claimedIds));
                Assert.DoesNotContain(deadIds, id => claimedIds.Contains(id));
            }

            // Второй узел штатно завершает свою партию нейтральными результатами.
            var secondResult = new CheckerLeaseResultRequest(claims[1]!.Items.Select(item =>
                new CheckerProxyResult(item.Id, false, null, null, false, "control unavailable", true)).ToArray());
            var completed = await dispatcher.CompleteAsync(
                secondNode.Id, claims[1]!.LeaseId, secondResult, CancellationToken.None);
            Assert.Equal(6, completed.Deferred);

            // Первый VPS «пропадает». Имитируем истечение TTL и убеждаемся, что его
            // пакет получает второй узел, а незавершённый аудит явно помечается failed.
            await using (var expire = await factory.CreateDbContextAsync())
            {
                var past = DateTimeOffset.UtcNow.AddSeconds(-1);
                await expire.CheckerNodes.Where(x => x.Id == firstNode.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CurrentLeaseUntil, past));
                await expire.Proxies.Where(x => x.CheckLeaseId == claims[0]!.LeaseId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CheckLeaseUntil, past));
            }

            var reclaimed = await dispatcher.ClaimAsync(secondNode.Id, CancellationToken.None);
            Assert.NotNull(reclaimed);
            Assert.Equal(claims[0]!.Items.Select(x => x.Id).Order(), reclaimed!.Items.Select(x => x.Id).Order());

            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal("failed", await verify.ValidationRuns
                .Where(x => x.LeaseId == claims[0]!.LeaseId).Select(x => x.Status).SingleAsync());
            Assert.Equal("completed", await verify.ValidationRuns
                .Where(x => x.LeaseId == claims[1]!.LeaseId).Select(x => x.Status).SingleAsync());
            Assert.Equal(6, await verify.CheckerNodes.Where(x => x.Id == secondNode.Id)
                .Select(x => x.CompletedChecks).SingleAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ExpiredLeaseLocksProxyBeforeWaitingForValidationAudit()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var schema = $"proxyharbor_claim_lock_order_{suffix}";
        var applicationName = $"proxyharbor-claim-lock-order-{suffix}";
        var serviceConnection = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schema,
            ApplicationName = applicationName
        };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(serviceConnection.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var expiredLeaseId = Guid.NewGuid();
            var node = Node("expired", batchSize: 1);
            node.CurrentLeaseId = expiredLeaseId;
            node.CurrentLeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
            var proxy = new ProxyEndpoint
            {
                Host = "198.51.100.200",
                Port = 8200,
                Protocol = ProxyProtocol.Http,
                NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                CheckLeaseId = expiredLeaseId,
                CheckLeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var run = new ValidationRun
            {
                LeaseId = expiredLeaseId,
                CheckerNodeId = node.Id,
                Claimed = 1
            };
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.AddRange(node, proxy, run);
                await seed.SaveChangesAsync();
            }

            var settings = new CollectorOptions { ProbeTimeoutSeconds = 5 };
            using var clients = new StubHttpClientFactory();
            using var origin = new OriginIpProvider(clients, Options.Create(settings), new ProbeControlHealth());
            using var validator = new ProxyValidator(factory,
                new ProxyProbeService(Options.Create(settings), origin), Options.Create(settings),
                NullLogger<ProxyValidator>.Instance);
            var dispatcher = new DistributedProxyValidationService(factory, validator, Options.Create(settings));

            var blockerConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schema,
                ApplicationName = $"proxyharbor-claim-blocker-{suffix}"
            };
            await using var blocker = new NpgsqlConnection(blockerConnectionString.ConnectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var lockRun = new NpgsqlCommand(
                "SELECT \"Id\" FROM \"ValidationRuns\" WHERE \"Id\" = @id FOR UPDATE", blocker, blockerTransaction))
            {
                lockRun.Parameters.AddWithValue("id", run.Id);
                Assert.Equal(run.Id, await lockRun.ExecuteScalarAsync());
            }

            var claimTask = dispatcher.ClaimAsync(node.Id, CancellationToken.None);
            var waitingForAuditLock = false;
            for (var attempt = 0; attempt < 100 && !waitingForAuditLock; attempt++)
            {
                await using var activity = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM pg_stat_activity
                        WHERE application_name = @application_name
                          AND wait_event_type = 'Lock'
                          AND query LIKE '%ValidationRuns%')
                    """, admin);
                activity.Parameters.AddWithValue("application_name", applicationName);
                waitingForAuditLock = (bool)(await activity.ExecuteScalarAsync())!;
                if (!waitingForAuditLock) await Task.Delay(50);
            }

            var probeConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schema,
                ApplicationName = $"proxyharbor-claim-probe-{suffix}"
            };
            await using var probe = new NpgsqlConnection(probeConnectionString.ConnectionString);
            await probe.OpenAsync();
            await using var probeTransaction = await probe.BeginTransactionAsync();
            await using var probeProxy = new NpgsqlCommand(
                "SELECT \"Id\" FROM \"Proxies\" WHERE \"Id\" = @id FOR UPDATE SKIP LOCKED", probe, probeTransaction);
            probeProxy.Parameters.AddWithValue("id", proxy.Id);
            var unlockedProxyId = await probeProxy.ExecuteScalarAsync();
            await probeTransaction.RollbackAsync();

            await blockerTransaction.CommitAsync();
            var claim = await claimTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(waitingForAuditLock);
            Assert.Null(unlockedProxyId);
            Assert.NotNull(claim);
            Assert.Single(claim!.Items);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal("failed", await verify.ValidationRuns
                .Where(x => x.Id == run.Id).Select(x => x.Status).SingleAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static CheckerNode Node(string name, int batchSize) => new()
    {
        Name = name,
        Host = name == "first" ? "203.0.113.10" : "203.0.113.11",
        SshUsername = "root",
        TokenHash = SHA256.HashData(Guid.NewGuid().ToByteArray()),
        BatchSize = batchSize,
        DeploymentStatus = "starting"
    };

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(new StubHandler());
        public HttpClient CreateClient(string name) => client;
        public void Dispose() => client.Dispose();
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
