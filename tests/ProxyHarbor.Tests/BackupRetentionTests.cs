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
                var path = Path.Combine(directory, $"proxyharbor-{index:D2}.phbackup");
                File.WriteAllBytes(path, [1]);
                File.SetLastWriteTimeUtc(path, now.AddMinutes(-index));
            }

            BackupService.ApplyRetention(directory, retentionDays: 7, intervalHours: 24);

            var retained = Directory.GetFiles(directory, "*.phbackup")
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(9, retained.Length);
            Assert.Contains("proxyharbor-00.phbackup", retained);
            Assert.DoesNotContain("proxyharbor-19.phbackup", retained);
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
            var expired = Path.Combine(directory, "proxyharbor-expired.phbackup");
            var fresh = Path.Combine(directory, "proxyharbor-fresh.phbackup");
            File.WriteAllBytes(expired, [1]);
            File.WriteAllBytes(fresh, [2]);
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));

            BackupService.ApplyRetention(directory, retentionDays: 7, intervalHours: 24);

            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(fresh));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
