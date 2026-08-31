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
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        if (!BackupOptions.IsObjectStorageConfigurationValid(options))
            throw new InvalidOperationException("S3-совместимое хранилище настроено не полностью или небезопасно.");

        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Зашифрованный backup-файл не найден.", path);
        var hash = await ComputeSha256Async(path, token);
        var key = BuildObjectKey(options.ObjectStoragePrefix, file.Name);

        var credentials = new BasicAWSCredentials(
            options.ObjectStorageAccessKey!, options.ObjectStorageSecretKey!);
        var configuration = new AmazonS3Config
        {
            ServiceURL = options.ObjectStorageEndpoint,
            AuthenticationRegion = options.ObjectStorageRegion,
            ForcePathStyle = options.ObjectStorageUsePathStyle,
            Timeout = TimeSpan.FromMinutes(10),
            MaxErrorRetry = 3
        };
        using var client = new AmazonS3Client(credentials, configuration);
        var request = new PutObjectRequest
        {
            BucketName = options.ObjectStorageBucket,
            Key = key,
            FilePath = file.FullName,
            ContentType = "application/octet-stream",
            AutoCloseStream = true
        };
        request.Metadata["sha256"] = hash;
        request.Metadata["format"] = "PHB3";
        await client.PutObjectAsync(request, token);

        var metadata = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = options.ObjectStorageBucket,
            Key = key
        }, token);
        var remoteHash = metadata.Metadata["x-amz-meta-sha256"] ?? metadata.Metadata["sha256"];
        if (metadata.ContentLength != file.Length ||
            !string.Equals(remoteHash, hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("S3-объект загружен, но проверка размера или SHA-256 не пройдена.");
        return key;
    }

    internal static string BuildObjectKey(string prefix, string fileName)
    {
        var normalized = prefix.Trim('/');
        return string.IsNullOrEmpty(normalized) ? fileName : $"{normalized}/{fileName}";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexStringLower(hash);
    }
}
