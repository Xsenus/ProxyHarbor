using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет, что restore принимает только однозначный backup без секретов.</summary>
public sealed class BackupArchiveValidatorTests
{
    [Fact]
    public void AcceptsLegacyBackupManifest()
    {
        using var archive = CreateArchive("""{"version":2,"secretsIncluded":false}""");

        BackupArchiveValidator.Validate(archive);
    }

    [Fact]
    public void AcceptsCurrentBackupManifestWithAuditHistory()
    {
        using var archive = CreateArchive(
            """{"version":4,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""",
            includeBackupRuns: true,
            includeValidationRuns: true,
            includeSettings: true);

        BackupArchiveValidator.Validate(archive);
    }

    [Fact]
    public void AcceptsVersionFiveOnlyWithCompleteTypedSettings()
    {
        var settings = CurrentSettings();
        using var archive = CreateArchive(
            """{"version":5,"settingsSchemaVersion":1,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""",
            includeBackupRuns: true,
            includeValidationRuns: true,
            includeSettings: true,
            collectorSettings: settings.Collector,
            backupSettings: settings.Backup,
            runtimeSettings: settings.Runtime);

        BackupArchiveValidator.Validate(archive);
    }

    [Fact]
    public void VersionSixRequiresAndAcceptsCompleteIdentitySnapshot()
    {
        var settings = CurrentSettings();
        using var archive = CreateArchive(
            """{"version":6,"settingsSchemaVersion":1,"createdAt":"2026-08-25T10:00:00Z","secretsIncluded":false}""",
            includeBackupRuns: true, includeValidationRuns: true, includeSettings: true, includeIdentity: true,
            collectorSettings: settings.Collector, backupSettings: settings.Backup, runtimeSettings: settings.Runtime);

        BackupArchiveValidator.Validate(archive);
    }

