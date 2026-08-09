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
            var names = archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(
                [
                    "database/backup-runs.json",
                    "database/proxies.json",
                    "database/runs.json",
                    "database/sources.json",
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
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
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
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("HTTP не должен использоваться.");
    }
}
