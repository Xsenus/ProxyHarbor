using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует сохранение primary exception при вторичном отказе удаления partial-файла.</summary>
public sealed class BackupFileCleanupTests
{
    [Fact]
    public void ExistingTemporaryFileIsDeleted()
    {
        var path = Path.GetTempFileName();

        var failure = BackupFileCleanup.TryDelete(path);

        Assert.Null(failure);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void MissingFileIsAlreadyClean()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.partial");

        Assert.Null(BackupFileCleanup.TryDelete(path));
    }

    [Fact]
    public void CleanupFailureIsAttachedWithoutReplacingPrimaryException()
    {
        var path = Path.GetTempFileName();
        var primary = new InvalidOperationException("primary pipeline failure");
        try
        {
            BackupFileCleanup.TryDeletePreservingPrimary(
                path,
                primary,
                _ => throw new IOException("secondary cleanup failure"));

            Assert.Equal(
                "IOException: secondary cleanup failure",
                primary.Data[BackupFileCleanup.FailureDataKey]);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
