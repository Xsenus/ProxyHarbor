using System.IO.Compression;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет постоянный аудит backup на настоящей PostgreSQL, когда она доступна в CI.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupAuditIntegrationTests
{
    private const string EncryptionKey = "integration-encryption-key-32-chars";

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FailureIsRecordedAndAbandonedRunIsRecovered()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var factory = new TestDbFactory(dbOptions);
        await using (var migrationDb = await factory.CreateDbContextAsync())
            await migrationDb.Database.MigrateAsync();

        var testStartedAt = DateTimeOffset.UtcNow;
        var abandonedId = Guid.NewGuid();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.BackupRuns.Add(new BackupRun
            {
                Id = abandonedId,
                StartedAt = testStartedAt.AddHours(-1),
                Status = "running"
            });
            await seed.SaveChangesAsync();
        }

        // Существующий файл нельзя использовать как каталог: это детерминированный сбой
        // после создания audit-записи, не требующий внешней сети или Telegram.
        var invalidDirectory = Path.GetTempFileName();
        try
        {
            using var service = new BackupService(
                factory,
                new UnusedHttpClientFactory(),
                Options.Create(new BackupOptions
                {
                    Directory = invalidDirectory,
                    EncryptionKey = EncryptionKey
                }),
                Options.Create(new CollectorOptions()),
                new ConfigurationBuilder().Build(),
                NullLogger<BackupService>.Instance);

            await Assert.ThrowsAsync<IOException>(() => service.CreateAndSendAsync(CancellationToken.None));

            await using var verify = await factory.CreateDbContextAsync();
            var abandoned = await verify.BackupRuns.AsNoTracking().SingleAsync(x => x.Id == abandonedId);
            var failed = await verify.BackupRuns.AsNoTracking()
                .Where(x => x.Id != abandonedId && x.StartedAt >= testStartedAt)
                .OrderByDescending(x => x.StartedAt)
                .FirstAsync();

            Assert.Equal("failed", abandoned.Status);
            Assert.NotNull(abandoned.FinishedAt);
            Assert.Contains("прерван", abandoned.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("failed", failed.Status);
            Assert.NotNull(failed.FinishedAt);
            Assert.False(string.IsNullOrWhiteSpace(failed.Error));
            Assert.Null(failed.FileName);
            Assert.Equal(0, failed.SizeBytes);
        }
        finally
        {
            File.Delete(invalidDirectory);
            await using var cleanup = await factory.CreateDbContextAsync();
            await cleanup.BackupRuns
                .Where(x => x.Id == abandonedId || x.StartedAt >= testStartedAt)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task TelegramFailureStillAppliesLocalRetentionAndAuditsPublishedBackup()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_backup_delivery_{Guid.NewGuid():N}";
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"proxyharbor-delivery-{Guid.NewGuid():N}");
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            Directory.CreateDirectory(backupDirectory);
            var expiredBackup = Path.Combine(backupDirectory, "proxyharbor-expired.phbackup");
            await File.WriteAllTextAsync(expiredBackup, "expired");
            File.SetLastWriteTimeUtc(expiredBackup, DateTime.UtcNow.AddDays(-8));

            using var clients = new RejectingTelegramClientFactory();
            using var service = new BackupService(
                factory,
                clients,
                Options.Create(new BackupOptions
                {
                    Directory = backupDirectory,
                    EncryptionKey = EncryptionKey,
                    RetentionDays = 7,
                    TelegramBotToken = "test-token",
                    TelegramChatId = "123456"
                }),
                Options.Create(new CollectorOptions()),
                new ConfigurationBuilder().Build(),
                NullLogger<BackupService>.Instance);

            await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => service.CreateAndSendAsync(CancellationToken.None));

            Assert.False(File.Exists(expiredBackup));
            var publishedBackup = Assert.Single(Directory.EnumerateFiles(backupDirectory, "*.phbackup"));
            var file = new FileInfo(publishedBackup);
            Assert.True(file.Length > 0);

            await using var verify = await factory.CreateDbContextAsync();
            var failed = await verify.BackupRuns.AsNoTracking().SingleAsync();
            Assert.Equal("failed", failed.Status);
            Assert.True(failed.TelegramConfigured);
            Assert.False(failed.SentToTelegram);
            Assert.Equal(file.Name, failed.FileName);
            Assert.Equal(file.Length, failed.SizeBytes);
            Assert.NotNull(failed.FinishedAt);
            Assert.Contains("Telegram", failed.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ChangedAuditOwnershipRejectsOtherwiseSuccessfulTelegramBackup()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_backup_ownership_{Guid.NewGuid():N}";
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"proxyharbor-ownership-{Guid.NewGuid():N}");
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            Directory.CreateDirectory(backupDirectory);
            using var clients = new ChangingAuditTelegramClientFactory(factory);
            using var service = new BackupService(
                factory,
                clients,
                Options.Create(new BackupOptions
                {
                    Directory = backupDirectory,
                    EncryptionKey = EncryptionKey,
                    TelegramBotToken = "test-token",
                    TelegramChatId = "123456"
                }),
                Options.Create(new CollectorOptions()),
                new ConfigurationBuilder().Build(),
                NullLogger<BackupService>.Instance);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CreateAndSendAsync(CancellationToken.None));

            Assert.Contains("ownership", exception.Message, StringComparison.OrdinalIgnoreCase);
            var publishedBackup = Assert.Single(Directory.EnumerateFiles(backupDirectory, "*.phbackup"));
            Assert.True(new FileInfo(publishedBackup).Length > 0);

            await using var verify = await factory.CreateDbContextAsync();
            var parallelResult = await verify.BackupRuns.AsNoTracking().SingleAsync();
            Assert.Equal("failed", parallelResult.Status);
            Assert.Equal("parallel failure", parallelResult.Error);
            Assert.Null(parallelResult.FileName);
            Assert.Equal(0, parallelResult.SizeBytes);
            Assert.False(parallelResult.SentToTelegram);
        }
        finally
        {
            if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ShutdownDuringTelegramUploadClosesFilesAndFinalizesFailedAudit()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_backup_cancel_{Guid.NewGuid():N}";
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"proxyharbor-cancel-{Guid.NewGuid():N}");
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            Directory.CreateDirectory(backupDirectory);
            using var clients = new HangingTelegramClientFactory();
            using var service = new BackupService(
                factory,
                clients,
                Options.Create(new BackupOptions
                {
                    Directory = backupDirectory,
                    EncryptionKey = EncryptionKey,
                    TelegramBotToken = "shutdown-secret-token",
                    TelegramChatId = "-100123456"
                }),
                Options.Create(new CollectorOptions()),
                new ConfigurationBuilder().Build(),
                NullLogger<BackupService>.Instance);
            using var stopping = new CancellationTokenSource();

            var backup = service.CreateAndSendAsync(stopping.Token);
            await clients.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await stopping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup)
                .WaitAsync(TimeSpan.FromSeconds(15));

            var encryptedPath = Assert.Single(Directory.EnumerateFiles(backupDirectory, "*.phbackup"));
            Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.partial"));
            Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.part*"));
            var decryptedPath = Path.Combine(backupDirectory, "shutdown-check.zip");
            await BackupEncryption.DecryptAsync(
                encryptedPath, decryptedPath, EncryptionKey, CancellationToken.None);
            using (var archive = ZipFile.OpenRead(decryptedPath))
                BackupArchiveValidator.Validate(archive);

            await using var verify = await factory.CreateDbContextAsync();
            var failed = await verify.BackupRuns.AsNoTracking().SingleAsync();
            Assert.Equal("failed", failed.Status);
            Assert.NotNull(failed.FinishedAt);
            Assert.True(failed.TelegramConfigured);
            Assert.False(failed.SentToTelegram);
            Assert.Equal(Path.GetFileName(encryptedPath), failed.FileName);
            Assert.Equal(new FileInfo(encryptedPath).Length, failed.SizeBytes);
            Assert.Contains("отменена", failed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("shutdown-secret-token", failed.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("-100123456", failed.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
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
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("HTTP не должен использоваться в этом сценарии.");
    }

    private sealed class RejectingTelegramClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new RejectingTelegramHandler());

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("telegram", name);
            return _client;
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class RejectingTelegramHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"ok\":false,\"error_code\":400,\"description\":\"delivery rejected\"}")
            });
    }

    private sealed class HangingTelegramClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HangingTelegramClientFactory() => _client = new HttpClient(new HangingTelegramHandler(Started));

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("telegram", name);
            return _client;
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class HangingTelegramHandler(TaskCompletionSource started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Недостижимый код hanging Telegram handler.");
        }
    }

    /// <summary>
    /// Имитирует успешный Telegram, но завершает audit-строку с ошибкой во время внешней
    /// доставки, чтобы воспроизвести потерю ownership перед переходом в completed.
    /// </summary>
    private sealed class ChangingAuditTelegramClientFactory(
        IDbContextFactory<ProxyHarborDbContext> dbFactory) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new ChangingAuditTelegramHandler(dbFactory));

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("telegram", name);
            return _client;
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class ChangingAuditTelegramHandler(
        IDbContextFactory<ProxyHarborDbContext> dbFactory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await db.BackupRuns
                .Where(x => x.Status == "running")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "failed")
                    .SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.Error, "parallel failure"), cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
        }
    }
}
