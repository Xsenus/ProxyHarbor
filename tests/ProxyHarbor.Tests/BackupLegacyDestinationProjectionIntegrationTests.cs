using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupLegacyDestinationProjectionIntegrationTests
{
    [Fact]
    public async Task UnreadableLegacyConfigurationOnlyBlocksEnabledRouting()
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        var disabled = new BackupLegacyDestinationProjector(
            new ThrowingDbFactory(),
            new ThrowingConfigurationStore(),
            dataProtection,
            Options.Create(new BackupRoutingOptions { Enabled = false }),
            CreateRegistry(),
            NullLogger<BackupLegacyDestinationProjector>.Instance);
        await disabled.ProjectAsync();

        var enabled = new BackupLegacyDestinationProjector(
            new ThrowingDbFactory(),
            new ThrowingConfigurationStore(),
            dataProtection,
            Options.Create(new BackupRoutingOptions { Enabled = true }),
            CreateRegistry(),
            NullLogger<BackupLegacyDestinationProjector>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => enabled.ProjectAsync());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task UpgradeTwiceIsIdempotentAndRoutingOffPreservesLegacyConfiguration()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_backup_projection_{Guid.NewGuid():N}";
        var keyDirectory = Path.Combine(Path.GetTempPath(), $"proxyharbor-projection-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyDirectory);
        var connectionBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(
                    connectionBuilder.ConnectionString,
                    postgres => postgres.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), null))
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = new ProxyHarborDbContext(dbOptions))
                await DatabaseSeeder.MigrateSchemaAsync(migrationDb, CancellationToken.None);

            var deployOptions = Options.Create(new BackupOptions
            {
                Directory = Path.GetTempPath(),
                EncryptionKey = new string('k', BackupOptions.MinimumEncryptionKeyLength)
            });
            var dataProtection = DataProtectionProvider.Create(keyDirectory);
            var store = new BackupConfigurationStore(factory, deployOptions, dataProtection);
            const string accessKey = "projection-access-key";
            const string secretKey = "projection-secret-key";
            const string botToken = "123456789:projection-telegram-token";
            const string chatId = "-1001234567890";
            await store.SaveAsync(new BackupOptions
            {
                Enabled = true,
                IntervalHours = 12,
                RetentionDays = 14,
                HistoryRetentionDays = 180,
                EncryptionKey = deployOptions.Value.EncryptionKey,
                Directory = deployOptions.Value.Directory,
                TelegramBotToken = botToken,
                TelegramChatId = chatId,
                MaxTelegramFileSizeMb = 40,
                SendToObjectStorage = true,
                ObjectStorageEndpoint = "https://storage.example.test",
                ObjectStorageRegion = "eu-central-1",
                ObjectStorageBucket = "proxyharbor-backups",
                ObjectStoragePrefix = "production/backups",
                ObjectStorageAccessKey = accessKey,
                ObjectStorageSecretKey = secretKey
            });

            string legacySettings;
            string legacySecrets;
            DateTimeOffset legacyUpdatedAt;
            await using (var before = new ProxyHarborDbContext(dbOptions))
            {
                var configuration = await before.BackupConfigurations.AsNoTracking().SingleAsync();
                legacySettings = configuration.SettingsJson;
                legacySecrets = configuration.ProtectedSecrets;
                legacyUpdatedAt = configuration.UpdatedAt;
                using var settings = JsonDocument.Parse(legacySettings);
                Assert.Equal(2, settings.RootElement.GetProperty("schemaVersion").GetInt32());
            }

            var projector = new BackupLegacyDestinationProjector(
                factory,
                store,
                dataProtection,
                Options.Create(new BackupRoutingOptions { Enabled = false }),
                CreateRegistry(),
                NullLogger<BackupLegacyDestinationProjector>.Instance);
            await Task.WhenAll(projector.ProjectAsync(), projector.ProjectAsync());

            Dictionary<Guid, DateTimeOffset> firstUpdatedAt;
            await using (var first = new ProxyHarborDbContext(dbOptions))
            {
                firstUpdatedAt = await first.BackupDestinations.AsNoTracking()
                    .ToDictionaryAsync(destination => destination.Id, destination => destination.UpdatedAt);
                Assert.Equal(2, firstUpdatedAt.Count);
                Assert.Equal(2, await first.BackupPoolDestinations.CountAsync());
                Assert.Empty(await first.BackupCopies.ToArrayAsync());
                Assert.Empty(await first.BackupDeliveryJobs.ToArrayAsync());
            }

            await projector.ProjectAsync();

            await using (var second = new ProxyHarborDbContext(dbOptions))
            {
                var configuration = await second.BackupConfigurations.AsNoTracking().SingleAsync();
                Assert.Equal(legacySettings, configuration.SettingsJson);
                Assert.Equal(legacySecrets, configuration.ProtectedSecrets);
                Assert.Equal(legacyUpdatedAt, configuration.UpdatedAt);

                var destinations = await second.BackupDestinations.AsNoTracking()
                    .OrderBy(destination => destination.Priority).ToArrayAsync();
                Assert.Equal(2, destinations.Length);
                Assert.All(destinations, destination => Assert.True(destination.Enabled));
                Assert.All(destinations, destination =>
                {
                    Assert.Equal(firstUpdatedAt[destination.Id], destination.UpdatedAt);
                    Assert.DoesNotContain(accessKey, destination.SettingsJson, StringComparison.Ordinal);
                    Assert.DoesNotContain(secretKey, destination.SettingsJson, StringComparison.Ordinal);
                    Assert.DoesNotContain(botToken, destination.SettingsJson, StringComparison.Ordinal);
                    Assert.DoesNotContain(chatId, destination.SettingsJson, StringComparison.Ordinal);
                    Assert.DoesNotContain(accessKey, destination.ProtectedSecrets, StringComparison.Ordinal);
                    Assert.DoesNotContain(secretKey, destination.ProtectedSecrets, StringComparison.Ordinal);
                    Assert.DoesNotContain(botToken, destination.ProtectedSecrets, StringComparison.Ordinal);
                    Assert.DoesNotContain(chatId, destination.ProtectedSecrets, StringComparison.Ordinal);
                });
                var pool = await second.BackupPools.AsNoTracking().SingleAsync();
                Assert.Equal(1, pool.RequiredVerifiedCopies);
                Assert.Equal(2, pool.DesiredVerifiedCopies);
                var routes = await second.BackupPoolDestinations.AsNoTracking()
                    .OrderBy(route => route.Priority).ToArrayAsync();
                Assert.Equal("primary", routes[0].Role);
                Assert.Equal("fallback", routes[1].Role);
                Assert.Equal("put,verify,read", routes[0].AllowedOperations);
                Assert.Equal("put", routes[1].AllowedOperations);
                Assert.All(routes, route => Assert.True(route.Enabled));
                Assert.Empty(await second.BackupDeliveryJobs.ToArrayAsync());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(keyDirectory)) Directory.Delete(keyDirectory, recursive: true);
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static BackupDestinationRegistry CreateRegistry() => new([
        new S3BackupDestinationAdapter(),
        new TelegramBackupDestinationAdapter()
    ]);

    private sealed class ThrowingDbFactory : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() =>
            throw new InvalidOperationException("DB must not be opened.");

        public Task<ProxyHarborDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("DB must not be opened.");
    }

    private sealed class ThrowingConfigurationStore : IBackupConfigurationStore
    {
        public Task<BackupOptions> GetAsync(CancellationToken token = default) =>
            throw new InvalidOperationException("Unreadable legacy configuration.");

        public Task SaveAsync(BackupOptions options, CancellationToken token = default) =>
            throw new NotSupportedException();
    }
}
