using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
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

    [Fact]
    public void CatalogPutRequestIsBoundedConditionalAndContentAddressed()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"format\":\"test\"}");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = S3BackupObjectStorageTransport.BuildCatalogObjectKey(
            "safe/snapshot.phbackup");
        var request = S3BackupObjectStorageTransport.CreateCatalogPutRequest(
            bytes, key, hash, "private-bucket");

        Assert.Equal("safe/snapshot.phbackup.catalog.v1.json", key);
        Assert.Equal("*", request.IfNoneMatch);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(hash, request.Metadata["sha256"]);
        Assert.Equal("ProxyHarbor.BackupCatalog.v1", request.Metadata["format"]);
        Assert.Equal(Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(bytes)),
            request.ChecksumSHA256);
        using var stream = request.InputStream;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Theory]
    [InlineData("../snapshot.phbackup")]
    [InlineData("safe//snapshot.phbackup")]
    [InlineData("safe/snapshot.phbackup?secret=1")]
    [InlineData("safe/snapshot.zip")]
    [InlineData("safe/снимок.phbackup")]
    public void CatalogObjectKeyRejectsUnsafeLocator(string locator)
    {
        Assert.Throws<ArgumentException>(() =>
            S3BackupObjectStorageTransport.BuildCatalogObjectKey(locator));
    }

    [Fact]
    public void CatalogObjectKeyRejectsOverlongLocator()
    {
        Assert.Throws<ArgumentException>(() =>
            S3BackupObjectStorageTransport.BuildCatalogObjectKey(
                new string('a', 1010) + ".phbackup"));
    }

    [Fact]
    public async Task CatalogPublicationCreatesOnlyAfterNotFoundAndVerifiesByHead()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":1}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var client = new StubS3Client();
        client.Body = bytes;
        client.Head = _ => client.HeadCalls == 1
            ? Task.FromException<GetObjectMetadataResponse>(NotFound())
            : Task.FromResult(Metadata(bytes.Length, hash));
        client.Put = request =>
        {
            Assert.Equal("*", request.IfNoneMatch);
            Assert.Equal("safe/snapshot.phbackup.catalog.v1.json", request.Key);
            return Task.FromResult(new PutObjectResponse());
        };
        var transport = new S3BackupObjectStorageTransport(_ => client);

        var result = await transport.PublishCatalogAsync(
            "safe/snapshot.phbackup", bytes, ValidOptions(), CancellationToken.None);

        Assert.Equal(hash, result.Sha256);
        Assert.Equal(2, client.HeadCalls);
        Assert.Equal(1, client.PutCalls);
    }

    [Fact]
    public async Task ExistingMatchingCatalogNeverWritesAgain()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":1}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var client = new StubS3Client
        {
            Head = _ => Task.FromResult(Metadata(bytes.Length, hash)),
            Body = bytes
        };
        var transport = new S3BackupObjectStorageTransport(_ => client);

        _ = await transport.PublishCatalogAsync(
            "safe/snapshot.phbackup", bytes, ValidOptions(), CancellationToken.None);

        Assert.Equal(0, client.PutCalls);
        Assert.Equal(1, client.HeadCalls);
    }

    [Fact]
    public async Task ExistingDifferentCatalogFailsWithoutOverwrite()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":1}");
        var client = new StubS3Client
        {
            Head = _ => Task.FromResult(Metadata(bytes.Length, new string('f', 64)))
        };
        var transport = new S3BackupObjectStorageTransport(_ => client);

        var failure = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            transport.PublishCatalogAsync(
                "safe/snapshot.phbackup", bytes, ValidOptions(), CancellationToken.None));

        Assert.Equal(BackupDestinationErrorCode.Collision, failure.Failure.Code);
        Assert.Equal(0, client.PutCalls);
    }

    [Fact]
    public async Task LostHeadAfterPutIsUnknownNotVerified()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":1}");
        var client = new StubS3Client();
        client.Body = bytes;
        client.Head = _ => client.HeadCalls == 1
            ? Task.FromException<GetObjectMetadataResponse>(NotFound())
            : Task.FromException<GetObjectMetadataResponse>(new HttpRequestException("fixture unavailable"));
        client.Put = _ =>
        {
            return Task.FromResult(new PutObjectResponse());
        };
        var transport = new S3BackupObjectStorageTransport(_ => client);

        var failure = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            transport.PublishCatalogAsync(
                "safe/snapshot.phbackup", bytes, ValidOptions(), CancellationToken.None));

        Assert.Equal(BackupDestinationErrorCode.UnknownOutcome, failure.Failure.Code);
        Assert.Equal(1, client.PutCalls);
    }

    [Fact]
    public async Task ConcurrentIdenticalCatalogPublicationReconcilesCollision()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":1}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var client = new StubS3Client();
        client.Body = bytes;
        client.Head = _ => client.HeadCalls == 1
            ? Task.FromException<GetObjectMetadataResponse>(NotFound())
            : Task.FromResult(Metadata(bytes.Length, hash));
        client.Put = _ => Task.FromException<PutObjectResponse>(new AmazonS3Exception("fixture collision")
        {
            StatusCode = HttpStatusCode.PreconditionFailed
        });
        var transport = new S3BackupObjectStorageTransport(_ => client);

        var result = await transport.PublishCatalogAsync(
            "safe/snapshot.phbackup", bytes, ValidOptions(), CancellationToken.None);

        Assert.Equal(hash, result.Sha256);
        Assert.Equal(2, client.HeadCalls);
        Assert.Equal(1, client.PutCalls);
    }

    [Fact]
    public async Task MismatchAfterPutNeverReportsSuccess()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":1}");
        var client = new StubS3Client();
        client.Head = _ => client.HeadCalls == 1
            ? Task.FromException<GetObjectMetadataResponse>(NotFound())
            : Task.FromResult(Metadata(bytes.Length, new string('f', 64)));
        var transport = new S3BackupObjectStorageTransport(_ => client);

        var failure = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            transport.PublishCatalogAsync(
                "safe/snapshot.phbackup", bytes, ValidOptions(), CancellationToken.None));

        Assert.Equal(BackupDestinationErrorCode.IntegrityMismatch, failure.Failure.Code);
        Assert.Equal(1, client.PutCalls);
    }

    [Fact]
    public async Task MatchingMetadataWithCorruptBodyIsCollision()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":1}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var client = new StubS3Client
        {
            Head = _ => Task.FromResult(Metadata(bytes.Length, hash)),
            Body = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":2}")
        };
        var transport = new S3BackupObjectStorageTransport(_ => client);

        var failure = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            transport.PublishCatalogAsync(
                "safe/snapshot.phbackup", bytes, ValidOptions(), CancellationToken.None));

        Assert.Equal(BackupDestinationErrorCode.Collision, failure.Failure.Code);
        Assert.Equal(0, client.PutCalls);
        Assert.Equal(1, client.GetCalls);
    }

    [Fact]
    public async Task ExistingCatalogWithOversizedBodyIsRejectedBeforePut()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":1}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var client = new StubS3Client
        {
            Head = _ => Task.FromResult(Metadata(bytes.Length, hash)),
            Get = _ => Task.FromResult(new GetObjectResponse
            {
                ContentLength = bytes.Length,
                ResponseStream = new MemoryStream([.. bytes, 1])
            })
        };
        var transport = new S3BackupObjectStorageTransport(_ => client);

        var failure = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            transport.PublishCatalogAsync(
                "safe/snapshot.phbackup", bytes, ValidOptions(), CancellationToken.None));

        Assert.Equal(BackupDestinationErrorCode.Collision, failure.Failure.Code);
        Assert.Equal(0, client.PutCalls);
    }

    [Fact]
    public async Task GetFailureAfterPutIsUnknownAndSanitized()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"catalog\":1}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var client = new StubS3Client();
        client.Head = _ => client.HeadCalls == 1
            ? Task.FromException<GetObjectMetadataResponse>(NotFound())
            : Task.FromResult(Metadata(bytes.Length, hash));
        client.Get = _ => Task.FromException<GetObjectResponse>(
            new HttpRequestException("https://s3.example.invalid/secret-path"));
        var transport = new S3BackupObjectStorageTransport(_ => client);

        var failure = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            transport.PublishCatalogAsync(
                "safe/snapshot.phbackup", bytes, ValidOptions(), CancellationToken.None));

        Assert.Equal(BackupDestinationErrorCode.UnknownOutcome, failure.Failure.Code);
        Assert.DoesNotContain("secret-path", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.PutCalls);
    }

    private static AmazonS3Exception NotFound() => new("fixture missing")
    {
        StatusCode = HttpStatusCode.NotFound
    };

    private static GetObjectMetadataResponse Metadata(long size, string hash)
    {
        var response = new GetObjectMetadataResponse { ContentLength = size };
        response.Metadata["sha256"] = hash;
        return response;
    }

    private sealed class StubS3Client() : AmazonS3Client(
        new BasicAWSCredentials("fixture-access", "fixture-secret"),
        new AmazonS3Config { ServiceURL = "https://s3.example.invalid" })
    {
        public Func<GetObjectMetadataRequest, Task<GetObjectMetadataResponse>> Head { get; set; } =
            _ => Task.FromException<GetObjectMetadataResponse>(NotFound());
        public Func<PutObjectRequest, Task<PutObjectResponse>> Put { get; set; } =
            _ => Task.FromResult(new PutObjectResponse());
        public Func<GetObjectRequest, Task<GetObjectResponse>>? Get { get; set; }
        public int HeadCalls { get; set; }
        public int PutCalls { get; set; }
        public int GetCalls { get; private set; }
        public byte[] Body { get; set; } = [];

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(
            GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            HeadCalls++;
            return Head(request);
        }

        public override Task<PutObjectResponse> PutObjectAsync(
            PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            PutCalls++;
            return Put(request);
        }

        public override Task<GetObjectResponse> GetObjectAsync(
            GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            if (Get is not null) return Get(request);
            return Task.FromResult(new GetObjectResponse
            {
                ContentLength = Body.Length,
                ResponseStream = new MemoryStream(Body, writable: false)
            });
        }
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
