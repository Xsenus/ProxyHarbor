using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет age/count bounds постоянного backup volume.</summary>
public sealed class BackupRetentionTests
{
    [Fact]
    public void RecoveryRetriesCannotGrowDailyBackupSetWithoutBound()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTime.UtcNow;
            for (var index = 0; index < 20; index++)
            {
                var path = Path.Combine(directory, $"proxyharbor-20260810-120000-{index:D4}.phbackup");
                File.WriteAllBytes(path, [1]);
                File.SetLastWriteTimeUtc(path, now.AddMinutes(-index));
            }

            BackupService.ApplyRetention(directory, retentionDays: 7, intervalHours: 24);

            var retained = Directory.GetFiles(directory, "*.phbackup")
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(9, retained.Length);
            Assert.Contains("proxyharbor-20260810-120000-0000.phbackup", retained);
            Assert.DoesNotContain("proxyharbor-20260810-120000-0019.phbackup", retained);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ExpiredFilesAreRemovedEvenBelowCountLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var expired = Path.Combine(directory, "proxyharbor-20260801-120000-0000.phbackup");
            var fresh = Path.Combine(directory, "proxyharbor-20260810-120000-0000.phbackup");
            File.WriteAllBytes(expired, [1]);
            File.WriteAllBytes(fresh, [2]);
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));

            BackupService.ApplyRetention(directory, retentionDays: 7, intervalHours: 24);

            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(fresh));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ManualAndMalformedNeighborArchivesAreNeverOwnedByRetention()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var manual = Path.Combine(directory, "manual.phbackup");
            var prefixedButNotGenerated = Path.Combine(directory, "proxyharbor-manual.phbackup");
            File.WriteAllBytes(manual, [1]);
            File.WriteAllBytes(prefixedButNotGenerated, [2]);
            File.SetLastWriteTimeUtc(manual, DateTime.UtcNow.AddYears(-1));
            File.SetLastWriteTimeUtc(prefixedButNotGenerated, DateTime.UtcNow.AddYears(-1));
            for (var index = 0; index < 5; index++)
                File.WriteAllBytes(
                    Path.Combine(directory, $"proxyharbor-20260810-130000-{index:D4}.phbackup"), [3]);

            BackupService.ApplyRetention(directory, retentionDays: 1, intervalHours: 24);

            Assert.True(File.Exists(manual));
            Assert.True(File.Exists(prefixedButNotGenerated));
            Assert.Equal(3, Directory.GetFiles(directory, "proxyharbor-20260810-*.phbackup").Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