    [Fact]
    public void VersionFiveRejectsIdentitySnapshotFromNewerSchema()
    {
        var settings = CurrentSettings();
        using var archive = CreateArchive(
            """{"version":5,"settingsSchemaVersion":1,"createdAt":"2026-08-25T10:00:00Z","secretsIncluded":false}""",
            includeBackupRuns: true, includeValidationRuns: true, includeSettings: true, includeIdentity: true,
            collectorSettings: settings.Collector, backupSettings: settings.Backup, runtimeSettings: settings.Runtime);

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("версии 6", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionFiveRejectsPresentButIncompleteSettingsObject()
    {
        var settings = CurrentSettings();
        using var archive = CreateArchive(
            """{"version":5,"settingsSchemaVersion":1,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""",
            includeBackupRuns: true,
            includeValidationRuns: true,
            includeSettings: true,
            collectorSettings: "{}",
            backupSettings: settings.Backup,
            runtimeSettings: settings.Runtime);

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("полной схеме", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionFiveRejectsSecretInclusionFlagInsideSettings()
    {
        var settings = CurrentSettings();
        var unsafeBackup = settings.Backup.Replace(
            "\"secretsIncluded\":false", "\"secretsIncluded\":true", StringComparison.Ordinal);
        using var archive = CreateArchive(
            """{"version":5,"settingsSchemaVersion":1,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""",
            includeBackupRuns: true,
            includeValidationRuns: true,
            includeSettings: true,
            collectorSettings: settings.Collector,
            backupSettings: unsafeBackup,
            runtimeSettings: settings.Runtime);

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("политику исключения секретов", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"version":5,"version":5,"settingsSchemaVersion":1,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""")]
    [InlineData("""{"version":5,"settingsSchemaVersion":1,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false,"untrackedSetting":true}""")]
    public void VersionFiveRejectsAmbiguousOrExtendedManifest(string manifest)
    {
        var settings = CurrentSettings();
        using var archive = CreateArchive(
            manifest,
            includeBackupRuns: true,
            includeValidationRuns: true,
            includeSettings: true,
            collectorSettings: settings.Collector,
            backupSettings: settings.Backup,
            runtimeSettings: settings.Runtime);

        Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));
    }

    [Fact]
    public void CurrentManifestRequiresValidationAuditHistory()
    {
        using var archive = CreateArchive(
            """{"version":4,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""",
            includeBackupRuns: true,
            includeSettings: true);

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("database/validation-runs.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentManifestRequiresBackupAuditHistory()
    {
        using var archive = CreateArchive(
            """{"version":3,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""",
            includeSettings: true);

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("database/backup-runs.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentManifestRequiresCompleteSettingsSnapshot()
    {
        using var archive = CreateArchive(
            """{"version":3,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""",
            includeBackupRuns: true,
            includeSettings: true,
            omittedEntry: "settings/runtime.json");

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("settings/runtime.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnexpectedEntryDespiteNoSecretsManifest()
    {
        using var archive = CreateArchive(
            """{"version":2,"secretsIncluded":false}""",
            unexpectedEntry: "settings/secrets.json");

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("вне разрешённой схемы", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsOversizedSettingsEntry()
    {
        using var archive = CreateArchive(
            """{"version":3,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""",
            includeBackupRuns: true,
            includeSettings: true,
            runtimeSettings: new string('x', 1024 * 1024 + 1));

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("превышает", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsHighlyCompressedDatabaseEntryBeforeRestoreTransaction()
    {
        // Два МиБ пробелов сжимаются в несколько КиБ и имитируют deflate ZIP-bomb,
        // не выделяя гигабайты памяти в unit-тесте.
        using var archive = CreateArchive(
            """{"version":2,"secretsIncluded":false}""",
            proxiesJson: new string(' ', 2 * 1024 * 1024) + "[]");

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("опасную степень ZIP-сжатия", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDatabaseEntryOverSixteenGibibytes()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BackupArchiveValidator.AccumulateValidatedEntrySize(
                "database/proxies.json", 16L * 1024 * 1024 * 1024 + 1, 16L * 1024 * 1024 * 1024, 0));

        Assert.Contains("16 ГиБ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAggregateUncompressedSizeOverThirtyTwoGibibytes()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BackupArchiveValidator.AccumulateValidatedEntrySize(
                "database/runs.json", 2, 2, 32L * 1024 * 1024 * 1024 - 1));

        Assert.Contains("32 ГиБ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateEntriesBeforeDatabaseReplacement()
    {
        using var stream = new MemoryStream();
        using (var writer = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(writer, "manifest.json", """{"version":2,"secretsIncluded":false}""");
            AddEntry(writer, "database/proxies.json", "[]");
            AddEntry(writer, "database/proxies.json", "[]");
            AddEntry(writer, "database/sources.json", "[]");
            AddEntry(writer, "database/runs.json", "[]");
        }
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("повторяющийся", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"version":2,"secretsIncluded":true}""")]
    [InlineData("""{"version":3,"createdAt":"not-a-date","secretsIncluded":false}""")]
    [InlineData("""{"version":4,"secretsIncluded":false}""")]
    [InlineData("""{"version":5,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""")]
    [InlineData("""{"version":5,"settingsSchemaVersion":2,"createdAt":"2026-08-09T10:00:00Z","secretsIncluded":false}""")]
    public void RejectsUnsafeOrUnsupportedManifest(string manifest)
    {
        using var archive = CreateArchive(manifest);

        Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));
    }

    private static ZipArchive CreateArchive(
        string manifest,
        bool includeBackupRuns = false,
        bool includeValidationRuns = false,
        bool includeSettings = false,
        bool includeIdentity = false,
        string? omittedEntry = null,
        string? unexpectedEntry = null,
        string collectorSettings = "{}",
        string backupSettings = "{}",
        string runtimeSettings = "{}",
        string proxiesJson = "[]")
    {
        var stream = new MemoryStream();
        using (var writer = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(writer, "manifest.json", manifest);
            AddEntry(writer, "database/proxies.json", proxiesJson);
            AddEntry(writer, "database/sources.json", "[]");
            AddEntry(writer, "database/runs.json", "[]");
            if (includeBackupRuns) AddEntry(writer, "database/backup-runs.json", "[]");
            if (includeValidationRuns) AddEntry(writer, "database/validation-runs.json", "[]");
            if (includeIdentity)
            {
                AddEntry(writer, "database/users.json", "[]");
                AddEntry(writer, "database/roles.json", "[]");
                AddEntry(writer, "database/user-roles.json", "[]");
                AddEntry(writer, "database/subscriptions.json", "[]");
            }
            if (includeSettings)
            {
                if (omittedEntry != "settings/collector.json") AddEntry(writer, "settings/collector.json", collectorSettings);
                if (omittedEntry != "settings/backup.json") AddEntry(writer, "settings/backup.json", backupSettings);
                if (omittedEntry != "settings/runtime.json") AddEntry(writer, "settings/runtime.json", runtimeSettings);
            }
            if (unexpectedEntry is not null) AddEntry(writer, unexpectedEntry, "top-secret");
        }
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static (string Collector, string Backup, string Runtime) CurrentSettings()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return (
            JsonSerializer.Serialize(new CollectorOptions(), jsonOptions),
            JsonSerializer.Serialize(
                BackupSettingsSnapshot.FromOptions(new BackupOptions(), telegramConfigured: false), jsonOptions),
            JsonSerializer.Serialize(new BackupRuntimeSettings(
                [], [], "*", new Dictionary<string, string?> { ["Default"] = "Information" },
                AdminApiKeyConfigured: true,
                AdminApiKeyIncluded: false,
                ConnectionStringConfigured: true,
                ConnectionStringIncluded: false), jsonOptions));
    }

    private static void AddEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(contents);
    }
}
