using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет постоянный аудит backup на настоящей PostgreSQL, когда она доступна в CI.</summary>
public sealed class BackupAuditIntegrationTests
{
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
                    EncryptionKey = "integration-encryption-key-32-chars"
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
}
