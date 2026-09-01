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
                .UseNpgsql(builder.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
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
                .UseNpgsql(builder.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
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
                SourceRetryCount = 0,
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

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task AutomaticRunUsesConditionalGetAndForcedRunRequiresFreshBody()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_vpn_conditional_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
                .Options;
            var factory = new TestDbFactory(dbOptions);
            Guid sourceId;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                var source = new VpnSource
                {
                    Name = "Conditional VPN feed",
                    Provider = "Integration test",
                    Url = "https://8.8.8.8/conditional-vpn.txt",
                    DefaultProtocol = VpnProtocol.Vless,
                    License = "MIT"
                };
                seed.VpnSources.Add(source);
                await seed.SaveChangesAsync();
                sourceId = source.Id;
            }

            var handler = new ConditionalVpnHandler();
            using var clients = new TestHttpClientFactory(handler);
            var service = new VpnCatalogService(factory, clients, Options.Create(new CollectorOptions
            {
                SourceConcurrency = 1,
                SourceTimeoutSeconds = 5,
                LastSeenRefreshMinutes = 360
            }), NullLogger<VpnCatalogService>.Instance);

            var first = await service.CollectAsync();
            DateTimeOffset firstContentFetchedAt;
            Guid endpointId;
            await using (var verify = await factory.CreateDbContextAsync())
            {
                var source = await verify.VpnSources.AsNoTracking().SingleAsync();
                endpointId = (await verify.VpnEndpoints.AsNoTracking().SingleAsync()).Id;
                firstContentFetchedAt = Assert.IsType<DateTimeOffset>(source.LastContentFetchedAt);
                Assert.Equal("\"vpn-v1\"", source.HttpETag);
                Assert.Equal(1, source.LastItemCount);
            }
            var versionsBefore304 = await ReadCatalogVersionsAsync(
                builder.ConnectionString, endpointId, sourceId);

            var notModified = await service.CollectAsync();
            var versionsAfter304 = await ReadCatalogVersionsAsync(
                builder.ConnectionString, endpointId, sourceId);

            Assert.Equal(1, first.ContentFetched);
            Assert.Equal(0, first.NotModified);
            Assert.Equal(0, notModified.ContentFetched);
            Assert.Equal(1, notModified.NotModified);
            Assert.Equal(1, notModified.Candidates);
            Assert.Equal(versionsBefore304, versionsAfter304);
            Assert.True(handler.Requests[1].IfNoneMatch);
            Assert.True(handler.Requests[1].IfModifiedSince);
            await using (var verify = await factory.CreateDbContextAsync())
            {
                var source = await verify.VpnSources.AsNoTracking().SingleAsync();
                Assert.Equal(firstContentFetchedAt, source.LastContentFetchedAt);
                Assert.True(source.LastFetchedAt > firstContentFetchedAt);
                Assert.Equal(1, source.LastItemCount);
            }

            var forced = await service.CollectAsync(forceAllSources: true);

            Assert.Equal(1, forced.ContentFetched);
            Assert.Equal(0, forced.NotModified);
            Assert.False(handler.Requests[2].IfNoneMatch);
            Assert.False(handler.Requests[2].IfModifiedSince);
            await using (var verify = await factory.CreateDbContextAsync())
                Assert.True((await verify.VpnSources.AsNoTracking().SingleAsync()).LastContentFetchedAt > firstContentFetchedAt);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ValidationQueueSelectsOnlyDueRowsInStableOrder()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_vpn_queue_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString).Options;
            var now = DateTimeOffset.UtcNow;
            var oldestDue = Endpoint("198.51.100.10", now.AddMinutes(-10));
            var newestDue = Endpoint("198.51.100.11", now.AddMinutes(-2));
            var firstNeverScheduled = Endpoint("198.51.100.12", null);
            firstNeverScheduled.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var secondNeverScheduled = Endpoint("198.51.100.14", null);
            secondNeverScheduled.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var future = Endpoint("198.51.100.13", now.AddMinutes(2));
            await using (var seed = new ProxyHarborDbContext(dbOptions))
            {
                await seed.Database.MigrateAsync();
                // Обратный insert-order доказывает, что NULL fallback использует
                // явный Id tie-break, а не случайный heap-order PostgreSQL.
                seed.VpnEndpoints.AddRange(
                    newestDue, future, secondNeverScheduled, firstNeverScheduled, oldestDue);
                await seed.SaveChangesAsync();
            }

            await using var queueDb = new ProxyHarborDbContext(dbOptions);
            var selected = await VpnValidationQueue.SelectAsync(
                queueDb, 4, now, CancellationToken.None);

            Assert.Equal(
                [oldestDue.Id, newestDue.Id, firstNeverScheduled.Id, secondNeverScheduled.Id],
                selected.Select(endpoint => endpoint.Id));
            Assert.DoesNotContain(selected, endpoint => endpoint.Id == future.Id);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ValidationBulkUpdatePreservesCatalogFieldsAndAppliesCountersOnce()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_vpn_validation_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
                .Options;
            var factory = new TestDbFactory(dbOptions);
            var first = new VpnEndpoint
            {
                Host = "8.8.8.8",
                Port = 443,
                Protocol = VpnProtocol.Trojan,
                ConnectionUri = "trojan://public@8.8.8.8:443",
                CountryCode = "US",
                Status = VpnEndpointStatus.Unreachable,
                SuccessfulChecks = 4,
                FailedChecks = 2,
                FirstSeenAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                LastSeenAt = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero)
            };
            var second = new VpnEndpoint
            {
                Host = "1.1.1.1",
                Port = 8443,
                Protocol = VpnProtocol.Vless,
                Status = VpnEndpointStatus.Reachable,
                SuccessfulChecks = 8,
                FailedChecks = 1,
                FirstSeenAt = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
                LastSeenAt = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero)
            };
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                seed.VpnEndpoints.AddRange(first, second);
                await seed.SaveChangesAsync();
            }

            using var clients = new TestHttpClientFactory(new DelegateHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)));
            var service = new VpnCatalogService(
                factory,
                clients,
                Options.Create(new CollectorOptions()),
                NullLogger<VpnCatalogService>.Instance);
            var checkedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
            var persisted = await service.PersistValidationResultsAsync(
            [
                new VpnValidationUpdate(
                    first.Id,
                    VpnEndpointStatus.Reachable,
                    42,
                    null,
                    checkedAt,
                    checkedAt.AddMinutes(5)),
                new VpnValidationUpdate(
                    second.Id,
                    VpnEndpointStatus.Unreachable,
                    null,
                    new string('x', 600),
                    checkedAt,
                    checkedAt.AddMinutes(15))
            ]);

            Assert.Equal(2, persisted);
            await using var verify = await factory.CreateDbContextAsync();
            var endpoints = await verify.VpnEndpoints.AsNoTracking()
                .OrderBy(x => x.Host).ToArrayAsync();
            var failed = endpoints[0];
            var alive = endpoints[1];
            Assert.Equal(VpnEndpointStatus.Unreachable, failed.Status);
            Assert.Null(failed.LatencyMs);
            Assert.Equal(8, failed.SuccessfulChecks);
            Assert.Equal(2, failed.FailedChecks);
            Assert.Equal(500, failed.LastError?.Length);
            Assert.Equal(checkedAt.AddMinutes(15), failed.NextCheckAt);

            Assert.Equal(VpnEndpointStatus.Reachable, alive.Status);
            Assert.Equal(42, alive.LatencyMs);
            Assert.Equal(5, alive.SuccessfulChecks);
            Assert.Equal(2, alive.FailedChecks);
            Assert.Null(alive.LastError);
            Assert.Equal(checkedAt, alive.LastCheckedAt);
            Assert.Equal(checkedAt.AddMinutes(5), alive.NextCheckAt);
            Assert.Equal("trojan://public@8.8.8.8:443", alive.ConnectionUri);
            Assert.Equal("US", alive.CountryCode);
            Assert.Equal(new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero), alive.LastSeenAt);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ValidationWriteWaitsForSharedVpnMutationGate()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_vpn_mutation_gate_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
                .Options;
            var factory = new TestDbFactory(dbOptions);
            var endpoint = new VpnEndpoint
            {
                Host = "9.9.9.9",
                Port = 443,
                Protocol = VpnProtocol.Trojan,
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            };
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                seed.VpnEndpoints.Add(endpoint);
                await seed.SaveChangesAsync();
            }

            using var clients = new TestHttpClientFactory(new DelegateHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)));
            var service = new VpnCatalogService(
                factory,
                clients,
                Options.Create(new CollectorOptions()),
                NullLogger<VpnCatalogService>.Instance);

            await using var blocker = new NpgsqlConnection(builder.ConnectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await PostgresAdvisoryLock.AcquireTransactionAsync(
                blocker,
                blockerTransaction,
                PostgresAdvisoryLock.VpnMutationKey,
                CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var persistence = service.PersistValidationResultsAsync(
            [
                new VpnValidationUpdate(
                    endpoint.Id,
                    VpnEndpointStatus.Reachable,
                    15,
                    null,
                    now,
                    now.AddMinutes(5))
            ]);
            await Task.Delay(250);
            var waitedForGate = !persistence.IsCompleted;
            await blockerTransaction.CommitAsync();

            Assert.True(waitedForGate);
            Assert.Equal(1, await persistence.WaitAsync(TimeSpan.FromSeconds(10)));
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

    private static VpnEndpoint Endpoint(string host, DateTimeOffset? nextCheckAt) => new()
    {
        Host = host,
        Port = 443,
        Protocol = VpnProtocol.Trojan,
        NextCheckAt = nextCheckAt,
        LastCheckedAt = nextCheckAt,
        FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-1),
        LastSeenAt = DateTimeOffset.UtcNow
    };

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

    private sealed class ConditionalVpnHandler : HttpMessageHandler
    {
        private static readonly DateTimeOffset LastModified =
            new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        internal List<(bool IfNoneMatch, bool IfModifiedSince)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var conditional = request.Headers.IfNoneMatch.Count > 0 || request.Headers.IfModifiedSince is not null;
            Requests.Add((request.Headers.IfNoneMatch.Count > 0, request.Headers.IfModifiedSince is not null));
            if (conditional)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("vless://conditional@8.8.4.4:443?type=tcp#conditional")
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"vpn-v1\"");
            response.Content.Headers.LastModified = LastModified;
            return Task.FromResult(response);
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
