namespace ProxyHarbor.Tests;

public sealed class BackupInventoryTests
{
    [Fact]
    public void ClassifiesLegacyRowsWithoutClaimingBackupIntegrity()
    {
        const string good = "proxyharbor-20260924-123456-1234.phbackup";
        const string badSize = "proxyharbor-20260924-123457-1234.phbackup";
        const string missing = "proxyharbor-20260924-123458-1234.phbackup";
        const string duplicate = "proxyharbor-20260924-123459-1234.phbackup";
        const string linked = "proxyharbor-20260924-123500-1234.phbackup";
        const string orphan = "proxyharbor-20260924-123501-1234.phbackup";
        var runs = new[]
        {
            Run(good, 10), Run(badSize, 10), Run(missing, 10),
            Run(duplicate, 10), Run(duplicate, 10), Run(linked, 10),
            Run("../private.phbackup", 10), Run(null, 0), Run(good, 10, "failed")
        };
        var files = new[]
        {
            new LocalBackupFile(good, 10), new LocalBackupFile(badSize, 11),
            new LocalBackupFile(duplicate, 10), new LocalBackupFile(linked, null),
            new LocalBackupFile(orphan, 20), new LocalBackupFile("other.phbackup", 8)
        };

        var report = BackupInventoryApplication.Analyze(runs, files);

        Assert.Equal("metadata-only-unverified", report.Assurance);
        Assert.Equal("duplicate-run-name", report.Runs[0].State);
        Assert.Equal("size-mismatch", report.Runs[1].State);
        Assert.Equal("missing-local-file", report.Runs[2].State);
        Assert.Equal("duplicate-run-name", report.Runs[3].State);
        Assert.Equal("duplicate-run-name", report.Runs[4].State);
        Assert.Equal("linked-file-skipped", report.Runs[5].State);
        Assert.Equal("invalid-name", report.Runs[6].State);
        Assert.Null(report.Runs[6].FileName);
        Assert.Equal("invalid-name", report.Runs[7].State);
        Assert.Equal("not-completed", report.Runs[8].State);
        Assert.Equal([orphan], report.OrphanFiles);
        Assert.Equal(1, report.IgnoredFiles);
        Assert.Empty(report.CiphertextHashes);
    }

    [Fact]
    public void ExactNameAndSizeRemainUnverified()
    {
        const string name = "proxyharbor-20260924-123456-1234.phbackup";
        var report = BackupInventoryApplication.Analyze(
            [Run(name, 10)], [new LocalBackupFile(name, 10)]);
        Assert.Equal("name-size-match-unverified", Assert.Single(report.Runs).State);
        Assert.Empty(report.CiphertextHashes);
    }

    [Fact]
    public async Task OptionalHashReadsOnlyCanonicalLocalCiphertext()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-inventory-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string name = "proxyharbor-20260924-123456-1234.phbackup";
            await File.WriteAllBytesAsync(Path.Combine(directory, name), "abc"u8.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(directory, "other.phbackup"), "secret"u8.ToArray());

            var files = await BackupInventoryApplication.ReadLocalFilesAsync(
                directory, true, CancellationToken.None);
            var report = BackupInventoryApplication.Analyze([Run(name, 3)], files);

            Assert.Equal("local-ciphertext-hashes-unverified", report.Assurance);
            Assert.Equal("name-size-match-unverified", Assert.Single(report.Runs).State);
            var hash = Assert.Single(report.CiphertextHashes);
            Assert.Equal(name, hash.FileName);
            Assert.Equal(3, hash.SizeBytes);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash.Sha256);
            Assert.Equal(1, report.IgnoredFiles);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HashModeSkipsSymbolicLinks()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-inventory-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "private.txt");
            await File.WriteAllBytesAsync(target, "secret"u8.ToArray());
            var link = Path.Combine(directory, "proxyharbor-20260924-123456-1234.phbackup");
            File.CreateSymbolicLink(link, target);

            var files = await BackupInventoryApplication.ReadLocalFilesAsync(
                directory, true, CancellationToken.None);
            var file = Assert.Single(files);
            Assert.Null(file.SizeBytes);
            Assert.Null(file.Sha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LegacyBackupRun Run(string? name, long bytes, string status = "completed") =>
        new(Guid.NewGuid(), status, name, bytes, false, false);
}
