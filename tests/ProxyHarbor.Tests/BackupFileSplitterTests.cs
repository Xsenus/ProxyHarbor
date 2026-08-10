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
        const string generatedStem = "proxyharbor-20260809-120000-0000";
        var complete = Path.Combine(directory, $"{generatedStem}.phbackup");
        var unrelated = Path.Combine(directory, "keep.zip");
        var orphans = new[]
        {
            Path.Combine(directory, $"{generatedStem}.zip"),
            Path.Combine(directory, $"{generatedStem}.phbackup.partial"),
            Path.Combine(directory, $"{generatedStem}.phbackup.part001-of-003"),
            Path.Combine(directory, $"{generatedStem}.phbackup.part1000-of-1000")
        };
        var similarButUnowned = new[]
        {
            Path.Combine(directory, "proxyharbor-manual.zip"),
            Path.Combine(directory, "proxyharbor-manual.phbackup.partial"),
            Path.Combine(directory, $"{generatedStem}.phbackup.part1-of-3"),
            Path.Combine(directory, $"{generatedStem}.phbackup.part0001-of-0003"),
            Path.Combine(directory, $"{generatedStem}.phbackup.part004-of-003"),
            Path.Combine(directory, $"{generatedStem}.phbackup.part001-of-001")
        };
        try
        {
            File.WriteAllText(complete, "encrypted");
            File.WriteAllText(unrelated, "unrelated");
            foreach (var orphan in orphans) File.WriteAllText(orphan, "incomplete");
            foreach (var neighbor in similarButUnowned) File.WriteAllText(neighbor, "manual");

            var removed = BackupService.DeleteOrphanArtifacts(directory);

            Assert.Equal(orphans.Length, removed);
            Assert.All(orphans, path => Assert.False(File.Exists(path)));
            Assert.All(similarButUnowned, path => Assert.True(File.Exists(path)));
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
            await foreach (var part in BackupFileSplitter.SplitAsync(
                source, 1_000_000, maximumParts: 20, CancellationToken.None))
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

    [Fact]
    public async Task SplitRejectsExcessivePartCountBeforeCreatingTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-split-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "backup.phbackup");
        await File.WriteAllBytesAsync(source, new byte[21]);
        try
        {
            var exception = await Assert.ThrowsAsync<BackupDeliveryPolicyException>(async () =>
            {
                await foreach (var _ in BackupFileSplitter.SplitAsync(
                    source, partLimit: 1, maximumParts: 20, CancellationToken.None))
                {
                }
            });

            Assert.Contains("21 Telegram-част", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.part*-of-*"));
            Assert.True(File.Exists(source));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(200, 10, 20)]
    public void RequiredPartCountUsesExactBoundaries(long length, long partLimit, int expected) =>
        Assert.Equal(expected, BackupFileSplitter.RequiredPartCount(length, partLimit, maximumParts: 20));

    [Fact]
    public void RequiredPartCountRejectsExtremeLengthWithOperationalErrorInsteadOfOverflow()
    {
        var exception = Assert.Throws<BackupDeliveryPolicyException>(() =>
            BackupFileSplitter.RequiredPartCount(long.MaxValue, partLimit: 1, maximumParts: 20));

        Assert.Contains("Telegram-част", exception.Message, StringComparison.Ordinal);
        Assert.StartsWith(
            BackupService.DeliveryPolicyErrorMarker,
            BackupService.FormatAuditError(exception),
            StringComparison.Ordinal);
    }
}
