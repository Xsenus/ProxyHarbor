using System.Collections.Concurrent;
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

/// <summary>Фиксирует единый PostgreSQL command для collection/backup части Prometheus snapshot.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class MetricsRunQueryIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task MetricsCollapseCollectionAndBackupLookupsIntoOneStatement()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_metrics_runs_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var commands = new MetricsCommandCounter();
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString, postgres =>
                    postgres.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), null))
                .AddInterceptors(commands)
                .Options;
            var factory = new TestDbFactory(options);
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();

            var successfulCollectionAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            var latestCollectionAt = successfulCollectionAt.AddMinutes(2);
            db.Runs.AddRange(
                new CollectionRun
                {
                    StartedAt = successfulCollectionAt.AddSeconds(-9),
                    FinishedAt = successfulCollectionAt,
                    Status = "completed",
                    SourcesProcessed = 1,
                    SourcesSucceeded = 1,
                    CandidatesFound = 99
                },
                new CollectionRun
                {
                    StartedAt = latestCollectionAt.AddSeconds(-4),
                    FinishedAt = latestCollectionAt,
                    Status = "failed",
                    SourcesProcessed = 1,
                    SourcesSucceeded = 1,
                    SourcesSkipped = 3,
                    SourcesTruncated = 1,
                    CandidatesFound = 7,
                    CandidateLimitReached = true,
                    Error = "expected integration failure"
                },
                new CollectionRun { StartedAt = latestCollectionAt.AddMinutes(1), Status = "running" });

            var successfulBackupAt = successfulCollectionAt.AddMinutes(1);
            db.BackupRuns.AddRange(
                new BackupRun
                {
                    StartedAt = successfulBackupAt.AddSeconds(-10),
                    FinishedAt = successfulBackupAt,
                    Status = "completed",
                    TelegramConfigured = true,
                    SentToTelegram = true,
                    SizeBytes = 12_345
                },
                new BackupRun
                {
                    StartedAt = latestCollectionAt.AddSeconds(-2),
                    FinishedAt = latestCollectionAt.AddSeconds(-1),
                    Status = "failed",
                    Error = "expected integration failure"
                },
                new BackupRun { StartedAt = latestCollectionAt.AddMinutes(1), Status = "running" });
            await db.SaveChangesAsync();

            commands.Reset();
            var metrics = await ReadMetricsAsync(factory);
            AssertSingleRunStatement(commands);
            Assert.Contains("proxyharbor_collection_runs_active 1", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_collection_success 0", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_collection_candidates 7", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_collection_sources_skipped 3", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_collection_sources_truncated 1", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_collection_candidate_limit_reached 1", metrics,
                StringComparison.Ordinal);
            Assert.Contains($"proxyharbor_last_collection_timestamp_seconds {latestCollectionAt.ToUnixTimeSeconds()}",
                metrics, StringComparison.Ordinal);
            Assert.Contains(
                $"proxyharbor_last_successful_collection_timestamp_seconds {successfulCollectionAt.ToUnixTimeSeconds()}",
                metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_collection_duration_seconds 4", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_backup_runs_active 1", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_backup_success 0", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_backup_telegram_configured 1", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_backup_sent_to_telegram 1", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_backup_size_bytes 12345", metrics, StringComparison.Ordinal);
            Assert.Contains($"proxyharbor_last_backup_timestamp_seconds {successfulBackupAt.ToUnixTimeSeconds()}",
                metrics, StringComparison.Ordinal);

            await db.Runs.ExecuteDeleteAsync();
            await db.BackupRuns.ExecuteDeleteAsync();
            commands.Reset();
            metrics = await ReadMetricsAsync(factory);
            AssertSingleRunStatement(commands);
            Assert.Contains("proxyharbor_collection_runs_active 0", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_collection_timestamp_seconds 0", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_backup_runs_active 0", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_last_backup_timestamp_seconds 0", metrics, StringComparison.Ordinal);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> ReadMetricsAsync(IDbContextFactory<ProxyHarborDbContext> factory)
    {
        var controller = new MetricsController(
            factory,
            Options.Create(new CollectorOptions()),
            Options.Create(new BackupOptions()),
            new ProbeControlHealth());
        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        return result.Content!;
    }

    private static void AssertSingleRunStatement(MetricsCommandCounter commands)
    {
        var collectionCommand = Assert.Single(commands.SelectSql, sql =>
            sql.Contains("FROM \"Runs\"", StringComparison.Ordinal));
        var backupCommand = Assert.Single(commands.SelectSql, sql =>
            sql.Contains("FROM \"BackupRuns\"", StringComparison.Ordinal));
        Assert.Equal(collectionCommand, backupCommand);
        Assert.Contains("LEFT JOIN LATERAL", collectionCommand, StringComparison.Ordinal);
        Assert.Contains("AS \"ActiveCollectionRuns\"", collectionCommand, StringComparison.Ordinal);
        Assert.Contains("AS \"ActiveBackupRuns\"", collectionCommand, StringComparison.Ordinal);
    }

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

    private sealed class MetricsCommandCounter : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> selectSql = new();
        internal string[] SelectSql => selectSql.ToArray();

        internal void Reset()
        {
            while (selectSql.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                selectSql.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
