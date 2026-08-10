using System.Data.Common;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Доказывает единый PostgreSQL snapshot для последовательно записываемых файлов backup.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupSnapshotConsistencyIntegrationTests
{
    private const string EncryptionKey = "consistent-snapshot-key-at-least-32-chars";

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ConcurrentCommitBetweenTableReadsIsExcludedFromWholeArchive()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_backup_snapshot_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-snapshot-{Guid.NewGuid():N}");
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var databaseOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            await using (var setup = new ProxyHarborDbContext(databaseOptions))
            {
                await setup.Database.MigrateAsync();
                setup.Proxies.Add(new ProxyEndpoint { Host = "8.8.8.8", Port = 8080 });
                setup.Sources.Add(new ProxySource
                {
                    Name = "Before snapshot",
                    Url = "https://8.8.8.8/before.txt",
                    DefaultProtocol = ProxyProtocol.Http
                });
                await setup.SaveChangesAsync();
            }

            var committedBetweenReads = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var interceptor = new AfterProxyReaderInterceptor(async token =>
            {
                await using var concurrent = new ProxyHarborDbContext(databaseOptions);
                concurrent.Sources.Add(new ProxySource
                {
                    Name = "After snapshot",
                    Url = "https://1.1.1.1/after.txt",
                    DefaultProtocol = ProxyProtocol.Socks5
                });
                await concurrent.SaveChangesAsync(token);
                committedBetweenReads.TrySetResult();
            });
            var snapshotOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
            var factory = new TestDbFactory(snapshotOptions);
            var backupOptions = new BackupOptions
            {
                Directory = directory,
                EncryptionKey = EncryptionKey
            };
            using var backup = new BackupService(
                factory,
                new UnusedHttpClientFactory(),
                Options.Create(backupOptions),
                Options.Create(new CollectorOptions()),
                new ConfigurationBuilder().Build(),
                NullLogger<BackupService>.Instance);
            Directory.CreateDirectory(directory);
            var encryptedPath = Path.Combine(directory, "consistent.phbackup.partial");
            var zipPath = Path.Combine(directory, "consistent.zip");

            await backup.CreateEncryptedSnapshotAsync(
                encryptedPath, Guid.NewGuid(), backupOptions, telegramConfigured: false, CancellationToken.None);
            await committedBetweenReads.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await BackupEncryption.DecryptAsync(encryptedPath, zipPath, EncryptionKey, CancellationToken.None);

            using var archive = ZipFile.OpenRead(zipPath);
            await using var sourceStream = BackupArchiveValidator.RequiredEntry(
                archive, "database/sources.json").Open();
            using var sourceDocument = await JsonDocument.ParseAsync(sourceStream);
            var archivedNames = sourceDocument.RootElement.EnumerateArray()
                .Select(source => source.GetProperty("name").GetString() ??
                    throw new InvalidDataException("Source name отсутствует в backup snapshot."))
                .ToArray();
            Assert.Equal(["Before snapshot"], archivedNames);

            // Вторая сессия действительно завершила commit до SELECT Sources, поэтому
            // отсутствие строки в архиве доказывает REPEATABLE READ, а не позднюю запись.
            await using var verify = new ProxyHarborDbContext(databaseOptions);
            Assert.Equal(2, await verify.Sources.CountAsync());
            Assert.True(await verify.Sources.AnyAsync(source => source.Name == "After snapshot"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Выполняет concurrent commit после фиксации snapshot первым SELECT.</summary>
    private sealed class AfterProxyReaderInterceptor(
        Func<CancellationToken, Task> afterReader) : DbCommandInterceptor
    {
        private int _triggered;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Proxies\"", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _triggered, 1) == 0)
                await afterReader(cancellationToken);
            return result;
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("HTTP не должен использоваться при создании snapshot.");
    }
}
