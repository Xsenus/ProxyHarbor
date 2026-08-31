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
    public async Task HeartbeatAndCompletionLockLeaseRowsInStableIdOrder()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var schema = $"proxyharbor_lease_lock_order_{suffix}";
        var applicationName = $"proxyharbor-lease-lock-order-{suffix}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
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
                .UseNpgsql(builder.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var leaseId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                // Физически добавляем higher первым, чтобы SQL обязан был полагаться
                // именно на ORDER BY Id, а не на случайный heap/input order.
                seed.Proxies.AddRange(
                    new ProxyEndpoint
                    {
                        Id = higherId,
                        Host = "198.51.100.102",
                        Port = 8102,
                        CheckLeaseId = leaseId,
                        CheckLeaseUntil = now.AddMinutes(5)
                    },
                    new ProxyEndpoint
                    {
                        Id = lowerId,
                        Host = "198.51.100.101",
                        Port = 8101,
                        CheckLeaseId = leaseId,
                        CheckLeaseUntil = now.AddMinutes(5)
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

            await AssertWaitsOnLowerBeforeLockingHigherAsync(
                builder.ConnectionString,
                admin,
                applicationName,
                lowerId,
                higherId,
                () => validator.RenewLeaseAsync(leaseId, now.AddMinutes(6), CancellationToken.None));

            var updates = new[]
            {
                ProxyCheckScheduler.Create(
                    new ProxyCheckResult(higherId, false, null, null, false, "control unavailable", true),
                    0, leaseId, now, settings),
                ProxyCheckScheduler.Create(
                    new ProxyCheckResult(lowerId, false, null, null, false, "control unavailable", true),
                    0, leaseId, now, settings)
            };
            await AssertWaitsOnLowerBeforeLockingHigherAsync(
                builder.ConnectionString,
                admin,
                applicationName,
                lowerId,
                higherId,
                async () =>
                {
                    var result = await validator.PersistResultsAsync(updates, CancellationToken.None);
                    return result.Deferred;
                });
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

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
            var lastCheckedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Proxies.Add(new ProxyEndpoint
                {
                    Id = proxyId,
                    Host = IPAddress.Loopback.ToString(),
                    Port = port,
                    Protocol = ProxyProtocol.Http,
                    Status = ProxyStatus.Alive,
                    LatencyMs = 120,
                    LastCheckedAt = lastCheckedAt,
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
                new ProxyProbeService(Options.Create(settings), origin, ConnectLoopbackFixtureAsync),
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
            Assert.Equal(lastCheckedAt.ToUnixTimeMilliseconds(), saved.LastCheckedAt?.ToUnixTimeMilliseconds());
            Assert.Equal(120, saved.LatencyMs);
            Assert.Null(saved.LastValidationAttemptAt);
            Assert.False(saved.LastValidationDeferred);
            Assert.Equal(ProxyStatus.Alive, saved.Status);
            Assert.Equal(7, saved.SuccessfulChecks);
            Assert.Equal(2, saved.FailedChecks);
            Assert.Equal(1, saved.ConsecutiveFailedChecks);
            var failedRun = await verify.ValidationRuns.AsNoTracking().SingleAsync();
            Assert.Equal("failed", failedRun.Status);
            Assert.Equal(1, failedRun.Claimed);
            Assert.Equal(0, failedRun.Checked);
            Assert.NotNull(failedRun.FinishedAt);
            Assert.Contains("canceled", failedRun.Error!, StringComparison.OrdinalIgnoreCase);

            // Повторяем ту же строку на гарантированно закрытом порту: сетевой Dead является
            // полноценным результатом и должен завершить следующий batch audit.
            using var unusedPortReservation = new TcpListener(IPAddress.Loopback, 0);
            unusedPortReservation.Start();
            var unusedPort = ((IPEndPoint)unusedPortReservation.LocalEndpoint).Port;
            unusedPortReservation.Stop();
            var staleRunId = Guid.NewGuid();
            var activeRunId = Guid.NewGuid();
            var activeLease = Guid.NewGuid();
            verify.ValidationRuns.AddRange(
                new ValidationRun
                {
                    Id = staleRunId,
                    LeaseId = Guid.NewGuid(),
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
                },
                new ValidationRun
                {
                    Id = activeRunId,
                    LeaseId = activeLease,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
                });
            verify.Proxies.Add(new ProxyEndpoint
            {
                Host = "203.0.113.200",
                Port = 65_000,
                CheckLeaseId = activeLease,
                CheckLeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5)
            });
            await verify.SaveChangesAsync();
            await verify.Proxies.Where(proxy => proxy.Id == proxyId).ExecuteUpdateAsync(setters => setters
                .SetProperty(proxy => proxy.Port, unusedPort)
                .SetProperty(proxy => proxy.NextCheckAt, (DateTimeOffset?)null));

            Assert.Equal((1, 0, 0), await validator.ValidateBatchAsync(CancellationToken.None));
            var completedRun = await verify.ValidationRuns.AsNoTracking()
                .Where(run => run.Status == "completed").SingleAsync();
            Assert.Equal(1, completedRun.Claimed);
            Assert.Equal(1, completedRun.Checked);
            Assert.Equal(0, completedRun.Alive);
            Assert.Equal(0, completedRun.Deferred);
            Assert.NotNull(completedRun.FinishedAt);
            Assert.Equal("failed", await verify.ValidationRuns.Where(run => run.Id == staleRunId)
                .Select(run => run.Status).SingleAsync());
            Assert.Equal("running", await verify.ValidationRuns.Where(run => run.Id == activeRunId)
                .Select(run => run.Status).SingleAsync());
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
    public async Task PartialLeaseOwnershipPersistsOwnedResultButFailsClosed()
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
        var expectedLease = Guid.NewGuid();
        var foreignLease = Guid.NewGuid();
        var checkedAt = DateTimeOffset.UtcNow;
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Proxies.AddRange(
                new ProxyEndpoint
                {
                    Id = ownedId,
                    Host = $"14.36.{ownedId.ToByteArray()[0]}.{ownedId.ToByteArray()[1]}",
                    Port = 30_000 + ownedId.ToByteArray()[2],
                    FirstSeenAt = checkedAt.AddMinutes(-1),
                    LastSeenAt = checkedAt.AddMinutes(-1),
                    CheckLeaseId = expectedLease,
                    CheckLeaseUntil = checkedAt.AddMinutes(5)
                },
                new ProxyEndpoint
                {
                    Id = foreignId,
                    Host = $"15.37.{foreignId.ToByteArray()[0]}.{foreignId.ToByteArray()[1]}",
                    Port = 30_000 + foreignId.ToByteArray()[2],
                    FirstSeenAt = checkedAt.AddMinutes(-1),
                    LastSeenAt = checkedAt.AddMinutes(-1),
                    CheckLeaseId = foreignLease,
                    CheckLeaseUntil = checkedAt.AddMinutes(5)
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
        var updates = new[]
        {
            ProxyCheckScheduler.Create(
                new ProxyCheckResult(ownedId, true, 50, "8.8.8.8", true, null),
                0, expectedLease, checkedAt, settings),
            ProxyCheckScheduler.Create(
                new ProxyCheckResult(foreignId, true, 60, "8.8.4.4", true, null),
                0, expectedLease, checkedAt, settings)
        };

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => validator.PersistResultsAsync(updates, CancellationToken.None));
            Assert.Contains("сохранено 1 из 2", exception.Message, StringComparison.Ordinal);

            await using var verify = await factory.CreateDbContextAsync();
            var owned = await verify.Proxies.AsNoTracking().SingleAsync(proxy => proxy.Id == ownedId);
            var foreign = await verify.Proxies.AsNoTracking().SingleAsync(proxy => proxy.Id == foreignId);
            Assert.Equal(ProxyStatus.Alive, owned.Status);
            Assert.Equal(1, owned.SuccessfulChecks);
            Assert.Null(owned.CheckLeaseId);
            Assert.Equal(ProxyStatus.Pending, foreign.Status);
            Assert.Equal(0, foreign.SuccessfulChecks);
            Assert.Equal(foreignLease, foreign.CheckLeaseId);
        }
        finally
        {
            await using var cleanup = await factory.CreateDbContextAsync();
            await cleanup.Proxies
                .Where(proxy => proxy.Id == ownedId || proxy.Id == foreignId)
                .ExecuteDeleteAsync();
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
            FirstSeenAt = checkedAt.AddDays(-1),
            LastSeenAt = checkedAt,
            LastCheckedAt = checkedAt,
            NextCheckAt = checkedAt,
            CheckLeaseId = leaseId,
            CheckLeaseUntil = checkedAt.AddMinutes(5),
            SuccessfulChecks = 5,
            FailedChecks = 3,
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
            Assert.Equal(checkedAt.AddMinutes(1), saved.LastValidationAttemptAt);
            Assert.True(saved.LastValidationDeferred);
            Assert.Equal(5, saved.SuccessfulChecks);
            Assert.Equal(3, saved.FailedChecks);
            Assert.Equal(3, saved.ConsecutiveFailedChecks);
            Assert.Null(saved.CheckLeaseId);
            Assert.Null(saved.CheckLeaseUntil);
            Assert.Equal(checkedAt.AddMinutes(2), saved.NextCheckAt);
            Assert.Equal("control unavailable", saved.LastError);

            // Следующая полноценная проверка должна заменить Deferred telemetry и снова
            // сделать LastCheckedAt временем объективного результата.
            var aliveLease = Guid.NewGuid();
            await using (var owner = await factory.CreateDbContextAsync())
                await owner.Proxies.Where(x => x.Id == proxyId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CheckLeaseId, aliveLease)
                    .SetProperty(x => x.CheckLeaseUntil, checkedAt.AddMinutes(6)));
            var alive = ProxyCheckScheduler.Create(
                new ProxyCheckResult(proxyId, true, 88, "1.1.1.1", true, null),
                saved.ConsecutiveFailedChecks,
                aliveLease,
                checkedAt.AddMinutes(2),
                settings);

            Assert.Equal((1, 1, 0), await validator.PersistResultsAsync([alive], CancellationToken.None));
            await using var afterAlive = await factory.CreateDbContextAsync();
            var completed = await afterAlive.Proxies.AsNoTracking().SingleAsync(x => x.Id == proxyId);
            Assert.Equal(checkedAt.AddMinutes(2), completed.LastCheckedAt);
            Assert.Equal(checkedAt.AddMinutes(2), completed.LastValidationAttemptAt);
            Assert.False(completed.LastValidationDeferred);
            Assert.Equal(ProxyStatus.Alive, completed.Status);
            Assert.Equal(88, completed.LatencyMs);
            Assert.Equal(checkedAt.AddMinutes(2), completed.FirstAliveAt);
            Assert.Equal(checkedAt.AddMinutes(2), completed.LastAliveAt);
            Assert.Equal(checkedAt.AddMinutes(2), completed.CurrentAliveSince);
        }
        finally
        {
            await using var cleanup = await factory.CreateDbContextAsync();
            await cleanup.Proxies.Where(x => x.Id == proxyId).ExecuteDeleteAsync();
        }
    }

    private static async Task AssertWaitsOnLowerBeforeLockingHigherAsync(
        string connectionString,
        NpgsqlConnection admin,
        string applicationName,
        Guid lowerId,
        Guid higherId,
        Func<Task<int>> operation)
    {
        await using var blocker = new NpgsqlConnection(connectionString);
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockLower = new NpgsqlCommand(
            "SELECT \"Id\" FROM \"Proxies\" WHERE \"Id\" = @id FOR UPDATE", blocker, blockerTransaction))
        {
            lockLower.Parameters.AddWithValue("id", lowerId);
            Assert.Equal(lowerId, await lockLower.ExecuteScalarAsync());
        }

        var operationTask = operation();
        var waitingForLower = false;
        for (var attempt = 0; attempt < 100 && !waitingForLower; attempt++)
        {
            await using var activity = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @application_name
                      AND wait_event_type = 'Lock'
                      AND query LIKE '%Proxies%')
                """, admin);
            activity.Parameters.AddWithValue("application_name", applicationName);
            waitingForLower = (bool)(await activity.ExecuteScalarAsync())!;
            if (!waitingForLower) await Task.Delay(50);
        }

        var probeBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"{applicationName}-probe"
        };
        await using var probe = new NpgsqlConnection(probeBuilder.ConnectionString);
        await probe.OpenAsync();
        await using var probeTransaction = await probe.BeginTransactionAsync();
        await using var probeHigher = new NpgsqlCommand(
            "SELECT \"Id\" FROM \"Proxies\" WHERE \"Id\" = @id FOR UPDATE SKIP LOCKED", probe, probeTransaction);
        probeHigher.Parameters.AddWithValue("id", higherId);
        var unlockedHigherId = await probeHigher.ExecuteScalarAsync();
        await probeTransaction.RollbackAsync();

        await blockerTransaction.CommitAsync();
        var affected = await operationTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(waitingForLower);
        Assert.Equal(higherId, unlockedHigherId);
        Assert.Equal(2, affected);
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

    /// <summary>
    /// Этот тест проверяет cancellation/lease lifecycle на локальном listener, а не
    /// production SSRF-policy; явная подмена существует только во friend test assembly.
    /// </summary>
    private static async Task<TcpClient> ConnectLoopbackFixtureAsync(
        string host,
        int port,
        CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
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
