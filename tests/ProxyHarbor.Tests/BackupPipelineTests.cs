using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет diskless ZIP-to-PHB3 pipeline без зависимости от внешней PostgreSQL.</summary>
public sealed class BackupPipelineTests
{
    private const string EncryptionKey = "pipeline-test-encryption-key-32-chars";

    [Fact]
    public void OperationalLoggingBoundaryCannotEscapeIntoPipeline()
    {
        var providerFailure = new InvalidOperationException("Deterministic logging provider failure.");
        var calls = 0;

        var escaped = Record.Exception(() => OperationalLogBoundary.Write(() =>
        {
            calls++;
            throw providerFailure;
        }));

        Assert.Null(escaped);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PipeCompletionFailurePreservesPrimaryButFailsSuccessfulOperation()
    {
        var primaryFailure = new InvalidOperationException("Deterministic producer failure.");
        var secondaryFailure = new IOException("Deterministic writer completion failure.");

        var preservingFailure = await Record.ExceptionAsync(() =>
            BackupService.CompletePipePreservingPrimaryAsync(
                _ => ValueTask.FromException(secondaryFailure),
                primaryFailure,
                "writer"));

        Assert.Null(preservingFailure);
        Assert.Equal(
            "writer: IOException",
            primaryFailure.Data[BackupService.PipeCompletionFailureDataKey]);

        var standaloneFailure = await Assert.ThrowsAsync<IOException>(() =>
            BackupService.CompletePipePreservingPrimaryAsync(
                _ => ValueTask.FromException(secondaryFailure),
                primaryFailure: null,
                "reader"));
        Assert.Same(secondaryFailure, standaloneFailure);
    }

    [Fact]
    public async Task SnapshotIsEncryptedDirectlyAndContainsExpectedArchive()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-pipeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var databaseRoot = new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot();
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase($"backup-{Guid.NewGuid():N}", databaseRoot)
                // InMemory не поддерживает транзакции, но сохраняет консистентный store для этого unit-теста.
                // Полная repeatable-read семантика отдельно проверяется PostgreSQL integration-gate.
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Sources.Add(new ProxySource
                {
                    Name = "Pipeline source",
                    Url = "https://example.test/proxies.txt",
                    DefaultProtocol = ProxyProtocol.Http,
                    Enabled = true,
                    LastResultTruncated = true
                });
                await seed.SaveChangesAsync();
            }

            var encryptedPath = Path.Combine(directory, "snapshot.phbackup.partial");
            var decryptedPath = Path.Combine(directory, "snapshot.zip");
            var backupOptions = new BackupOptions
            {
                Directory = directory,
                EncryptionKey = EncryptionKey
            };
            using var service = new BackupService(
                factory,
                new UnusedHttpClientFactory(),
                Options.Create(backupOptions),
                Options.Create(new CollectorOptions()),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "example.test",
                    ["Security:AdminApiKey"] = "configured-but-never-exported"
                }).Build(),
                NullLogger<BackupService>.Instance);

            await service.CreateEncryptedSnapshotAsync(
                encryptedPath, Guid.NewGuid(), backupOptions, telegramConfigured: false, CancellationToken.None);

            Assert.True(File.Exists(encryptedPath));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.zip"));
            await BackupEncryption.DecryptAsync(encryptedPath, decryptedPath, EncryptionKey, CancellationToken.None);

            using var archive = ZipFile.OpenRead(decryptedPath);
            BackupArchiveValidator.Validate(archive);
            var names = archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(
                [
                    "database/access-block-rules.json",
                    "database/backup-runs.json",
                    "database/payment-configuration.json",
                    "database/payment-orders.json",
                    "database/proxies.json",
                    "database/proxy-access-buckets.json",
                    "database/roles.json",
                    "database/runs.json",
                    "database/sources.json",
                    "database/subscription-admin-actions.json",
                    "database/subscriptions.json",
                    "database/user-roles.json",
                    "database/users.json",
                    "database/validation-runs.json",
                    "manifest.json",
                    "settings/backup.json",
                    "settings/collector.json",
                    "settings/runtime.json"
                ],
                names);
            var sourcesEntry = Assert.Single(archive.Entries, entry => entry.FullName == "database/sources.json");
            await using var sourcesStream = sourcesEntry.Open();
            using var sources = await JsonDocument.ParseAsync(sourcesStream);
            Assert.Equal("Pipeline source", sources.RootElement[0].GetProperty("name").GetString());
            Assert.True(sources.RootElement[0].GetProperty("lastResultTruncated").GetBoolean());
            using var manifestStream = BackupArchiveValidator.RequiredEntry(archive, "manifest.json").Open();
            using var manifest = await JsonDocument.ParseAsync(manifestStream);
            Assert.Equal(6, manifest.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(1, manifest.RootElement.GetProperty("settingsSchemaVersion").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProducerFailureCancelsEncryptorAndPreservesOriginalException()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-pipeline-producer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new FailingDbFactory();
            var options = new BackupOptions { Directory = directory, EncryptionKey = EncryptionKey };
            using var service = CreateService(factory, options);
            var partialPath = Path.Combine(directory, "producer-failure.phbackup.partial");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateEncryptedSnapshotAsync(
                    partialPath, Guid.NewGuid(), options, telegramConfigured: false, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Same(factory.Failure, exception);
            Assert.False(File.Exists(partialPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EncryptorFailureCancelsBlockedProducerWithoutHanging()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-pipeline-encryptor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new CancellationAwareHangingDbFactory();
            var options = new BackupOptions { Directory = directory, EncryptionKey = EncryptionKey };
            using var service = CreateService(factory, options);

            // Каталог нельзя открыть как ciphertext-файл: encryptor падает сразу, пока
            // producer ожидает контекст БД и может завершиться только через linked cancellation.
            var exception = await Record.ExceptionAsync(() => service.CreateEncryptedSnapshotAsync(
                directory, Guid.NewGuid(), options, telegramConfigured: false, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.True(exception is UnauthorizedAccessException or IOException, exception?.ToString());
            await factory.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static BackupService CreateService(
        IDbContextFactory<ProxyHarborDbContext> factory,
        BackupOptions options) =>
        new(
            factory,
            new UnusedHttpClientFactory(),
            Options.Create(options),
            Options.Create(new CollectorOptions()),
            new ConfigurationBuilder().Build(),
            NullLogger<BackupService>.Instance);

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("HTTP не должен использоваться.");
    }

    /// <summary>Детерминированно ломает ZIP producer до первой строки БД.</summary>
    private sealed class FailingDbFactory : IDbContextFactory<ProxyHarborDbContext>
    {
        internal InvalidOperationException Failure { get; } = new("sentinel producer failure");

        public ProxyHarborDbContext CreateDbContext() => throw Failure;

        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<ProxyHarborDbContext>(Failure);
    }

    /// <summary>Доказывает, что сбой encryptor действительно отменяет зависший producer.</summary>
    private sealed class CancellationAwareHangingDbFactory : IDbContextFactory<ProxyHarborDbContext>
    {
        internal TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProxyHarborDbContext CreateDbContext() =>
            throw new InvalidOperationException("Синхронное создание контекста не ожидается.");

        public async Task<ProxyHarborDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Недостижимый код hanging DB factory.");
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
