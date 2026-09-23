using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
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

    [Fact]
    public async Task MaterializationPublishesOnlyMatchingBodyAndLeavesNoPartialFile()
    {
        var bytes = new byte[300_000];
        RandomNumberGenerator.Fill(bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-s3-materialize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, "restored.phbackup");
        var partialPath = Path.Combine(directory, ".restored.partial");
        try
        {
            var result = await S3BackupObjectStorageTransport.CopyVerifyAndPublishAsync(
                new MemoryStream(bytes), partialPath, finalPath, bytes.Length, hash, CancellationToken.None);

            Assert.Equal(finalPath, result.Path);
            Assert.Equal(bytes.Length, result.SizeBytes);
            Assert.Equal(hash, result.Sha256);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(finalPath));
            Assert.False(File.Exists(partialPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MaterializationMismatchIsQuarantinedByDeletion()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-s3-mismatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, "restored.phbackup");
        var partialPath = Path.Combine(directory, ".restored.partial");
        try
        {
            var exception = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
                S3BackupObjectStorageTransport.CopyVerifyAndPublishAsync(
                    new MemoryStream(bytes),
                    partialPath,
                    finalPath,
                    bytes.Length,
                    new string('0', 64),
                    CancellationToken.None));

            Assert.Equal(BackupDestinationErrorCode.IntegrityMismatch, exception.Failure.Code);
            Assert.False(File.Exists(finalPath));
            Assert.False(File.Exists(partialPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", BackupDestinationErrorCode.AuthorizationFailed,
        BackupDestinationFailureDisposition.Permanent)]
    [InlineData(HttpStatusCode.TooManyRequests, "SlowDown", BackupDestinationErrorCode.RateLimited,
        BackupDestinationFailureDisposition.UnknownOutcome)]
    [InlineData(HttpStatusCode.PreconditionFailed, "PreconditionFailed", BackupDestinationErrorCode.Collision,
        BackupDestinationFailureDisposition.Permanent)]
    [InlineData(HttpStatusCode.Conflict, "ConditionalRequestConflict", BackupDestinationErrorCode.Collision,
        BackupDestinationFailureDisposition.Permanent)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", BackupDestinationErrorCode.UnknownOutcome,
        BackupDestinationFailureDisposition.UnknownOutcome)]
    public void S3FailuresMapToStableSafeContract(
        HttpStatusCode status,
        string errorCode,
        BackupDestinationErrorCode expectedCode,
        BackupDestinationFailureDisposition expectedDisposition)
    {
        var failure = S3BackupObjectStorageTransport.ClassifyProviderFailure(
            new AmazonS3Exception("provider detail") { StatusCode = status, ErrorCode = errorCode },
            BackupDestinationOperation.Put);

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(expectedDisposition, failure.Disposition);
    }

    [Fact]
    public void LostPutResponseRequiresReconciliationButReadFailureCanRetry()
    {
        var put = S3BackupObjectStorageTransport.ClassifyProviderFailure(
            new HttpRequestException("request URI may contain secrets"),
            BackupDestinationOperation.Put);
        var read = S3BackupObjectStorageTransport.ClassifyProviderFailure(
            new HttpRequestException("request URI may contain secrets"),
            BackupDestinationOperation.Materialize);

        Assert.Equal(BackupDestinationErrorCode.UnknownOutcome, put.Code);
        Assert.Equal(BackupDestinationFailureDisposition.UnknownOutcome, put.Disposition);
        Assert.Equal(BackupDestinationErrorCode.Unavailable, read.Code);
        Assert.Equal(BackupDestinationFailureDisposition.Retryable, read.Disposition);
    }

    [Fact]
    public void PutRequestUsesConditionalCreateAndContentIdentity()
    {
        var hash = new string('a', 64);
        var request = S3BackupObjectStorageTransport.CreatePutRequest(
            new FileInfo("snapshot.phbackup"), "safe/snapshot.phbackup", hash, "backup");

        Assert.Equal("*", request.IfNoneMatch);
        Assert.Equal("safe/snapshot.phbackup", request.Key);
        Assert.Equal(hash, request.Metadata["sha256"]);
        Assert.Equal("PHB3", request.Metadata["format"]);
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
