using System.IO.Compression;
using System.Text;
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
        using var archive = CreateArchive("""{"version":3,"secretsIncluded":false}""", includeBackupRuns: true);

        BackupArchiveValidator.Validate(archive);
    }

    [Fact]
    public void CurrentManifestRequiresBackupAuditHistory()
    {
        using var archive = CreateArchive("""{"version":3,"secretsIncluded":false}""");

        var exception = Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));

        Assert.Contains("database/backup-runs.json", exception.Message, StringComparison.Ordinal);
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
    [InlineData("""{"version":4,"secretsIncluded":false}""")]
    public void RejectsUnsafeOrUnsupportedManifest(string manifest)
    {
        using var archive = CreateArchive(manifest);

        Assert.Throws<InvalidDataException>(() => BackupArchiveValidator.Validate(archive));
    }

    private static ZipArchive CreateArchive(string manifest, bool includeBackupRuns = false)
    {
        var stream = new MemoryStream();
        using (var writer = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(writer, "manifest.json", manifest);
            AddEntry(writer, "database/proxies.json", "[]");
            AddEntry(writer, "database/sources.json", "[]");
            AddEntry(writer, "database/runs.json", "[]");
            if (includeBackupRuns) AddEntry(writer, "database/backup-runs.json", "[]");
        }
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static void AddEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(contents);
    }
}
