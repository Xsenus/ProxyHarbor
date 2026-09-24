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
    }

    [Fact]
    public void ExactNameAndSizeRemainUnverified()
    {
        const string name = "proxyharbor-20260924-123456-1234.phbackup";
        var report = BackupInventoryApplication.Analyze(
            [Run(name, 10)], [new LocalBackupFile(name, 10)]);
        Assert.Equal("name-size-match-unverified", Assert.Single(report.Runs).State);
    }

    private static LegacyBackupRun Run(string? name, long bytes, string status = "completed") =>
        new(Guid.NewGuid(), status, name, bytes, false, false);
}
