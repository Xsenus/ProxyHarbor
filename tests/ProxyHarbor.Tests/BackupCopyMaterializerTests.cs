using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupCopyMaterializerTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4, 5];
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData(Bytes));

    [Fact]
    public async Task ReadsFallbackAfterPreferredProviderFailureWithoutChangingCopies()
    {
        var directory = NewDirectory();
        try
        {
            var factory = NewFactory();
            var (run, preferred, fallback) = await SeedAsync(factory);
            var adapter = new ReadAdapter(preferred.Id, Bytes);
            var materializer = NewMaterializer(factory, adapter);
            var path = Path.Combine(directory, "restored.phbackup");

            var result = await materializer.MaterializeAsync(
                run.Id, path, TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Equal(fallback.Id, result.BackupDestinationId);
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal([preferred.Id, fallback.Id], adapter.Calls);
            await using var db = await factory.CreateDbContextAsync();
            Assert.All(await db.BackupCopies.ToArrayAsync(), copy => Assert.Equal("verified", copy.State));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RejectsWrongIdentityAndForbiddenReadRouteBeforeProviderIo()
    {
        var directory = NewDirectory();
        try
        {
            var factory = NewFactory();
            var (run, preferred, fallback) = await SeedAsync(factory);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var copy = await db.BackupCopies.SingleAsync(item => item.BackupDestinationId == preferred.Id);
                copy.ContentSha256 = new string('a', 64);
                var route = await db.BackupPoolDestinations.SingleAsync(
                    item => item.BackupDestinationId == fallback.Id);
                route.AllowedOperations = "put,verify";
                await db.SaveChangesAsync();
            }
            var adapter = new ReadAdapter(Guid.Empty, Bytes);
            var materializer = NewMaterializer(factory, adapter);

            await Assert.ThrowsAsync<InvalidOperationException>(() => materializer.MaterializeAsync(
                run.Id, Path.Combine(directory, "restored.phbackup"),
                TimeSpan.FromSeconds(10), CancellationToken.None));
            Assert.Empty(adapter.Calls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AllProvidersUnavailableLeavesNoPublishedFile()
    {
        var directory = NewDirectory();
        try
        {
            var factory = NewFactory();
            var (run, _, _) = await SeedAsync(factory);
            var adapter = new ReadAdapter(Guid.Empty, Bytes, failAll: true);
            var materializer = NewMaterializer(factory, adapter);
            var path = Path.Combine(directory, "restored.phbackup");

            var exception = await Assert.ThrowsAsync<BackupDestinationOperationException>(
                () => materializer.MaterializeAsync(
                    run.Id, path, TimeSpan.FromSeconds(10), CancellationToken.None));

            Assert.Equal(BackupDestinationErrorCode.Unavailable, exception.Failure.Code);
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.candidate"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DrainingSourceRemainsReadableButExistingTargetIsNeverOverwritten()
    {
        var directory = NewDirectory();
        try
        {
            var factory = NewFactory();
            var (run, preferred, fallback) = await SeedAsync(factory);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var preferredRoute = await db.BackupPoolDestinations.SingleAsync(
                    item => item.BackupDestinationId == preferred.Id);
                preferredRoute.Enabled = false;
                preferredRoute.Draining = true;
                var fallbackRoute = await db.BackupPoolDestinations.SingleAsync(
                    item => item.BackupDestinationId == fallback.Id);
                fallbackRoute.Enabled = false;
                await db.SaveChangesAsync();
            }
            var adapter = new ReadAdapter(Guid.Empty, Bytes);
            var materializer = NewMaterializer(factory, adapter);
            var path = Path.Combine(directory, "restored.phbackup");
            await File.WriteAllBytesAsync(path, [9]);
            await Assert.ThrowsAsync<IOException>(() => materializer.MaterializeAsync(
                run.Id, path, TimeSpan.FromSeconds(10), CancellationToken.None));
            Assert.Empty(adapter.Calls);

            File.Delete(path);
            var result = await materializer.MaterializeAsync(
                run.Id, path, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal(preferred.Id, result.BackupDestinationId);
            Assert.Equal([preferred.Id], adapter.Calls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CorruptPreferredCopyIsQuarantinedAndFallbackIsRead()
    {
        var baseConnection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        var directory = NewDirectory();
        var schema = $"proxyharbor_backup_read_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var factory = new TestDbFactory(new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connection.ConnectionString).Options);
            await using (var db = await factory.CreateDbContextAsync())
                await DatabaseSeeder.MigrateSchemaAsync(db, CancellationToken.None);
            var (run, preferred, fallback) = await SeedAsync(factory);
            var adapter = new ReadAdapter(Guid.Empty, Bytes, preferred.Id);
            var materializer = NewMaterializer(factory, adapter);
            var path = Path.Combine(directory, "restored.phbackup");

            var result = await materializer.MaterializeAsync(
                run.Id, path, TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Equal(fallback.Id, result.BackupDestinationId);
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "*.candidate"));
            await using var verify = await factory.CreateDbContextAsync();
            var copies = await verify.BackupCopies.ToDictionaryAsync(item => item.BackupDestinationId);
            Assert.Equal("quarantined", copies[preferred.Id].State);
            Assert.Null(copies[preferred.Id].VerifiedAt);
            Assert.Equal(BackupDestinationErrorCode.IntegrityMismatch.ToString(),
                copies[preferred.Id].LastErrorCode);
            Assert.Equal("verified", copies[fallback.Id].State);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static BackupCopyMaterializer NewMaterializer(
        IDbContextFactory<ProxyHarborDbContext> factory,
        ReadAdapter adapter) => new(factory,
        new BackupDestinationRegistry([adapter, new TelegramBackupDestinationAdapter()]),
        new BackupDestinationHealth());

    private static TestDbFactory NewFactory() => new(
        new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"materializer-{Guid.NewGuid():N}").Options);

    private static async Task<(BackupRun Run, BackupDestination Preferred, BackupDestination Fallback)> SeedAsync(
        TestDbFactory factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var pool = new BackupPool { Name = "test-pool", PolicyVersion = 1 };
        var run = new BackupRun
        {
            Status = "completed",
            FinishedAt = DateTimeOffset.UtcNow,
            FileName = "snapshot.phbackup",
            SizeBytes = Bytes.Length,
            ContentSha256 = Hash,
            BackupPoolId = pool.Id,
            ProtectionPolicyVersion = 1,
            RequiredVerifiedCopies = 1,
            DesiredVerifiedCopies = 2
        };
        var preferred = new BackupDestination
        {
            Name = "preferred",
            Kind = "s3",
            Enabled = true,
            FailureDomain = "domain-a"
        };
        var fallback = new BackupDestination
        {
            Name = "fallback",
            Kind = "s3",
            Enabled = true,
            FailureDomain = "domain-b"
        };
        db.AddRange(pool, run, preferred, fallback);
        foreach (var (destination, priority) in new[] { (preferred, 1), (fallback, 2) })
        {
            db.BackupPoolDestinations.Add(new BackupPoolDestination
            {
                BackupPoolId = pool.Id,
                BackupDestinationId = destination.Id,
                Priority = priority,
                Enabled = true,
                AllowedOperations = "put,verify,read"
            });
            db.BackupCopies.Add(new BackupCopy
            {
                BackupRunId = run.Id,
                BackupDestinationId = destination.Id,
                ContentSha256 = Hash,
                SizeBytes = Bytes.Length,
                State = "verified",
                VerifiedAt = DateTimeOffset.UtcNow,
                NativeLocator = $"safe/{destination.Name}.phbackup",
                PolicyVersion = 1
            });
        }
        await db.SaveChangesAsync();
        return (run, preferred, fallback);
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backup-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ReadAdapter(
        Guid failingDestination, byte[] bytes, Guid corruptDestination = default, bool failAll = false)
        : IBackupDestinationAdapter
    {
        public string Kind => "s3";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), false, false, false);
        public List<Guid> Calls { get; } = [];

        public async Task<BackupDestinationMaterializationResult> MaterializeAsync(
            BackupDestination destination, string fileName, string nativeLocator,
            string finalPath, string expectedSha256, long expectedSize, CancellationToken token)
        {
            Calls.Add(destination.Id);
            if (failAll || destination.Id == failingDestination)
                throw new BackupDestinationOperationException(
                    new BackupDestinationFailure(
                        BackupDestinationErrorCode.Unavailable,
                        BackupDestinationFailureDisposition.Retryable),
                    "synthetic provider outage");
            await File.WriteAllBytesAsync(
                finalPath, destination.Id == corruptDestination ? [9, 9, 9, 9, 9] : bytes, token);
            return new BackupDestinationMaterializationResult(
                finalPath, bytes.Length, expectedSha256, null, null);
        }
    }
}
