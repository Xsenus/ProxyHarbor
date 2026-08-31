using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет set-based flush access telemetry на настоящей PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyAccessMonitorIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FailedBulkFlushReturnsEveryIncrementForNextAttempt()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_access_retry_{Guid.NewGuid():N}";
        var workingBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var workingOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(workingBuilder.ConnectionString).Options;
            await using (var migrationDb = new ProxyHarborDbContext(workingOptions))
                await migrationDb.Database.MigrateAsync();
            var failingBuilder = new NpgsqlConnectionStringBuilder(workingBuilder.ConnectionString)
            {
                Host = "127.0.0.1",
                Port = 1,
                Timeout = 1,
                CommandTimeout = 1,
                Pooling = false
            };
            var failingOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(failingBuilder.ConnectionString).Options;
            var factory = new FailOnceDbFactory(failingOptions, workingOptions);
            var monitor = new ProxyAccessMonitor(factory, NullLogger<ProxyAccessMonitor>.Instance);
            var context = Context(1, contentLength: 512, proxyItems: 7);
            monitor.Record(context, "export", blocked: false);
            monitor.Record(context, "export", blocked: true);

            await monitor.FlushOnceAsync(CancellationToken.None);
            await using (var afterFailure = new ProxyHarborDbContext(workingOptions))
                Assert.Empty(await afterFailure.ProxyAccessBuckets.ToArrayAsync());

            await monitor.FlushOnceAsync(CancellationToken.None);
            await using var afterRetry = new ProxyHarborDbContext(workingOptions);
            var saved = Assert.Single(await afterRetry.ProxyAccessBuckets.ToArrayAsync());
            Assert.Equal(2, saved.Requests);
            Assert.Equal(1, saved.BlockedRequests);
            Assert.Equal(14, saved.ProxyItems);
            Assert.Equal(1_024, saved.BytesSent);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task BulkFlushInsertsAndAccumulatesManyIndependentBuckets()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_access_flush_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString).Options;
            var factory = new TestDbFactory(options);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var monitor = new ProxyAccessMonitor(factory, NullLogger<ProxyAccessMonitor>.Instance);
            long expectedBytes = 0;
            const int bucketCount = 250;
            for (var index = 0; index < bucketCount; index++)
            {
                var context = Context(index, contentLength: 100 + index, proxyItems: 3);
                monitor.Record(context, "catalog", blocked: false);
                monitor.Record(context, "catalog", blocked: true);
                expectedBytes += 2L * (100 + index);
            }

            await monitor.FlushOnceAsync(CancellationToken.None);
            await using (var inserted = await factory.CreateDbContextAsync())
            {
                Assert.Equal(bucketCount, await inserted.ProxyAccessBuckets.CountAsync());
                Assert.Equal(bucketCount * 2, await inserted.ProxyAccessBuckets.SumAsync(x => x.Requests));
                Assert.Equal(bucketCount, await inserted.ProxyAccessBuckets.SumAsync(x => x.BlockedRequests));
                Assert.Equal(bucketCount * 6L, await inserted.ProxyAccessBuckets.SumAsync(x => x.ProxyItems));
                Assert.Equal(expectedBytes, await inserted.ProxyAccessBuckets.SumAsync(x => x.BytesSent));
            }

            for (var index = 0; index < bucketCount; index++)
            {
                var context = Context(index, contentLength: 7, proxyItems: 2);
                monitor.Record(context, "catalog", blocked: false);
                expectedBytes += 7;
            }
            await monitor.FlushOnceAsync(CancellationToken.None);

            await using var accumulated = await factory.CreateDbContextAsync();
            Assert.Equal(bucketCount * 3, await accumulated.ProxyAccessBuckets.SumAsync(x => x.Requests));
            Assert.Equal(bucketCount, await accumulated.ProxyAccessBuckets.SumAsync(x => x.BlockedRequests));
            Assert.Equal(bucketCount * 8L, await accumulated.ProxyAccessBuckets.SumAsync(x => x.ProxyItems));
            Assert.Equal(expectedBytes, await accumulated.ProxyAccessBuckets.SumAsync(x => x.BytesSent));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static DefaultHttpContext Context(int index, long contentLength, int proxyItems)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(
            $"198.51.{index / 250}.{index % 250 + 1}");
        context.Response.ContentLength = contentLength;
        context.Items["ProxyHarbor.ProxyItems"] = proxyItems;
        return context;
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FailOnceDbFactory(
        DbContextOptions<ProxyHarborDbContext> failingOptions,
        DbContextOptions<ProxyHarborDbContext> workingOptions)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        private int attempts;

        public ProxyHarborDbContext CreateDbContext() =>
            new(Interlocked.Increment(ref attempts) == 1 ? failingOptions : workingOptions);

        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
