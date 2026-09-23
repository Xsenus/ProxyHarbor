using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupCatalogServiceTests
{
    private const string Key = "synthetic-catalog-signing-key-32-chars";
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void SignedCatalogCanBeOpenedWithoutDatabaseAndContainsNoSecrets()
    {
        var snapshot = Snapshot();

        var encoded = BackupCatalogService.Seal(snapshot, Key);
        var opened = BackupCatalogService.Open(encoded, Key);
        var json = Encoding.UTF8.GetString(encoded);

        Assert.Equal(snapshot, opened with { Copies = snapshot.Copies });
        Assert.Equal(snapshot.Copies, opened.Copies);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpoint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bucket", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, json, StringComparison.Ordinal);
        Assert.Equal("safe/snapshot.phbackup", opened.Copies[0].NativeLocator);
        Assert.NotEqual(json, Encoding.UTF8.GetString(BackupCatalogService.Seal(snapshot, Key)));
    }

    [Fact]
    public void SingleCopySidecarIsByteStableAcrossCrashRetry()
    {
        var snapshot = Snapshot();
        var first = BackupCatalogService.SealForCopySidecar(snapshot, Key);
        var retry = BackupCatalogService.SealForCopySidecar(snapshot, Key);

        Assert.Equal(first, retry);
        Assert.Equal(snapshot.Copies, BackupCatalogService.Open(first, Key).Copies);
        Assert.NotEqual(first, BackupCatalogService.Seal(snapshot, Key));
        Assert.Throws<ArgumentException>(() => BackupCatalogService.SealForCopySidecar(
            snapshot, "short"));
        var secondCopy = snapshot.Copies[0] with
        {
            CopyId = Guid.NewGuid(),
            DestinationId = Guid.NewGuid()
        };
        var multiCopy = snapshot with { Copies = [snapshot.Copies[0], secondCopy] };
        Assert.Throws<ArgumentException>(() =>
            BackupCatalogService.SealForCopySidecar(multiCopy, Key));
    }

    [Fact]
    public void TamperingWrongKeyAndDuplicateFieldsAreRejected()
    {
        var encoded = BackupCatalogService.Seal(Snapshot(), Key);
        var json = Encoding.UTF8.GetString(encoded);
        var changedLocator = Encoding.UTF8.GetBytes(json.Replace(
            "safe/snapshot.phbackup", "evil/snapshot.phbackup", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Open(changedLocator, Key));
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Open(encoded,
            "different-catalog-signing-key-32-chars"));
        using var parsed = JsonDocument.Parse(encoded);
        var salt = parsed.RootElement.GetProperty("salt").GetString()!;
        var changedSalt = Encoding.UTF8.GetBytes(json.Replace(
            $"\"salt\":\"{salt}\"", $"\"salt\":\"{new string('f', 32)}\"",
            StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Open(changedSalt, Key));

        var duplicate = Encoding.UTF8.GetBytes(json.Replace(
            "\"version\":1,", "\"version\":1,\"version\":1,", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Open(duplicate, Key));
        var unknown = Encoding.UTF8.GetBytes(json.Replace(
            "\"mac\":", "\"endpoint\":\"https://example.invalid\",\"mac\":", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Open(unknown, Key));
    }

    [Fact]
    public void DuplicateDestinationStaleSnapshotAndUnsafeLocatorAreRejected()
    {
        var snapshot = Snapshot();
        var duplicate = snapshot with
        {
            Copies = [snapshot.Copies[0], snapshot.Copies[0] with { CopyId = Guid.NewGuid() }]
        };
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Seal(duplicate, Key));
        var stale = snapshot with { ExportedAt = snapshot.FinishedAt.AddSeconds(-1) };
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Seal(stale, Key));
        var unsafeLocator = snapshot with
        {
            Copies = [snapshot.Copies[0] with { NativeLocator = "https://user:secret@example.invalid/file" }]
        };
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Seal(unsafeLocator, Key));
    }

    [Fact]
    public void InvalidSnapshotAndCopyFieldsFailClosed()
    {
        var valid = Snapshot();
        var copy = valid.Copies[0];
        var invalid = new BackupCatalogSnapshot[]
        {
            valid with { BackupRunId = Guid.Empty },
            valid with { FileName = "../snapshot.phbackup" },
            valid with { FileName = "snapshot.zip" },
            valid with { SizeBytes = 0 },
            valid with { Sha256 = new string('A', 64) },
            valid with { PolicyVersion = 0 },
            valid with { KeyReference = "key:secret" },
            valid with { KeyReference = new string('k', 65) },
            valid with { FinishedAt = valid.FinishedAt.ToOffset(TimeSpan.FromHours(1)) },
            valid with { ExportedAt = valid.ExportedAt.ToOffset(TimeSpan.FromHours(1)) },
            valid with { ExportedAt = DateTimeOffset.UtcNow.AddHours(1) },
            valid with { Copies = [] },
            valid with { Copies = [copy with { CopyId = Guid.Empty }] },
            valid with { Copies = [copy with { DestinationId = Guid.Empty }] },
            valid with { Copies = [copy, copy with { DestinationId = Guid.NewGuid() }] },
            valid with { Copies = [copy with { Kind = "telegram" }] },
            valid with { Copies = [copy with { Priority = -1 }] },
            valid with { Copies = [copy with { VerifiedAt = valid.FinishedAt.AddSeconds(-1) }] },
            valid with { Copies = [copy with { VerifiedAt = valid.ExportedAt.AddSeconds(1) }] },
            valid with { Copies = [copy with { NativeLocator = "../snapshot.phbackup" }] },
            valid with { Copies = [copy with { NativeLocator = "safe/other.phbackup" }] },
            valid with { Copies = [copy with { NativeLocator = "safe\\snapshot.phbackup" }] }
        };
        foreach (var snapshot in invalid)
            Assert.Throws<InvalidDataException>(() => BackupCatalogService.Seal(snapshot, Key));
        Assert.Throws<ArgumentException>(() => BackupCatalogService.Seal(valid, "short"));
    }

    [Fact]
    public void OversizedAndStructurallyAmbiguousCatalogsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Open(
            new byte[256 * 1024 + 1], Key));
        var json = Encoding.UTF8.GetString(BackupCatalogService.Seal(Snapshot(), Key));
        var missing = Encoding.UTF8.GetBytes(json.Replace(
            "\"keyReference\":\"legacy\",", string.Empty, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Open(missing, Key));
        var nestedDuplicate = Encoding.UTF8.GetBytes(json.Replace(
            "\"kind\":\"s3\",", "\"kind\":\"s3\",\"kind\":\"s3\",",
            StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => BackupCatalogService.Open(nestedDuplicate, Key));
    }

    [Fact]
    public async Task ExportSelectsOnlyExactVerifiedS3ReadCopies()
    {
        var factory = NewFactory();
        Guid runId;
        Guid allowedId;
        Guid allowedCopyId = Guid.Empty;
        Guid forbiddenCopyId = Guid.Empty;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow;
            var pool = new BackupPool { Name = "catalog-test", PolicyVersion = 3 };
            var run = new BackupRun
            {
                Status = "completed",
                StartedAt = now.AddMinutes(-2),
                FinishedAt = now.AddMinutes(-1),
                BackupPoolId = pool.Id,
                ProtectionPolicyVersion = 3,
                RequiredVerifiedCopies = 1,
                DesiredVerifiedCopies = 2,
                FileName = "snapshot.phbackup",
                SizeBytes = 5,
                ContentSha256 = Hash
            };
            db.AddRange(pool, run);
            var configs = new[]
            {
                (Name: "allowed", Kind: "s3", Operations: "put,verify,read", Hash, State: "verified"),
                (Name: "forbidden", Kind: "s3", Operations: "put,verify", Hash, State: "verified"),
                (Name: "wrong-hash", Kind: "s3", Operations: "read", Hash: new string('b', 64), State: "verified"),
                (Name: "foreign-key", Kind: "s3", Operations: "read", Hash, State: "verified"),
                (Name: "telegram", Kind: "telegram", Operations: "read", Hash, State: "verified")
            };
            Guid first = Guid.Empty;
            foreach (var (name, kind, operations, hash, state) in configs)
            {
                var destination = new BackupDestination
                {
                    Name = name,
                    Kind = kind,
                    Enabled = true,
                    FailureDomain = name,
                    SettingsJson = $"{{\"prefix\":\"safe/{name}\"}}"
                };
                if (name == "allowed") first = destination.Id;
                db.BackupDestinations.Add(destination);
                db.BackupPoolDestinations.Add(new BackupPoolDestination
                {
                    BackupPoolId = pool.Id,
                    BackupDestinationId = destination.Id,
                    AllowedOperations = operations,
                    Enabled = true
                });
                var copy = new BackupCopy
                {
                    BackupRunId = run.Id,
                    BackupDestinationId = destination.Id,
                    ContentSha256 = hash,
                    SizeBytes = 5,
                    State = state,
                    VerifiedAt = now,
                    NativeLocator = name == "foreign-key"
                        ? "foreign/snapshot.phbackup"
                        : $"safe/{name}/snapshot.phbackup",
                    PolicyVersion = 3
                };
                if (name == "allowed") allowedCopyId = copy.Id;
                if (name == "forbidden") forbiddenCopyId = copy.Id;
                db.BackupCopies.Add(copy);
            }
            await db.SaveChangesAsync();
            runId = run.Id;
            allowedId = first;
        }
        var service = new BackupCatalogService(factory);

        var snapshot = await service.CreateAsync(runId, "legacy", CancellationToken.None);

        Assert.Single(snapshot.Copies);
        Assert.Equal(allowedId, snapshot.Copies[0].DestinationId);
        Assert.Equal(Hash, snapshot.Sha256);
        Assert.Equal("legacy", snapshot.KeyReference);

        var sidecar = await service.CreateForCopyAsync(
            allowedCopyId, "catalog-v1", CancellationToken.None);
        Assert.Single(sidecar.Copies);
        Assert.Equal(allowedCopyId, sidecar.Copies[0].CopyId);
        Assert.Equal("catalog-v1", sidecar.KeyReference);
        Assert.Equal(sidecar.Copies[0].VerifiedAt, sidecar.ExportedAt);
        var repeated = await service.CreateForCopyAsync(
            allowedCopyId, "catalog-v1", CancellationToken.None);
        Assert.Equal(BackupCatalogService.SealForCopySidecar(sidecar, Key),
            BackupCatalogService.SealForCopySidecar(repeated, Key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateForCopyAsync(
            forbiddenCopyId, "catalog-v1", CancellationToken.None));

        await using (var db = await factory.CreateDbContextAsync())
        {
            var route = await db.BackupPoolDestinations.SingleAsync(
                item => item.BackupDestinationId == allowedId);
            route.AllowedOperations = "put,verify";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(
            runId, "legacy", CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PostgreSqlInventoryExportsVerifiableOfflineCatalog()
    {
        var baseConnection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        var schema = $"proxyharbor_catalog_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var factory = new TestDbFactory(new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connection.ConnectionString).Options);
            Guid runId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await DatabaseSeeder.MigrateSchemaAsync(db, CancellationToken.None);
                var now = DateTimeOffset.UtcNow;
                var pool = new BackupPool { Name = "catalog-pg", PolicyVersion = 2 };
                var run = new BackupRun
                {
                    Status = "completed",
                    StartedAt = now.AddMinutes(-2),
                    FinishedAt = now.AddMinutes(-1),
                    BackupPoolId = pool.Id,
                    ProtectionPolicyVersion = 2,
                    RequiredVerifiedCopies = 1,
                    DesiredVerifiedCopies = 1,
                    FileName = "snapshot.phbackup",
                    SizeBytes = 5,
                    ContentSha256 = Hash
                };
                var destination = new BackupDestination
                {
                    Name = "catalog-s3",
                    Kind = "s3",
                    Enabled = true,
                    FailureDomain = "domain-a",
                    SettingsJson = "{\"prefix\":\"safe\"}"
                };
                db.AddRange(pool, run, destination,
                    new BackupPoolDestination
                    {
                        BackupPoolId = pool.Id,
                        BackupDestinationId = destination.Id,
                        AllowedOperations = "read",
                        Enabled = true
                    },
                    new BackupCopy
                    {
                        BackupRunId = run.Id,
                        BackupDestinationId = destination.Id,
                        ContentSha256 = Hash,
                        SizeBytes = 5,
                        PolicyVersion = 2,
                        State = "verified",
                        VerifiedAt = now,
                        NativeLocator = "safe/snapshot.phbackup"
                    });
                await db.SaveChangesAsync();
                runId = run.Id;
            }

            var snapshot = await new BackupCatalogService(factory).CreateAsync(
                runId, "legacy", CancellationToken.None);
            var offline = BackupCatalogService.Open(BackupCatalogService.Seal(snapshot, Key), Key);

            Assert.Equal(runId, offline.BackupRunId);
            Assert.Single(offline.Copies);
            Assert.Equal("safe/snapshot.phbackup", offline.Copies[0].NativeLocator);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static BackupCatalogSnapshot Snapshot()
    {
        var finished = DateTimeOffset.UtcNow.AddMinutes(-2);
        var exported = DateTimeOffset.UtcNow;
        return new BackupCatalogSnapshot(
            Guid.NewGuid(), "snapshot.phbackup", 5, Hash, 1,
            finished, exported, "legacy",
            [new BackupCatalogCopy(Guid.NewGuid(), Guid.NewGuid(), "s3",
                "safe/snapshot.phbackup", 1, finished.AddMinutes(1))]);
    }

    private static TestDbFactory NewFactory() => new(
        new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"backup-catalog-{Guid.NewGuid():N}").Options);

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
