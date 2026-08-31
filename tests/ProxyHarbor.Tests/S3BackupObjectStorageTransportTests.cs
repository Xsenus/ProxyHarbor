using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class S3BackupObjectStorageTransportTests
{
    [Fact]
    public void RussianS3CompatibleConfigurationPassesStrictValidation()
    {
        var options = ValidOptions();

        Assert.True(BackupOptions.IsObjectStorageConfigurationValid(options));
        Assert.Equal("proxyharbor/backups/archive.phbackup",
            S3BackupObjectStorageTransport.BuildObjectKey(options.ObjectStoragePrefix, "archive.phbackup"));
    }

    [Theory]
    [InlineData("http://storage.example.test", "proxyharbor/backups")]
    [InlineData("https://user:pass@storage.example.test", "proxyharbor/backups")]
    [InlineData("https://storage.example.test", "../backups")]
    [InlineData("https://storage.example.test", "proxyharbor\\backups")]
    public void UnsafeEndpointOrPrefixIsRejected(string endpoint, string prefix)
    {
        var options = ValidOptions();
        options.ObjectStorageEndpoint = endpoint;
        options.ObjectStoragePrefix = prefix;

        Assert.False(BackupOptions.IsObjectStorageConfigurationValid(options));
    }

    [Fact]
    public void EmptyPrefixKeepsCanonicalFileNameAtBucketRoot()
    {
        Assert.Equal("archive.phbackup",
            S3BackupObjectStorageTransport.BuildObjectKey(string.Empty, "archive.phbackup"));
    }

    private static BackupOptions ValidOptions() => new()
    {
        SendToObjectStorage = true,
        ObjectStorageEndpoint = "https://storage.yandexcloud.net",
        ObjectStorageRegion = "ru-central1",
        ObjectStorageBucket = "proxyharbor-backups",
        ObjectStoragePrefix = "proxyharbor/backups",
        ObjectStorageUsePathStyle = true,
        ObjectStorageAccessKey = "test-access-key",
        ObjectStorageSecretKey = "test-secret-key"
    };
}
