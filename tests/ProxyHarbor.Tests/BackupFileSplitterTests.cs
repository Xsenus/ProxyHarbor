using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет, что Telegram-части без потерь собираются в исходный зашифрованный поток.</summary>
public sealed class BackupFileSplitterTests
{
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
