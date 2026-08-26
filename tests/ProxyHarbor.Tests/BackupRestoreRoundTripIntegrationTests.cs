using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Доказывает полное создание, шифрование и восстановление снимка на настоящей PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupRestoreRoundTripIntegrationTests
{
    private const string EncryptionKey = "round-trip-integration-key-32-chars";
    private const string AdminSecret = "round-trip-admin-secret-must-not-leak";
    private const string ConnectionSecret = "Host=secret-db;Password=round-trip-db-secret";

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task EncryptedBackupRestoresEveryDatabaseTableAndRepresentativeField()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var sourceSchema = $"proxyharbor_backup_{Guid.NewGuid():N}";
        var targetSchema = $"proxyharbor_restore_{Guid.NewGuid():N}";
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"proxyharbor-roundtrip-{Guid.NewGuid():N}");
        var sourceConnection = WithSearchPath(baseConnectionString, sourceSchema);
        var targetConnection = WithSearchPath(baseConnectionString, targetSchema);
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await CreateSchemaAsync(admin, sourceSchema);
        await CreateSchemaAsync(admin, targetSchema);

        try
        {
            var sourceOptions = DbOptions(sourceConnection);
            var targetOptions = DbOptions(targetConnection);
            var sourceFactory = new TestDbFactory(sourceOptions);
            await using (var source = new ProxyHarborDbContext(sourceOptions))
            {
                await DatabaseSeeder.InitializeAsync(source);
                await SeedRepresentativeSnapshotAsync(source);
            }
            await using (var target = new ProxyHarborDbContext(targetOptions))
            {
                await DatabaseSeeder.InitializeAsync(target);
                var targetSourceMarker = await target.Sources.OrderBy(source => source.Priority).FirstAsync();
                targetSourceMarker.Name = "Target metadata must survive failed restore";
                target.Proxies.Add(new ProxyEndpoint { Host = "9.9.9.9", Port = 9_999 });
                await target.SaveChangesAsync();
            }

            var backupOptions = new BackupOptions
            {
                Directory = backupDirectory,
                EncryptionKey = EncryptionKey,
                RetentionDays = 7,
                HistoryRetentionDays = 365
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "proxy.example;localhost",
                ["Cors:Origins:0"] = "https://dashboard.example",
                ["Logging:LogLevel:Default"] = "Information",
                ["Security:AdminApiKey"] = AdminSecret,
                ["ConnectionStrings:Postgres"] = ConnectionSecret
            }).Build();
            using var backup = new BackupService(
                sourceFactory,
                new UnusedHttpClientFactory(),
                Options.Create(backupOptions),
                Options.Create(new CollectorOptions()),
                configuration,
                NullLogger<BackupService>.Instance);

            var encryptedPath = await backup.CreateAndSendAsync(CancellationToken.None);
            var restoreKeyFile = Path.Combine(backupDirectory, "restore-key.secret");
            await File.WriteAllTextAsync(restoreKeyFile, EncryptionKey);

            Assert.True(File.Exists(encryptedPath));
            Assert.EndsWith(".phbackup", encryptedPath, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.zip"));
            Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.partial"));
            var lastCompletedAt = await BackupWorker.ReadLastCompletedAtAsync(
                sourceFactory, CancellationToken.None);
            Assert.NotNull(lastCompletedAt);
            Assert.True(lastCompletedAt.Value > SnapshotTime);
            await VerifySettingsSnapshotAsync(encryptedPath, backupDirectory);

            await using (var apiLease = await DatabaseRuntimeGate.TryAcquireApiLeaseAsync(
                targetConnection, CancellationToken.None))
            {
                Assert.NotNull(apiLease);
                string? blockedRestoreTemporaryDirectory = null;
                var blockedExitCode = await RestoreApplication.RunAsync([
                    "--input", encryptedPath,
                    "--connection", targetConnection,
                    "--encryption-key-file", restoreKeyFile,
                    "--replace-existing-data"],
                    new RestoreExecutionHooks(
                        TemporaryDirectoryCreated: directory => blockedRestoreTemporaryDirectory = directory),
                    CancellationToken.None);

                Assert.Equal(1, blockedExitCode);
                Assert.NotNull(blockedRestoreTemporaryDirectory);
                Assert.False(Directory.Exists(blockedRestoreTemporaryDirectory));
                await AssertTargetMarkersSurvivedAsync(targetOptions);
            }

            var invalidBackup = await CreateSemanticallyInvalidBackupAsync(encryptedPath, backupDirectory);
            var failedExitCode = await RestoreApplication.RunAsync([
                "--input", invalidBackup,
                "--connection", targetConnection,
                "--encryption-key-file", restoreKeyFile,
                "--replace-existing-data"]);
            Assert.Equal(1, failedExitCode);
            await using (var unchanged = new ProxyHarborDbContext(targetOptions))
            {
                Assert.Equal(1, await unchanged.Proxies.CountAsync(proxy => proxy.Host == "9.9.9.9"));
                Assert.Equal(BuiltInSourceCatalog.Sources.Count, await unchanged.Sources.CountAsync());
                Assert.Equal(
                    "Target metadata must survive failed restore",
                    await unchanged.Sources.OrderBy(source => source.Priority).Select(source => source.Name).FirstAsync());
            }

            using (var stopping = new CancellationTokenSource())
            {
                string? restoreTemporaryDirectory = null;
                var hooks = new RestoreExecutionHooks(
                    TemporaryDirectoryCreated: directory => restoreTemporaryDirectory = directory,
                    RowImported: (entryName, importedCount) =>
                    {
                        // Останавливаем restore внутри открытого binary COPY, уже после первой
                        // записи. Это самая опасная точка между DELETE и COMMIT.
                        if (entryName == "database/proxies.json" && importedCount == 1)
                            stopping.Cancel();
                    });

                var cancelledExitCode = await RestoreApplication.RunAsync([
                    "--input", encryptedPath,
                    "--connection", targetConnection,
                    "--encryption-key-file", restoreKeyFile,
                    "--replace-existing-data"], hooks, stopping.Token);

                Assert.Equal(130, cancelledExitCode);
                Assert.NotNull(restoreTemporaryDirectory);
                Assert.False(Directory.Exists(restoreTemporaryDirectory));
                await AssertTargetMarkersSurvivedAsync(targetOptions);
            }

            using (var stoppingBeforeCommit = new CancellationTokenSource())
            {
                string? restoreTemporaryDirectory = null;
                var hooks = new RestoreExecutionHooks(
                    TemporaryDirectoryCreated: directory => restoreTemporaryDirectory = directory,
                    BeforeCommit: stoppingBeforeCommit.Cancel);

                var cancelledExitCode = await RestoreApplication.RunAsync([
                    "--input", encryptedPath,
                    "--connection", targetConnection,
                    "--encryption-key-file", restoreKeyFile,
                    "--replace-existing-data"], hooks, stoppingBeforeCommit.Token);

                Assert.Equal(130, cancelledExitCode);
                Assert.NotNull(restoreTemporaryDirectory);
                Assert.False(Directory.Exists(restoreTemporaryDirectory));
                await AssertTargetMarkersSurvivedAsync(targetOptions);
            }

            // Secondary cleanup failure must not hide the primary cancellation or turn its
            // process contract from exit 130 into a generic exit 1. The retained plaintext
            // directory is removed explicitly by this deterministic failure-canary.
            using (var stoppingWithCleanupFailure = new CancellationTokenSource())
            {
                string? restoreTemporaryDirectory = null;
                var hooks = new RestoreExecutionHooks(
                    TemporaryDirectoryCreated: directory => restoreTemporaryDirectory = directory,
                    RowImported: (entryName, importedCount) =>
                    {
                        if (entryName == "database/proxies.json" && importedCount == 1)
                            stoppingWithCleanupFailure.Cancel();
                    },
                    DeleteTemporaryDirectory: _ => throw new IOException("Deterministic cleanup failure."));

                try
                {
                    var cancelledExitCode = await RestoreApplication.RunAsync([
                        "--input", encryptedPath,
                        "--connection", targetConnection,
                        "--encryption-key-file", restoreKeyFile,
                        "--replace-existing-data"], hooks, stoppingWithCleanupFailure.Token);

                    Assert.Equal(130, cancelledExitCode);
                    Assert.NotNull(restoreTemporaryDirectory);
                    Assert.True(Directory.Exists(restoreTemporaryDirectory));
                    await AssertTargetMarkersSurvivedAsync(targetOptions);
                }
                finally
                {
                    if (restoreTemporaryDirectory is not null && Directory.Exists(restoreTemporaryDirectory))
                        Directory.Delete(restoreTemporaryDirectory, recursive: true);
                }
            }

            // Если primary failure не было, невозможность удалить plaintext должна сделать
            // команду неуспешной. Транзакция уже могла закоммититься, поэтому runbook требует
            // сначала проверить целевую БД, а не запускать destructive restore повторно вслепую.
            string? completedRestoreTemporaryDirectory = null;
            try
            {
                var cleanupFailureExitCode = await RestoreApplication.RunAsync([
                    "--input", encryptedPath,
                    "--connection", targetConnection,
                    "--encryption-key-file", restoreKeyFile,
                    "--replace-existing-data"],
                    new RestoreExecutionHooks(
                        TemporaryDirectoryCreated: directory => completedRestoreTemporaryDirectory = directory,
                        DeleteTemporaryDirectory: _ => throw new IOException("Deterministic cleanup failure.")),
                    CancellationToken.None);

                Assert.Equal(1, cleanupFailureExitCode);
                Assert.NotNull(completedRestoreTemporaryDirectory);
                Assert.True(Directory.Exists(completedRestoreTemporaryDirectory));
                await VerifyRestoredSnapshotAsync(targetOptions);
            }
            finally
            {
                if (completedRestoreTemporaryDirectory is not null &&
                    Directory.Exists(completedRestoreTemporaryDirectory))
                {
                    Directory.Delete(completedRestoreTemporaryDirectory, recursive: true);
                }
            }

            var exitCode = await RestoreApplication.RunAsync([
                "--input", encryptedPath,
                "--connection", targetConnection,
                "--encryption-key-file", restoreKeyFile,
                "--replace-existing-data"]);
            Assert.Equal(0, exitCode);

            await VerifyRestoredSnapshotAsync(targetOptions);
        }
        finally
        {
            if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
            await DropSchemaAsync(admin, sourceSchema);
            await DropSchemaAsync(admin, targetSchema);
        }
    }

    /// <summary>Доказывает, что незавершённый destructive restore полностью откатился.</summary>
    private static async Task AssertTargetMarkersSurvivedAsync(
        DbContextOptions<ProxyHarborDbContext> options)
    {
        await using var unchanged = new ProxyHarborDbContext(options);
        Assert.Equal(1, await unchanged.Proxies.CountAsync(proxy => proxy.Host == "9.9.9.9"));
        Assert.Equal(0, await unchanged.Proxies.CountAsync(proxy => proxy.Host == "8.8.8.8"));
        Assert.Equal(BuiltInSourceCatalog.Sources.Count, await unchanged.Sources.CountAsync());
        Assert.Equal(
            "Target metadata must survive failed restore",
            await unchanged.Sources.OrderBy(source => source.Priority).Select(source => source.Name).FirstAsync());
    }

    private static async Task VerifySettingsSnapshotAsync(string encryptedPath, string directory)
    {
        var zipPath = Path.Combine(directory, "settings-verification.zip");
        try
        {
            await BackupEncryption.DecryptAsync(encryptedPath, zipPath, EncryptionKey, CancellationToken.None);
            using var archive = ZipFile.OpenRead(zipPath);
            BackupArchiveValidator.Validate(archive);

            using var backupSettings = JsonDocument.Parse(await ReadEntryAsync(archive, "settings/backup.json"));
            Assert.Equal(directory, backupSettings.RootElement.GetProperty("directory").GetString());
            Assert.False(backupSettings.RootElement.GetProperty("secretsIncluded").GetBoolean());

            using var runtime = JsonDocument.Parse(await ReadEntryAsync(archive, "settings/runtime.json"));
            Assert.Equal("proxy.example;localhost", runtime.RootElement.GetProperty("allowedHosts").GetString());
            Assert.Equal("Information", runtime.RootElement.GetProperty("logLevels").GetProperty("Default").GetString());
            Assert.Equal("https://dashboard.example", runtime.RootElement.GetProperty("corsOrigins")[0].GetString());
            Assert.True(runtime.RootElement.GetProperty("connectionStringConfigured").GetBoolean());
            Assert.False(runtime.RootElement.GetProperty("connectionStringIncluded").GetBoolean());

            var entryContents = new List<string>();
            foreach (var entry in archive.Entries)
                entryContents.Add(await ReadEntryAsync(entry));
            var allText = string.Join('\n', entryContents);
            Assert.DoesNotContain(EncryptionKey, allText, StringComparison.Ordinal);
            Assert.DoesNotContain(AdminSecret, allText, StringComparison.Ordinal);
            Assert.DoesNotContain(ConnectionSecret, allText, StringComparison.Ordinal);
            Assert.DoesNotContain("round-trip-db-secret", allText, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name) =>
        await ReadEntryAsync(BackupArchiveValidator.RequiredEntry(archive, name));

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> CreateSemanticallyInvalidBackupAsync(
        string encryptedPath,
        string directory)
    {
        var zipPath = Path.Combine(directory, "invalid-snapshot.zip");
        var invalidBackup = Path.Combine(directory, "invalid-snapshot.phbackup");
        await BackupEncryption.DecryptAsync(encryptedPath, zipPath, EncryptionKey, CancellationToken.None);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            archive.GetEntry("database/proxies.json")!.Delete();
            var entry = archive.CreateEntry("database/proxies.json", CompressionLevel.SmallestSize);
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, new object?[] { null });
        }
        await BackupEncryption.EncryptAsync(zipPath, invalidBackup, EncryptionKey, CancellationToken.None);
        return invalidBackup;
    }

    private static async Task SeedRepresentativeSnapshotAsync(ProxyHarborDbContext db)
    {
        var builtIn = await db.Sources.OrderBy(source => source.Priority).FirstAsync();
        builtIn.Enabled = false;
        builtIn.LastFetchedAt = SnapshotTime;
        builtIn.LastSucceededAt = SnapshotTime.AddMinutes(-1);
        builtIn.LastContentFetchedAt = SnapshotTime.AddMinutes(-2);
        builtIn.NextFetchAt = SnapshotTime.AddHours(1);
        builtIn.LastItemCount = 321;
        builtIn.LastResultTruncated = true;
        builtIn.ConsecutiveFailures = 2;
        builtIn.LastError = "representative source error";
        builtIn.HttpETag = "\"built-in-v1\"";
        builtIn.HttpLastModifiedAt = SnapshotTime.AddHours(-2);
        db.Sources.Add(ExpectedCustomSource());
        db.Proxies.Add(ExpectedProxy());
        db.VpnSources.Add(ExpectedVpnSource());
        db.VpnEndpoints.Add(ExpectedVpnEndpoint());
        db.VpnEndpointSources.Add(ExpectedVpnEndpointSource());
        db.Runs.Add(ExpectedCollectionRun());
        db.ValidationRuns.Add(ExpectedValidationRun());
        db.BackupRuns.Add(ExpectedBackupRun());
        await db.SaveChangesAsync();
    }

    private static async Task VerifyRestoredSnapshotAsync(DbContextOptions<ProxyHarborDbContext> options)
    {
        await using var db = new ProxyHarborDbContext(options);
        var proxy = await db.Proxies.AsNoTracking().SingleAsync();
        Assert.Equivalent(ExpectedProxy(), proxy, strict: true);

        Assert.Equal(BuiltInSourceCatalog.Sources.Count + 1, await db.Sources.CountAsync());
        var customSource = await db.Sources.AsNoTracking().SingleAsync(source => source.Id == SnapshotIds.CustomSource);
        Assert.Equivalent(ExpectedCustomSource(), customSource, strict: true);
        var builtIn = await db.Sources.AsNoTracking()
            .SingleAsync(source => source.Url == BuiltInSourceCatalog.Sources[0].Url);
        Assert.Equal(BuiltInSourceCatalog.Sources[0].Name, builtIn.Name);
        Assert.False(builtIn.Enabled);
        Assert.Equal(SnapshotTime, builtIn.LastFetchedAt);
        Assert.Equal(SnapshotTime.AddMinutes(-1), builtIn.LastSucceededAt);
        Assert.Equal(SnapshotTime.AddMinutes(-2), builtIn.LastContentFetchedAt);
        Assert.Equal(SnapshotTime.AddHours(1), builtIn.NextFetchAt);
        Assert.Equal(321, builtIn.LastItemCount);
        Assert.True(builtIn.LastResultTruncated);
        Assert.Equal(2, builtIn.ConsecutiveFailures);
        Assert.Equal("representative source error", builtIn.LastError);
        Assert.Equal("\"built-in-v1\"", builtIn.HttpETag);
        Assert.Equal(SnapshotTime.AddHours(-2), builtIn.HttpLastModifiedAt);

        var vpnSource = await db.VpnSources.AsNoTracking()
            .SingleAsync(source => source.Id == SnapshotIds.VpnSource);
        Assert.Equivalent(ExpectedVpnSource(), vpnSource, strict: true);
        var vpnEndpoint = await db.VpnEndpoints.AsNoTracking()
            .SingleAsync(endpoint => endpoint.Id == SnapshotIds.VpnEndpoint);
        Assert.Equivalent(ExpectedVpnEndpoint(), vpnEndpoint, strict: true);
        var vpnProvenance = await db.VpnEndpointSources.AsNoTracking().SingleAsync(link =>
            link.VpnEndpointId == SnapshotIds.VpnEndpoint && link.VpnSourceId == SnapshotIds.VpnSource);
        Assert.Equivalent(ExpectedVpnEndpointSource(), vpnProvenance, strict: true);

        var run = await db.Runs.AsNoTracking().SingleAsync();
        Assert.Equivalent(ExpectedCollectionRun(), run, strict: true);

        var validationRun = await db.ValidationRuns.AsNoTracking().SingleAsync();
        Assert.Equivalent(ExpectedValidationRun(), validationRun, strict: true);

        var backupRun = await db.BackupRuns.AsNoTracking().SingleAsync();
        Assert.Equivalent(ExpectedBackupRun(), backupRun, strict: true);
    }

    private static ProxyEndpoint ExpectedProxy() => new()
    {
        Id = SnapshotIds.Proxy,
        Host = "8.8.8.8",
        Port = 10_808,
        Protocol = ProxyProtocol.Socks5,
        Status = ProxyStatus.Alive,
        LatencyMs = 234,
        ExitIp = "1.1.1.1",
        CountryCode = "US",
        IsAnonymous = true,
        FirstSeenAt = SnapshotTime.AddDays(-2),
        LastSeenAt = SnapshotTime.AddDays(-1),
        LastCheckedAt = SnapshotTime,
        FirstAliveAt = SnapshotTime.AddDays(-1),
        LastAliveAt = SnapshotTime,
        CurrentAliveSince = SnapshotTime.AddMinutes(-10),
        LastValidationAttemptAt = SnapshotTime.AddMinutes(1),
        LastValidationDeferred = true,
        NextCheckAt = SnapshotTime.AddMinutes(5),
        CheckLeaseUntil = SnapshotTime.AddMinutes(1),
        CheckLeaseId = SnapshotIds.Lease,
        SuccessfulChecks = 11,
        FailedChecks = 4,
        ConsecutiveFailedChecks = 2,
        LastError = "representative proxy error"
    };

    private static ProxySource ExpectedCustomSource() => new()
    {
        Id = SnapshotIds.CustomSource,
        Name = "Custom restore source",
        Url = "https://example.com/proxies.txt",
        DefaultProtocol = ProxyProtocol.Socks5,
        Enabled = true,
        Priority = 9_999,
        LastFetchedAt = SnapshotTime,
        LastSucceededAt = SnapshotTime.AddMinutes(-2),
        LastContentFetchedAt = SnapshotTime.AddMinutes(-3),
        NextFetchAt = SnapshotTime.AddHours(2),
        LastItemCount = 17,
        LastResultTruncated = true,
        ConsecutiveFailures = 3,
        LastError = "custom source error",
        HttpETag = "W/\"custom-v1\"",
        HttpLastModifiedAt = SnapshotTime.AddHours(-3)
    };

    private static VpnSource ExpectedVpnSource() => new()
    {
        Id = SnapshotIds.VpnSource,
        Name = "Custom VPN restore source",
        Provider = "Round-trip provider",
        Url = "https://example.com/vpn.txt",
        DefaultProtocol = VpnProtocol.Vless,
        Enabled = true,
        Priority = 321,
        License = "Public test feed",
        LastFetchedAt = SnapshotTime,
        LastSucceededAt = SnapshotTime.AddMinutes(-1),
        LastItemCount = 7,
        ConsecutiveFailures = 1,
        LastError = "representative VPN source error"
    };

    private static VpnEndpoint ExpectedVpnEndpoint() => new()
    {
        Id = SnapshotIds.VpnEndpoint,
        Host = "vpn.example.com",
        Port = 443,
        Protocol = VpnProtocol.Vless,
        Transport = "tcp",
        CountryCode = "DE",
        ConnectionUri = "vless://public-id@vpn.example.com:443?security=tls#round-trip",
        Status = VpnEndpointStatus.Reachable,
        LatencyMs = 87,
        FirstSeenAt = SnapshotTime.AddDays(-3),
        LastSeenAt = SnapshotTime,
        LastCheckedAt = SnapshotTime.AddMinutes(-2),
        NextCheckAt = SnapshotTime.AddMinutes(3),
        SuccessfulChecks = 19,
        FailedChecks = 2,
        LastError = "representative VPN endpoint detail",
        FirstSourceId = SnapshotIds.VpnSource
    };

    private static VpnEndpointSource ExpectedVpnEndpointSource() => new()
    {
        VpnEndpointId = SnapshotIds.VpnEndpoint,
        VpnSourceId = SnapshotIds.VpnSource,
        LastSeenAt = SnapshotTime.AddMinutes(-4)
    };

    private static CollectionRun ExpectedCollectionRun() => new()
    {
        Id = SnapshotIds.CollectionRun,
        StartedAt = SnapshotTime.AddMinutes(-10),
        FinishedAt = SnapshotTime.AddMinutes(-9),
        SourcesProcessed = 81,
        SourcesSucceeded = 79,
        SourcesFailed = 2,
        SourcesSkipped = 3,
        SourcesTruncated = 4,
        CandidatesFound = 12_345,
        CandidateLimitReached = true,
        NewProxies = 2_345,
        AliveProxies = 1_234,
        Status = "completed",
        Error = "representative collection detail"
    };

    private static BackupRun ExpectedBackupRun() => new()
    {
        Id = SnapshotIds.BackupRun,
        StartedAt = SnapshotTime.AddDays(-1),
        FinishedAt = SnapshotTime.AddDays(-1).AddMinutes(1),
        Status = "completed",
        FileName = "previous.phbackup",
        SizeBytes = 98_765,
        TelegramConfigured = true,
        SentToTelegram = true,
        Error = "representative backup detail"
    };

    private static ValidationRun ExpectedValidationRun() => new()
    {
        Id = SnapshotIds.ValidationRun,
        LeaseId = SnapshotIds.Lease,
        StartedAt = SnapshotTime.AddMinutes(-5),
        FinishedAt = SnapshotTime.AddMinutes(-4).AddSeconds(-30),
        Claimed = 1_000,
        Checked = 994,
        Alive = 6,
        Deferred = 6,
        Status = "completed",
        Error = "representative validation detail"
    };

    private static DateTimeOffset SnapshotTime =>
        new(2026, 8, 9, 10, 11, 12, TimeSpan.Zero);

    private static DbContextOptions<ProxyHarborDbContext> DbOptions(string connectionString) =>
        new DbContextOptionsBuilder<ProxyHarborDbContext>().UseNpgsql(connectionString).Options;

    private static string WithSearchPath(string connectionString, string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
        return builder.ConnectionString;
    }

    private static async Task CreateSchemaAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropSchemaAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
        await command.ExecuteNonQueryAsync();
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
            throw new InvalidOperationException("HTTP не должен использоваться без Telegram-конфигурации.");
    }

    private static class SnapshotIds
    {
        internal static readonly Guid Proxy = Guid.Parse("10000000-0000-0000-0000-000000000001");
        internal static readonly Guid CustomSource = Guid.Parse("10000000-0000-0000-0000-000000000002");
        internal static readonly Guid CollectionRun = Guid.Parse("10000000-0000-0000-0000-000000000003");
        internal static readonly Guid BackupRun = Guid.Parse("10000000-0000-0000-0000-000000000004");
        internal static readonly Guid Lease = Guid.Parse("10000000-0000-0000-0000-000000000005");
        internal static readonly Guid ValidationRun = Guid.Parse("10000000-0000-0000-0000-000000000006");
        internal static readonly Guid VpnSource = Guid.Parse("10000000-0000-0000-0000-000000000007");
        internal static readonly Guid VpnEndpoint = Guid.Parse("10000000-0000-0000-0000-000000000008");
    }
}
