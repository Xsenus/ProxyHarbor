using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует операторский snapshot и точную семантику доступной validation-очереди.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class AdminDiagnosticsIntegrationTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DiagnosticsExcludeActiveLeaseFromDueBacklogAndEta()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_diagnostics_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(options);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                var now = DateTimeOffset.UtcNow;
                seed.Proxies.AddRange(
                    new ProxyEndpoint
                    {
                        Host = "1.1.1.1",
                        Port = 8080,
                        FirstSeenAt = now.AddDays(-5),
                        LastSeenAt = now.AddDays(-4)
                    },
                    new ProxyEndpoint
                    {
                        Host = "8.8.8.8",
                        Port = 8080,
                        CheckLeaseId = Guid.NewGuid(),
                        CheckLeaseUntil = now.AddMinutes(1)
                    },
                    new ProxyEndpoint
                    {
                        Host = "9.9.9.9",
                        Port = 8080,
                        NextCheckAt = now.AddMinutes(1)
                    });
                seed.ValidationRuns.Add(new ValidationRun
                {
                    LeaseId = Guid.NewGuid(),
                    StartedAt = now.AddSeconds(-11),
                    FinishedAt = now.AddSeconds(-1),
                    Claimed = 10,
                    Checked = 10,
                    Status = "completed"
                });
                seed.Runs.Add(new CollectionRun
                {
                    StartedAt = now.AddMinutes(-2),
                    FinishedAt = now.AddMinutes(-1),
                    Status = "completed"
                });
                seed.BackupRuns.Add(new BackupRun
                {
                    StartedAt = now.AddMinutes(-2),
                    FinishedAt = now.AddMinutes(-1),
                    Status = "completed"
                });
                await seed.SaveChangesAsync();
            }

            var mutation = new MutateBeforeReadInterceptor("FROM \"ValidationRuns\"", async token =>
            {
                await using var update = await factory.CreateDbContextAsync(token);
                await update.Proxies.ExecuteDeleteAsync(token);
                update.ValidationRuns.Add(new ValidationRun
                {
                    LeaseId = Guid.NewGuid(),
                    StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                    FinishedAt = DateTimeOffset.UtcNow,
                    Claimed = 20,
                    Checked = 20,
                    Status = "completed"
                });
                await update.SaveChangesAsync(token);
            });
            var diagnosticsFactory = RetryFactory(builder.ConnectionString, mutation);
            var collectorOptions = Options.Create(new CollectorOptions
            {
                ValidationConcurrency = 10,
                ValidationBatchSize = 20
            });
            using var proxySnapshotCache = new ProxyMetricsSnapshotCache(
                diagnosticsFactory,
                collectorOptions,
                NullLogger<ProxyMetricsSnapshotCache>.Instance,
                TimeProvider.System);
            using var vpnSnapshotCache = new VpnMetricsSnapshotCache(
                diagnosticsFactory,
                NullLogger<VpnMetricsSnapshotCache>.Instance,
                TimeProvider.System);
            var controller = new AdminController(
                diagnosticsFactory,
                null!,
                null!,
                null!,
                null!,
                Options.Create(new BackupOptions()),
                collectorOptions,
                null,
                null,
                proxySnapshotCache,
                vpnSnapshotCache);

            var action = await controller.Diagnostics(CancellationToken.None);
            Assert.True(mutation.MutationInvoked);
            var result = Assert.IsType<OkObjectResult>(action.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(
                result.Value, WebJsonOptions));
            var root = json.RootElement;
            var queue = root.GetProperty("validationQueue");
            Assert.Equal(3, queue.GetProperty("total").GetInt32());
            Assert.Equal(1, queue.GetProperty("due").GetInt32());
            Assert.Equal(1, queue.GetProperty("leased").GetInt32());
            Assert.Equal(1, queue.GetProperty("scheduled").GetInt32());
            Assert.Equal(1, queue.GetProperty("staleUnseen").GetInt32());
            Assert.Equal(10, queue.GetProperty("attemptsLastFiveMinutes").GetInt32());
            Assert.Equal(1, queue.GetProperty("estimatedDrainSeconds").GetInt64());
            Assert.Equal(10, queue.GetProperty("concurrencyLimit").GetInt32());
            Assert.Equal(20, queue.GetProperty("batchSize").GetInt32());
            Assert.True(root.GetProperty("databaseBytes").GetInt64() > 0);
            Assert.Single(root.GetProperty("recentRuns").EnumerateArray());
            Assert.Single(root.GetProperty("recentValidationRuns").EnumerateArray());
            Assert.Single(root.GetProperty("recentBackups").EnumerateArray());

            var secondAction = await controller.Diagnostics(CancellationToken.None);
            Assert.IsType<OkObjectResult>(secondAction.Result);
            Assert.Equal(1, proxySnapshotCache.DatabaseReads);
            Assert.Equal(1, vpnSnapshotCache.DatabaseReads);

            // Concurrent mutation действительно закоммичена, но уже открытый diagnostics
            // snapshot обязан показать целиком предшествующую эпоху всех таблиц.
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(0, await verify.Proxies.CountAsync());
            Assert.Equal(2, await verify.ValidationRuns.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
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

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    /// <summary>Коммитит изменение непосредственно перед первым чтением validation history.</summary>
    private sealed class MutateBeforeReadInterceptor(
        string commandMarker,
        Func<CancellationToken, Task> mutate) : DbCommandInterceptor
    {
        private int _mutationInvoked;
        internal bool MutationInvoked => Volatile.Read(ref _mutationInvoked) != 0;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(commandMarker, StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref _mutationInvoked, 1, 0) == 0)
                await mutate(cancellationToken);
            return result;
        }
    }
}
