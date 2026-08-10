using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>
/// Доказывает на настоящей PostgreSQL, что multi-query public response видит одну эпоху
/// даже при конкурентном validation/source update и retry-enabled DbContext factory.
/// </summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class PublicReadSnapshotIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PublicMultiQueryResponsesRemainInOneEpochDuringConcurrentMutations()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await WithSchemaAsync(connectionString, async (builder, plainFactory) =>
        {
            var now = DateTimeOffset.UtcNow;
            var firstId = Guid.Parse("10000000-0000-0000-0000-000000000001");
            await using (var seed = await plainFactory.CreateDbContextAsync())
            {
                seed.Proxies.AddRange(
                    Endpoint(firstId, "1.1.1.1", 100, now),
                    Endpoint(Guid.Parse("10000000-0000-0000-0000-000000000002"), "8.8.8.8", 200, now));
                await seed.SaveChangesAsync();
            }

            var interceptor = new MutateBeforeReadInterceptor("FROM \"Proxies\"", 2, async token =>
            {
                await using var update = await plainFactory.CreateDbContextAsync(token);
                await update.Proxies.Where(proxy => proxy.Id == firstId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(proxy => proxy.Status, ProxyStatus.Dead)
                        .SetProperty(proxy => proxy.FailedChecks, proxy => proxy.FailedChecks + 1)
                        .SetProperty(proxy => proxy.ConsecutiveFailedChecks, 1), token);
            });
            var retryFactory = RetryFactory(builder.ConnectionString, interceptor);
            var controller = new ProxiesController(
                retryFactory, Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));

            var action = await controller.Get(
                null, null, null, page: 1, pageSize: 1, cancellationToken: CancellationToken.None);
            var page = Assert.IsType<PagedResult<ProxyDto>>(Assert.IsType<OkObjectResult>(action.Result).Value);

            Assert.True(interceptor.MutationInvoked);
            Assert.Single(page.Items);
            Assert.Equal(2, page.Total);
            await using (var pageVerify = await plainFactory.CreateDbContextAsync())
                Assert.Equal(1, await pageVerify.Proxies.CountAsync(proxy => proxy.Status == ProxyStatus.Alive));
            await using (var reset = await plainFactory.CreateDbContextAsync())
                await reset.Proxies.ExecuteDeleteAsync();

            var proxyId = Guid.Parse("20000000-0000-0000-0000-000000000001");
            var sourceId = Guid.Parse("20000000-0000-0000-0000-000000000002");
            await using (var seed = await plainFactory.CreateDbContextAsync())
            {
                seed.Proxies.Add(Endpoint(proxyId, "9.9.9.9", 120, now));
                seed.Sources.Add(new ProxySource
                {
                    Id = sourceId,
                    Name = "snapshot-source",
                    Url = "https://example.com/snapshot-proxies.txt",
                    Enabled = true
                });
                await seed.SaveChangesAsync();
            }

            var statsInterceptor = new MutateBeforeReadInterceptor("FROM \"Sources\"", 1, async token =>
            {
                await using var update = await plainFactory.CreateDbContextAsync(token);
                await update.Proxies.Where(proxy => proxy.Id == proxyId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(proxy => proxy.Status, ProxyStatus.Dead)
                        .SetProperty(proxy => proxy.FailedChecks, proxy => proxy.FailedChecks + 1)
                        .SetProperty(proxy => proxy.ConsecutiveFailedChecks, 1), token);
                await update.Sources.Where(source => source.Id == sourceId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(source => source.Enabled, false), token);
            });
            var statsController = new StatsController(
                RetryFactory(builder.ConnectionString, statsInterceptor),
                Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));

            var statsAction = await statsController.Get(CancellationToken.None);
            var stats = Assert.IsType<StatsResponse>(Assert.IsType<OkObjectResult>(statsAction.Result).Value);

            Assert.True(statsInterceptor.MutationInvoked);
            Assert.Equal(1, stats.Alive);
            Assert.Equal(1, stats.Sources);
            await using var statsVerify = await plainFactory.CreateDbContextAsync();
            Assert.Equal(0, await statsVerify.Proxies.CountAsync(proxy => proxy.Status == ProxyStatus.Alive));
            Assert.Equal(0, await statsVerify.Sources.CountAsync(source => source.Enabled));
        });
    }

    private static async Task WithSchemaAsync(
        string baseConnectionString,
        Func<NpgsqlConnectionStringBuilder, TestDbFactory, Task> test)
    {
        var schema = $"proxyharbor_public_snapshot_{Guid.NewGuid():N}";
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
            await using (var migration = await factory.CreateDbContextAsync())
                await migration.Database.MigrateAsync();
            await test(builder, factory);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static TestDbFactory RetryFactory(string connectionString, DbCommandInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), null))
            .AddInterceptors(interceptor)
            .Options;
        return new TestDbFactory(options);
    }

    private static ProxyEndpoint Endpoint(Guid id, string host, int latencyMs, DateTimeOffset now) => new()
    {
        Id = id,
        Host = host,
        Port = 8080,
        Protocol = ProxyProtocol.Http,
        Status = ProxyStatus.Alive,
        FirstSeenAt = now.AddMinutes(-5),
        LastSeenAt = now,
        LastCheckedAt = now,
        NextCheckAt = now.AddMinutes(5),
        LatencyMs = latencyMs,
        SuccessfulChecks = 1
    };

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    /// <summary>Коммитит mutation строго перед заданным чтением тестируемого snapshot.</summary>
    private sealed class MutateBeforeReadInterceptor(
        string commandMarker,
        int readNumber,
        Func<CancellationToken, Task> mutate) : DbCommandInterceptor
    {
        private int _matchingReads;
        private int _mutationInvoked;
        internal bool MutationInvoked => Volatile.Read(ref _mutationInvoked) != 0;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(commandMarker, StringComparison.Ordinal) &&
                Interlocked.Increment(ref _matchingReads) == readNumber &&
                Interlocked.CompareExchange(ref _mutationInvoked, 1, 0) == 0)
                await mutate(cancellationToken);
            return result;
        }
    }
}
