using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyCollectorRefreshIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "PostgresIntegration")]
    public async Task RefreshMakesProgressPastLockedBatchesAndRetriesSlowWrites(bool lockFirstBatch)
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        const int count = 1_002;
        var schema = $"proxyharbor_refresh_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var factory = new TestDbFactory(new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options);
            var old = DateTimeOffset.UtcNow.AddHours(-2);
            await SeedAsync(factory, count, old);
            await using var blocker = new NpgsqlConnection(builder.ConnectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            if (lockFirstBatch)
            {
                await using var command = new NpgsqlCommand("""
                    SELECT "Id" FROM "Proxies" ORDER BY "Id" LIMIT 1000 FOR UPDATE
                    """, blocker, blockerTransaction);
                await using var reader = await command.ExecuteReaderAsync();
                var locked = 0;
                while (await reader.ReadAsync()) locked++;
                Assert.Equal(1_000, locked);
            }
            else
            {
                await blockerTransaction.CommitAsync();
                await using var command = new NpgsqlCommand("""
                    CREATE TABLE refresh_ledger (row_count integer NOT NULL);
                    CREATE FUNCTION refresh_delay() RETURNS trigger LANGUAGE plpgsql AS $$
                    DECLARE changed integer;
                    BEGIN
                        SELECT count(*) INTO changed FROM refreshed;
                        IF changed > 64 THEN PERFORM pg_sleep(3.2); END IF;
                        INSERT INTO refresh_ledger VALUES (changed);
                        RETURN NULL;
                    END $$;
                    CREATE TRIGGER refresh_delay AFTER UPDATE ON "Proxies"
                    REFERENCING NEW TABLE AS refreshed FOR EACH STATEMENT EXECUTE FUNCTION refresh_delay();
                    """, blocker);
                await command.ExecuteNonQueryAsync();
            }
            using var clients = new TestHttpClientFactory(new FeedHandler(count));
            using var collector = CreateCollector(factory, clients);
            var result = await collector.CollectAsync(CancellationToken.None, forceAllSources: true)
                .WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal("completed", result.Status);
            Assert.Equal(0, result.NewProxies);
            Assert.Equal(count, result.CandidatesFound);
            await using var verify = await factory.CreateDbContextAsync();
            var refreshed = await verify.Proxies.CountAsync(x => x.LastSeenAt > old.AddMinutes(1));
            if (lockFirstBatch)
            {
                Assert.Equal(2, refreshed);
                await blockerTransaction.CommitAsync();
                var retried = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);
                Assert.Equal("completed", retried.Status);
                Assert.Equal(count, await verify.Proxies.CountAsync(x => x.LastSeenAt > old.AddMinutes(1)));
            }
            else
            {
                Assert.Equal(count, refreshed);
                await using var ledger = new NpgsqlCommand(
                    "SELECT sum(row_count), max(row_count) FROM refresh_ledger", blocker);
                await using var reader = await ledger.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(count, reader.GetInt64(0));
                Assert.InRange(reader.GetInt32(1), 1, 64);
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OneRowTimeoutFailsTheAuditAndRollsBackItsRefresh()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schema = $"proxyharbor_refresh_failure_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var factory = new TestDbFactory(new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString).Options);
            var old = DateTimeOffset.UtcNow.AddHours(-2);
            await SeedAsync(factory, 1, old);
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using (var trigger = new NpgsqlCommand("""
                CREATE FUNCTION refresh_delay() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_sleep(3.2); RETURN NULL; END $$;
                CREATE TRIGGER refresh_delay AFTER UPDATE ON "Proxies"
                FOR EACH STATEMENT EXECUTE FUNCTION refresh_delay();
                """, connection))
                await trigger.ExecuteNonQueryAsync();
            using var clients = new TestHttpClientFactory(new FeedHandler(1));
            using var collector = CreateCollector(factory, clients);
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                collector.CollectAsync(CancellationToken.None, forceAllSources: true));
            Assert.Equal(PostgresErrorCodes.QueryCanceled, exception.SqlState);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal("failed", await verify.Runs.Select(x => x.Status).SingleAsync());
            Assert.True(await verify.Proxies.Select(x => x.LastSeenAt).SingleAsync() < old.AddMinutes(1));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ProxyCollector CreateCollector(TestDbFactory factory, TestHttpClientFactory clients) =>
        new(factory, clients, Options.Create(new CollectorOptions
        {
            SourceRetryCount = 0,
            LastSeenRefreshMinutes = 1
        }), NullLogger<ProxyCollector>.Instance);

    private static async Task SeedAsync(TestDbFactory factory, int count, DateTimeOffset old)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        db.Sources.Add(new ProxySource
        {
            Name = "Bounded refresh feed",
            Url = "https://8.8.8.8/refresh.txt",
            DefaultProtocol = ProxyProtocol.Http
        });
        db.Proxies.AddRange(Enumerable.Range(0, count).Select(i => new ProxyEndpoint
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{i + 1:x12}"),
            Host = "1.1.1.1",
            Port = 1000 + i,
            Protocol = ProxyProtocol.Http,
            FirstSeenAt = old,
            LastSeenAt = old
        }));
        await db.SaveChangesAsync();
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
        private readonly HttpClient _client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("sources", name);
            return _client;
        }
        public void Dispose() => _client.Dispose();
    }

    private sealed class FeedHandler(int count) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Join('\n', Enumerable.Range(0, count)
                    .Select(i => $"1.1.1.1:{1000 + i}")))
            });
    }
}
