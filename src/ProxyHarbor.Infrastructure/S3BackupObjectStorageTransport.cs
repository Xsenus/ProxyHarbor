using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Доставляет только уже зашифрованные PHB3-архивы в S3-совместимый bucket.
/// Успех возвращается после HEAD-проверки размера и сохранённого SHA-256.
/// </summary>
public sealed class S3BackupObjectStorageTransport : IBackupObjectStorageTransport
{
    /// <inheritdoc />
    public async Task<string> UploadAndVerifyAsync(
        string path,
        BackupOptions options,
        CancellationToken token) =>
        (await UploadAndVerifyDetailedAsync(path, options, token)).ObjectKey;

    /// <inheritdoc />
    public async Task<BackupObjectStorageWriteResult> UploadAndVerifyDetailedAsync(
        string path,
        BackupOptions options,
        CancellationToken token)
    {
        ValidateOptions(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Зашифрованный backup-файл не найден.", path);
        var hash = await ComputeSha256Async(path, token);
        var key = BuildObjectKey(options.ObjectStoragePrefix, file.Name);

        using var client = CreateClient(options);
        var request = CreatePutRequest(file, key, hash, options.ObjectStorageBucket!);
        var put = await ExecuteProviderAsync(
            BackupDestinationOperation.Put,
            () => client.PutObjectAsync(request, token),
            token);
        BackupObjectStorageVerificationResult verified;
        try
        {
            verified = await VerifyAsync(client, key, file.Length, hash, options, token);
        }
        catch (BackupDestinationOperationException exception)
            when (exception.Failure.Code != BackupDestinationErrorCode.IntegrityMismatch)
        {
            // PUT уже получил provider response, но независимый HEAD не завершился.
            // Повторять PUT вслепую нельзя: durable worker сначала должен reconcile locator.
            throw Failure(
                BackupDestinationErrorCode.UnknownOutcome,
                BackupDestinationFailureDisposition.UnknownOutcome,
                "S3 PUT завершён, но независимая verification не подтверждена.");
        }
        catch (OperationCanceledException)
        {
            throw Failure(
                BackupDestinationErrorCode.UnknownOutcome,
                BackupDestinationFailureDisposition.UnknownOutcome,
                "S3 PUT завершён, но verification была прервана.");
        }
        return new BackupObjectStorageWriteResult(
            key,
            verified.SizeBytes,
            verified.Sha256,
            FirstValue(verified.VersionId, put.VersionId),
            FirstValue(verified.NativeChecksum, put.ChecksumSHA256),
            FirstValue(verified.EntityTag, put.ETag));
    }

    internal static PutObjectRequest CreatePutRequest(
        FileInfo file, string key, string hash, string bucket)
    {
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            FilePath = file.FullName,
            ContentType = "application/octet-stream",
            AutoCloseStream = true,
            // Не заменять существующий immutable locator даже при повторной доставке.
            IfNoneMatch = "*",
            // SHA-256 передаётся и как стандартный SDK checksum, и как совместимая
            // metadata: часть S3-compatible реализаций не возвращает native checksum.
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(hash))
        };
        request.Metadata["sha256"] = hash;
        request.Metadata["format"] = "PHB3";
        return request;
    }

    /// <inheritdoc />
    public async Task<BackupObjectStorageVerificationResult> VerifyAsync(
        string objectKey,
        long expectedSize,
        string expectedSha256,
        BackupOptions options,
        CancellationToken token)
    {
        ValidateOptions(options);
        using var client = CreateClient(options);
        return await VerifyAsync(client, objectKey, expectedSize, expectedSha256, options, token);
    }

    /// <inheritdoc />
    public async Task<BackupObjectStorageMaterializationResult> MaterializeAndVerifyAsync(
        string objectKey,
        string finalPath,
        long expectedSize,
        string expectedSha256,
        BackupOptions options,
        CancellationToken token)
    {
        ValidateOptions(options);
        ValidateExpected(objectKey, expectedSize, expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        var fullFinalPath = Path.GetFullPath(finalPath);
        var directory = Path.GetDirectoryName(fullFinalPath) ?? throw new InvalidOperationException(
            "Не удалось определить каталог materialized backup.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Каталог materialized backup не существует.");
        var partialPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullFinalPath)}.{Guid.NewGuid():N}.partial");

        using var client = CreateClient(options);
        try
        {
            using var response = await ExecuteProviderAsync(
                BackupDestinationOperation.Materialize,
                () => client.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = options.ObjectStorageBucket,
                    Key = objectKey
                }, token),
                token);
            var remoteHash = MetadataSha256(response.Metadata);
            if (response.ContentLength != expectedSize ||
                !string.IsNullOrWhiteSpace(remoteHash) &&
                !string.Equals(remoteHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw Failure(
                    BackupDestinationErrorCode.IntegrityMismatch,
                    BackupDestinationFailureDisposition.Permanent,
                    "S3 metadata не совпадает с ожидаемым размером или SHA-256.");
            var result = await CopyVerifyAndPublishAsync(
                response.ResponseStream,
                partialPath,
                fullFinalPath,
                expectedSize,
                expectedSha256,
                token);
            return new BackupObjectStorageMaterializationResult(
                result.Path,
                result.SizeBytes,
                result.Sha256,
                NullIfEmpty(response.VersionId),
                NullIfEmpty(response.ChecksumSHA256),
                NullIfEmpty(response.ETag));
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    internal static string BuildObjectKey(string prefix, string fileName)
    {
        var normalized = prefix.Trim('/');
        return string.IsNullOrEmpty(normalized) ? fileName : $"{normalized}/{fileName}";
    }

    internal static async Task<BackupObjectStorageMaterializationResult> CopyVerifyAndPublishAsync(
        Stream input,
        string partialPath,
        string finalPath,
        long expectedSize,
        string expectedSha256,
        CancellationToken token)
    {
        ValidateExpected("materialized-object", expectedSize, expectedSha256);
        if (File.Exists(finalPath))
            throw Failure(
                BackupDestinationErrorCode.Collision,
                BackupDestinationFailureDisposition.Permanent,
                "Локальный materialized backup уже существует.");
        try
        {
            await using var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(partialPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long copied = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, token);
                if (read == 0) break;
                copied = checked(copied + read);
                if (copied > expectedSize)
                    throw Failure(
                        BackupDestinationErrorCode.IntegrityMismatch,
                        BackupDestinationFailureDisposition.Permanent,
                        "S3 body превышает ожидаемый размер.");
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            await output.FlushAsync(token);
            var actualHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (copied != expectedSize ||
                !string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw Failure(
                    BackupDestinationErrorCode.IntegrityMismatch,
                    BackupDestinationFailureDisposition.Permanent,
                    "S3 body не прошёл проверку размера или SHA-256.");
            output.Close();
            try
            {
                File.Move(partialPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                throw Failure(
                    BackupDestinationErrorCode.Collision,
                    BackupDestinationFailureDisposition.Permanent,
                    "Локальный materialized backup уже существует.");
            }
            return new BackupObjectStorageMaterializationResult(
                finalPath, copied, actualHash, null, null, null);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    internal static BackupDestinationFailure ClassifyProviderFailure(
        Exception exception,
        BackupDestinationOperation operation)
    {
        if (exception is AmazonS3Exception s3)
        {
            var code = s3.ErrorCode;
            if (code is "InvalidAccessKeyId" or "InvalidToken" or "SignatureDoesNotMatch" ||
                s3.StatusCode == HttpStatusCode.Unauthorized)
                return new(BackupDestinationErrorCode.AuthenticationFailed,
                    BackupDestinationFailureDisposition.Permanent);
            if (code is "AccessDenied" or "AllAccessDisabled" || s3.StatusCode == HttpStatusCode.Forbidden)
                return new(BackupDestinationErrorCode.AuthorizationFailed,
                    BackupDestinationFailureDisposition.Permanent);
            if (code is "NoSuchBucket" or "InvalidBucketName" or "InvalidRequest")
                return new(BackupDestinationErrorCode.InvalidConfiguration,
                    BackupDestinationFailureDisposition.Permanent);
            if (code is "SlowDown" or "Throttling" || (int)s3.StatusCode == 429)
                return operation == BackupDestinationOperation.Put
                    ? new(BackupDestinationErrorCode.RateLimited,
                        BackupDestinationFailureDisposition.UnknownOutcome)
                    : new(BackupDestinationErrorCode.RateLimited,
                        BackupDestinationFailureDisposition.Retryable);
            if (s3.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
                return new(BackupDestinationErrorCode.Collision,
                    BackupDestinationFailureDisposition.Permanent);
            if (s3.StatusCode == HttpStatusCode.NotFound)
                return new(BackupDestinationErrorCode.NotFound,
                    BackupDestinationFailureDisposition.Permanent);
            if ((int)s3.StatusCode >= 500)
                return operation == BackupDestinationOperation.Put
                    ? new(BackupDestinationErrorCode.UnknownOutcome,
                        BackupDestinationFailureDisposition.UnknownOutcome)
                    : new(BackupDestinationErrorCode.Unavailable,
                        BackupDestinationFailureDisposition.Retryable);
            return new(BackupDestinationErrorCode.ProviderRejected,
                BackupDestinationFailureDisposition.Permanent);
        }
        if (exception is TimeoutException or TaskCanceledException)
            return operation == BackupDestinationOperation.Put
                ? new(BackupDestinationErrorCode.UnknownOutcome,
                    BackupDestinationFailureDisposition.UnknownOutcome)
                : new(BackupDestinationErrorCode.Timeout,
                    BackupDestinationFailureDisposition.Retryable);
        if (exception is HttpRequestException or IOException or AmazonServiceException)
            return operation == BackupDestinationOperation.Put
                ? new(BackupDestinationErrorCode.UnknownOutcome,
                    BackupDestinationFailureDisposition.UnknownOutcome)
                : new(BackupDestinationErrorCode.Unavailable,
                    BackupDestinationFailureDisposition.Retryable);
        return new(BackupDestinationErrorCode.ProviderRejected,
            BackupDestinationFailureDisposition.Permanent);
    }

    private static async Task<BackupObjectStorageVerificationResult> VerifyAsync(
        AmazonS3Client client,
        string objectKey,
        long expectedSize,
        string expectedSha256,
        BackupOptions options,
        CancellationToken token)
    {
        ValidateExpected(objectKey, expectedSize, expectedSha256);
        var metadata = await ExecuteProviderAsync(
            BackupDestinationOperation.Verify,
            () => client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = options.ObjectStorageBucket,
                Key = objectKey
            }, token),
            token);
        var remoteHash = MetadataSha256(metadata.Metadata);
        if (metadata.ContentLength != expectedSize ||
            !string.Equals(remoteHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw Failure(
                BackupDestinationErrorCode.IntegrityMismatch,
                BackupDestinationFailureDisposition.Permanent,
                "S3-объект не прошёл проверку размера или SHA-256.");
        return new BackupObjectStorageVerificationResult(
            objectKey,
            metadata.ContentLength,
            remoteHash.ToLowerInvariant(),
            NullIfEmpty(metadata.VersionId),
            NullIfEmpty(metadata.ChecksumSHA256),
            NullIfEmpty(metadata.ETag));
    }

    private static AmazonS3Client CreateClient(BackupOptions options)
    {
        var credentials = new BasicAWSCredentials(
            options.ObjectStorageAccessKey!, options.ObjectStorageSecretKey!);
        return new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = options.ObjectStorageEndpoint,
            AuthenticationRegion = options.ObjectStorageRegion,
            ForcePathStyle = options.ObjectStorageUsePathStyle,
            Timeout = TimeSpan.FromMinutes(10),
            MaxErrorRetry = 3
        });
    }

    private static async Task<T> ExecuteProviderAsync<T>(
        BackupDestinationOperation operation,
        Func<Task<T>> action,
        CancellationToken token)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException("S3 backup operation отменена.", token);
        }
        catch (Exception exception)
        {
            var failure = ClassifyProviderFailure(exception, operation);
            throw new BackupDestinationOperationException(
                failure,
                $"S3 backup operation завершилась ошибкой '{failure.Code}'.");
        }
    }

    private static void ValidateOptions(BackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!BackupOptions.IsObjectStorageConfigurationValid(options))
            throw Failure(
                BackupDestinationErrorCode.InvalidConfiguration,
                BackupDestinationFailureDisposition.Permanent,
                "S3-совместимое хранилище настроено не полностью или небезопасно.");
    }

    private static void ValidateExpected(string objectKey, long expectedSize, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Ожидаемый SHA-256 должен быть hex.", nameof(expectedSha256));
    }

    private static string MetadataSha256(MetadataCollection metadata) =>
        metadata["x-amz-meta-sha256"] ?? metadata["sha256"] ?? string.Empty;

    private static string? FirstValue(string? preferred, string? fallback) =>
        NullIfEmpty(preferred) ?? NullIfEmpty(fallback);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static BackupDestinationOperationException Failure(
        BackupDestinationErrorCode code,
        BackupDestinationFailureDisposition disposition,
        string message) => new(new(code, disposition), message);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
