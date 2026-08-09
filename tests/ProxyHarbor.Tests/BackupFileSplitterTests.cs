using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет, что Telegram-части без потерь собираются в исходный зашифрованный поток.</summary>
public sealed class BackupFileSplitterTests
{
    [Fact]
    public void OrphanCleanupRemovesOnlyIncompleteBackupArtifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var complete = Path.Combine(directory, "proxyharbor-20260809.phbackup");
        var unrelated = Path.Combine(directory, "keep.zip");
        var orphans = new[]
        {
            Path.Combine(directory, "proxyharbor-20260809.zip"),
            Path.Combine(directory, "proxyharbor-20260809.phbackup.partial"),
            Path.Combine(directory, "proxyharbor-20260809.phbackup.part001-of-003")
        };
        try
        {
            File.WriteAllText(complete, "encrypted");
            File.WriteAllText(unrelated, "unrelated");
            foreach (var orphan in orphans) File.WriteAllText(orphan, "incomplete");

            var removed = BackupService.DeleteOrphanArtifacts(directory);

            Assert.Equal(orphans.Length, removed);
            Assert.All(orphans, path => Assert.False(File.Exists(path)));
            Assert.True(File.Exists(complete));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SplitAsyncProducesOrderedTemporaryPartsAndRemovesThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "backup.phbackup");
        var payload = Enumerable.Range(0, 2_500_123).Select(x => (byte)(x % 251)).ToArray();
        await File.WriteAllBytesAsync(source, payload);
        var observedPaths = new List<string>();
        await using var reconstructed = new MemoryStream();

        try
        {
            await foreach (var part in BackupFileSplitter.SplitAsync(source, 1_000_000, CancellationToken.None))
            {
                observedPaths.Add(part.Path);
                Assert.Equal(3, part.Total);
                await using var stream = File.OpenRead(part.Path);
                await stream.CopyToAsync(reconstructed);
            }

            Assert.Equal(payload, reconstructed.ToArray());
            Assert.Equal(3, observedPaths.Count);
            Assert.All(observedPaths, path => Assert.False(File.Exists(path)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
