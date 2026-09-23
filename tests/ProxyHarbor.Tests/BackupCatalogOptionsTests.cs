using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class BackupCatalogOptionsTests
{
    private const string Key = "synthetic-catalog-signing-key-32-chars";

    [Fact]
    public async Task OfflineInspectVerifiesCatalogWithoutDatabase()
    {
        var directory = NewDirectory();
        try
        {
            var keyFile = Path.Combine(directory, "key.secret");
            var catalog = Path.Combine(directory, "inventory.catalog.json");
            await File.WriteAllTextAsync(keyFile, Key);
            await File.WriteAllBytesAsync(catalog, BackupCatalogService.Seal(Snapshot(), Key));
            var options = new BackupCatalogOptions(
                "inspect", null, catalog, null, keyFile, null, "legacy");

            var result = await BackupCatalogApplication.RunAsync(options, CancellationToken.None);

            Assert.Equal(0, result);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OfflineInspectRejectsTamperingWithoutDatabase()
    {
        var directory = NewDirectory();
        try
        {
            var keyFile = Path.Combine(directory, "key.secret");
            var catalog = Path.Combine(directory, "inventory.catalog.json");
            await File.WriteAllTextAsync(keyFile, Key);
            var bytes = BackupCatalogService.Seal(Snapshot(), Key);
            bytes[^20] ^= 1;
            await File.WriteAllBytesAsync(catalog, bytes);
            var options = new BackupCatalogOptions(
                "inspect", null, catalog, null, keyFile, null, "legacy");

            Assert.Equal(1, await BackupCatalogApplication.RunAsync(options, CancellationToken.None));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExportWriterNeverOverwritesAndCleansCancelledPartial()
    {
        var directory = NewDirectory();
        try
        {
            var output = Path.Combine(directory, "inventory.catalog.json");
            await BackupCatalogApplication.WriteNewFileAsync(output, [1, 2, 3], CancellationToken.None);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(output));
            await Assert.ThrowsAsync<IOException>(() =>
                BackupCatalogApplication.WriteNewFileAsync(output, [9], CancellationToken.None));
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(output));

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BackupCatalogApplication.WriteNewFileAsync(
                    Path.Combine(directory, "cancelled.catalog.json"), [1, 2, 3], cancelled.Token));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("--connection", "Host=evil")]
    [InlineData("--encryption-key", "inline-secret")]
    [InlineData("--endpoint", "https://evil.invalid")]
    public void ParserRejectsInlineSecretsAndProviderUrls(string option, string value)
    {
        Assert.Throws<ArgumentException>(() => BackupCatalogOptions.Parse([
            "inspect", option, value]));
    }

    private static BackupCatalogSnapshot Snapshot()
    {
        var finished = DateTimeOffset.UtcNow.AddMinutes(-2);
        return new BackupCatalogSnapshot(
            Guid.NewGuid(), "snapshot.phbackup", 5, new string('a', 64), 1,
            finished, DateTimeOffset.UtcNow, "legacy",
            [new BackupCatalogCopy(Guid.NewGuid(), Guid.NewGuid(), "s3",
                "safe/snapshot.phbackup", 1, finished.AddMinutes(1))]);
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backup-catalog-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
