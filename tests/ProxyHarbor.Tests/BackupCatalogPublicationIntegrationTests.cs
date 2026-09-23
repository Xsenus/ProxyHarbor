using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupCatalogPublicationIntegrationTests
{
    private const string Key = "synthetic-dedicated-catalog-signing-key-32-characters";

    [Fact]
    public async Task PublicationIsFencedAndIdempotentAfterUnknownOutcome()
    {
        var connection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        await using var fixture = await Fixture.CreateAsync(connection);
        var adapter = new FakeS3Adapter { FailWithUnknown = true };
        var processor = fixture.Processor(adapter);

        var first = await processor.TryClaimAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await processor.TryClaimAsync(CancellationToken.None));
        await processor.ProcessAsync(first, CancellationToken.None);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var copy = await db.BackupCopies.SingleAsync();
            Assert.Equal("pending", copy.CatalogState);
            Assert.Equal(nameof(BackupDestinationErrorCode.UnknownOutcome), copy.CatalogLastErrorCode);
            Assert.Null(copy.CatalogObjectKey);
            Assert.Null(copy.CatalogPublishedAt);
            copy.CatalogNotBefore = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        adapter.FailWithUnknown = false;
        var retry = await processor.TryClaimAsync(CancellationToken.None);
        Assert.NotNull(retry);
        Assert.NotEqual(first.LeaseId, retry.LeaseId);
        await processor.ProcessAsync(first, CancellationToken.None);
        Assert.Single(adapter.Catalogs);
        await processor.ProcessAsync(retry, CancellationToken.None);

        Assert.Equal(2, adapter.Catalogs.Count);
        Assert.Equal(adapter.Catalogs[0], adapter.Catalogs[1]);
        Assert.Null(await processor.TryClaimAsync(CancellationToken.None));
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var copy = await db.BackupCopies.SingleAsync();
            Assert.Equal("published", copy.CatalogState);
            Assert.Equal("safe/snapshot.phbackup.catalog.v1.json", copy.CatalogObjectKey);
            Assert.NotNull(copy.CatalogPublishedAt);
            Assert.Null(copy.CatalogLeaseId);
            Assert.Equal(2, copy.CatalogAttempt);
            Assert.Equal("verified", copy.State);
        }
    }

    [Fact]
    public async Task ExpiredLeaseCanBeStolenButOldOwnerCannotPublish()
    {
        var connection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        await using var fixture = await Fixture.CreateAsync(connection);
        var adapter = new FakeS3Adapter();
        var firstProcessor = fixture.Processor(adapter);
        var secondProcessor = fixture.Processor(adapter);
        var old = await firstProcessor.TryClaimAsync(CancellationToken.None);
        Assert.NotNull(old);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var copy = await db.BackupCopies.SingleAsync();
            copy.CatalogLeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        var current = await secondProcessor.TryClaimAsync(CancellationToken.None);
        Assert.NotNull(current);
        await firstProcessor.ProcessAsync(old, CancellationToken.None);
        Assert.Empty(adapter.Catalogs);
        await secondProcessor.ProcessAsync(current, CancellationToken.None);
        Assert.Single(adapter.Catalogs);
    }

    [Fact]
    public async Task ChangedKeyReferenceRequiresManualReviewBeforeProviderIO()
    {
        var connection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        await using var fixture = await Fixture.CreateAsync(connection);
        var adapter = new FakeS3Adapter { FailWithUnknown = true };
        var first = fixture.Processor(adapter);
        var lease = await first.TryClaimAsync(CancellationToken.None);
        Assert.NotNull(lease);
        await first.ProcessAsync(lease, CancellationToken.None);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var copy = await db.BackupCopies.SingleAsync();
            copy.CatalogNotBefore = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        var rotated = fixture.Processor(adapter, "catalog-v2");
        Assert.Null(await rotated.TryClaimAsync(CancellationToken.None));
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var copy = await db.BackupCopies.SingleAsync();
            Assert.Equal("manual_review", copy.CatalogState);
            Assert.Equal("KeyReferenceChanged", copy.CatalogLastErrorCode);
        }
        Assert.Single(adapter.Catalogs);
    }

    [Fact]
    public async Task DisabledCatalogPublisherDoesNotClaim()
    {
        var processor = new BackupCatalogPublicationProcessor(null!,
            new BackupDestinationRegistry([new FakeS3Adapter(), new NoCatalogTelegramAdapter()]),
            Options.Create(new BackupCatalogSigningOptions { Enabled = false }),
            Options.Create(new BackupRoutingOptions { Enabled = true }));
        Assert.Null(await processor.TryClaimAsync(CancellationToken.None));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NpgsqlConnection admin;
        private readonly string schema;
        internal TestDbFactory Factory { get; }

        private Fixture(NpgsqlConnection admin, string schema, TestDbFactory factory)
        {
            this.admin = admin;
            this.schema = schema;
            Factory = factory;
        }

        internal static async Task<Fixture> CreateAsync(string baseConnection)
        {
            var schema = $"proxyharbor_catalog_publish_{Guid.NewGuid():N}";
            var admin = new NpgsqlConnection(baseConnection);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
                await create.ExecuteNonQueryAsync();
            var connection = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
            var factory = new TestDbFactory(new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connection.ConnectionString).Options);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await DatabaseSeeder.MigrateSchemaAsync(db, CancellationToken.None);
                var now = DateTimeOffset.UtcNow;
                var pool = new BackupPool { Name = "catalog-publication", PolicyVersion = 2 };
                var run = new BackupRun
                {
                    Status = "completed",
                    StartedAt = now.AddMinutes(-3),
                    FinishedAt = now.AddMinutes(-2),
                    BackupPoolId = pool.Id,
                    ProtectionPolicyVersion = 2,
                    RequiredVerifiedCopies = 1,
                    DesiredVerifiedCopies = 1,
                    FileName = "snapshot.phbackup",
                    SizeBytes = 5,
                    ContentSha256 = new string('a', 64)
                };
                var destination = new BackupDestination
                {
                    Name = "S3 fixture",
                    Kind = "s3",
                    Enabled = true,
                    FailureDomain = "fixture",
                    SettingsJson = "{\"prefix\":\"safe\"}"
                };
                db.AddRange(pool, run, destination);
                db.BackupPoolDestinations.Add(new BackupPoolDestination
                {
                    BackupPoolId = pool.Id,
                    BackupDestinationId = destination.Id,
                    Enabled = true,
                    AllowedOperations = "put,verify,read"
                });
                db.BackupCopies.Add(new BackupCopy
                {
                    BackupRunId = run.Id,
                    BackupDestinationId = destination.Id,
                    ContentSha256 = run.ContentSha256,
                    SizeBytes = run.SizeBytes,
                    State = "verified",
                    VerifiedAt = now.AddMinutes(-1),
                    NativeLocator = "safe/snapshot.phbackup",
                    PolicyVersion = 2
                });
                await db.SaveChangesAsync();
            }
            return new Fixture(admin, schema, factory);
        }

        internal BackupCatalogPublicationProcessor Processor(FakeS3Adapter adapter,
            string keyReference = "catalog-v1") => new(
                Factory,
                new BackupDestinationRegistry([adapter, new NoCatalogTelegramAdapter()]),
                Options.Create(new BackupCatalogSigningOptions
                {
                    Enabled = true,
                    SigningKey = Key,
                    KeyReference = keyReference
                }),
                Options.Create(new BackupRoutingOptions { Enabled = true }));

        public async ValueTask DisposeAsync()
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeS3Adapter : IBackupDestinationAdapter
    {
        public string Kind => "s3";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), false, false, false);
        public bool FailWithUnknown { get; set; }
        public List<byte[]> Catalogs { get; } = [];

        public Task<BackupObjectStorageVerificationResult> PublishCatalogAsync(
            BackupDestination destination, string backupObjectKey,
            ReadOnlyMemory<byte> signedCatalog, CancellationToken token)
        {
            var bytes = signedCatalog.ToArray();
            Catalogs.Add(bytes);
            _ = BackupCatalogService.Open(bytes, Key);
            if (FailWithUnknown)
                throw new BackupDestinationOperationException(
                    new BackupDestinationFailure(BackupDestinationErrorCode.UnknownOutcome,
                        BackupDestinationFailureDisposition.UnknownOutcome),
                    "fixture unknown");
            return Task.FromResult(new BackupObjectStorageVerificationResult(
                S3BackupObjectStorageTransport.BuildCatalogObjectKey(backupObjectKey),
                bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                null, null, null));
        }
    }

    private sealed class NoCatalogTelegramAdapter : IBackupDestinationAdapter
    {
        public string Kind => "telegram";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(false), new(false), new(false), false, false, false);
    }
}
