using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Infrastructure;

/// <summary>Офлайн-чтение PHB3 по подписанному каталогу без production БД и key ring.</summary>
internal static class OfflineCatalogMaterializeApplication
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        try { return await RunAsync(OfflineCatalogMaterializeOptions.Parse(args), token); }
        catch (Exception)
        {
            Console.Error.WriteLine("Offline materialize отклонён: проверьте параметры.");
            return 1;
        }
    }

    internal static async Task<int> RunAsync(OfflineCatalogMaterializeOptions options, CancellationToken token)
    {
        try
        {
            if (options.ShowHelp)
            {
                Console.WriteLine(OfflineCatalogMaterializeOptions.Help);
                return 0;
            }
            options.Validate();
            var key = RuntimeSecretConfiguration.ReadOptionalFile(options.KeyFile, "--key-file")
                ?? throw new ArgumentException("Файл ключа пуст.");
            var catalog = await ReadBoundedAsync(options.CatalogPath!, 256 * 1024, token);
            var providers = await ReadBoundedAsync(options.ProvidersPath!, 64 * 1024, token);
            var result = await new OfflineCatalogMaterializer(new S3BackupObjectStorageTransport())
                .MaterializeAsync(catalog, key, providers, options.OutputPath!,
                    TimeSpan.FromSeconds(options.DeadlineSeconds), token);
            Console.WriteLine($"PHB3 проверен и сохранён: {Path.GetFileName(result.Path)}; " +
                $"copy={result.CopyId}; sha256={result.Sha256}.");
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Console.Error.WriteLine("Offline materialize прерван.");
            return 130;
        }
        catch (Exception)
        {
            // Исключения SDK и JSON могут содержать endpoint или локальные secret paths.
            Console.Error.WriteLine("Offline materialize не выполнен: проверьте каталог, provider files и доступность копий.");
            return 1;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 || stream.Length > maximum)
            throw new InvalidDataException("Файл имеет недопустимый размер.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, token) != 0)
            throw new InvalidDataException("Файл изменился или имеет недопустимый размер.");
        return bytes;
    }
}

internal sealed record OfflineCatalogMaterializeOptions(
    string? CatalogPath, string? ProvidersPath, string? KeyFile,
    string? OutputPath, int DeadlineSeconds, bool ShowHelp)
{
    internal const string Help = """
        ProxyHarbor offline-materialize — PHB3 без production БД и Data Protection key ring.

        offline-materialize --catalog <absolute.catalog.json> \
          --providers <absolute.providers.json> --key-file <absolute-backup-key-file> \
          --output <absolute-new-file.phbackup> [--deadline-seconds 120]

        Вместо --key-file допускается SecretFiles__BackupEncryptionKey.
        providers.json: {"version":1,"destinations":[{"destinationId":"uuid",
          "endpoint":"https://s3.example","region":"region","bucket":"private-bucket",
          "prefix":"proxyharbor/backups","usePathStyle":true,
          "accessKeyFile":"/private/access","secretKeyFile":"/private/secret"}]}.
        Файлы секретов храните отдельно от каталога; inline credentials запрещены.
        Команда только извлекает ciphertext; для restore используйте --input в изолированной БД.
        """;

    internal static OfflineCatalogMaterializeOptions Parse(string[] args)
    {
        string? catalog = null, providers = null, keyFile = null, output = null;
        var deadline = 120;
        var help = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) throw new ArgumentException("Повторный параметр.");
            switch (option)
            {
                case "--catalog": catalog = Next(args, ref index); break;
                case "--providers": providers = Next(args, ref index); break;
                case "--key-file": keyFile = Next(args, ref index); break;
                case "--output": output = Next(args, ref index); break;
                case "--deadline-seconds":
                    if (!int.TryParse(Next(args, ref index), NumberStyles.None,
                        CultureInfo.InvariantCulture, out deadline))
                        throw new ArgumentException("Некорректный deadline.");
                    break;
                case "--help" or "-h": help = true; break;
                default: throw new ArgumentException("Неизвестный параметр.");
            }
        }
        if (!help) keyFile ??= Environment.GetEnvironmentVariable("SecretFiles__BackupEncryptionKey");
        return new(catalog, providers, keyFile, output, deadline, help);
    }

    internal void Validate()
    {
        if (!IsExistingAbsolute(CatalogPath) || !IsExistingAbsolute(ProvidersPath) ||
            !IsExistingAbsolute(KeyFile) || string.IsNullOrWhiteSpace(OutputPath) ||
            !Path.IsPathFullyQualified(OutputPath) ||
            !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(OutputPath))) ||
            File.Exists(OutputPath) || Directory.Exists(OutputPath) ||
            DeadlineSeconds is < 1 or > 1800)
            throw new ArgumentException("Некорректные offline-materialize параметры.");
    }

    private static bool IsExistingAbsolute(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);

    private static string Next(string[] args, ref int index) =>
        ++index < args.Length && !string.IsNullOrWhiteSpace(args[index])
            ? args[index] : throw new ArgumentException("Отсутствует значение параметра.");
}

internal sealed class OfflineCatalogMaterializer(IBackupObjectStorageTransport transport)
{
    internal async Task<(string Path, Guid CopyId, string Sha256)> MaterializeAsync(
        byte[] signedCatalog, string backupKey, byte[] providerFile,
        string outputPath, TimeSpan deadline, CancellationToken token)
    {
        var snapshot = BackupCatalogService.Open(signedCatalog, backupKey);
        var providers = ParseProviders(providerFile);
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(deadline));
        var finalPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(finalPath)!;
        if (!Directory.Exists(directory) || File.Exists(finalPath) || Directory.Exists(finalPath))
            throw new IOException("Целевой файл недоступен или уже существует.");
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
        overall.CancelAfter(deadline);
        var started = TimeProvider.System.GetTimestamp();
        var candidates = snapshot.Copies.OrderBy(copy => copy.Priority)
            .ThenByDescending(copy => copy.VerifiedAt).ThenBy(copy => copy.CopyId)
            .Where(copy => providers.ContainsKey(copy.DestinationId)).ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("Нет конфигурации ни для одной verified copy.");
        for (var index = 0; index < candidates.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var copy = candidates[index];
            var options = providers[copy.DestinationId];
            if (!string.Equals(copy.NativeLocator,
                S3BackupObjectStorageTransport.BuildObjectKey(options.ObjectStoragePrefix, snapshot.FileName),
                StringComparison.Ordinal))
                continue;
            var remaining = deadline - TimeProvider.System.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            attempt.CancelAfter(TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / (candidates.Length - index))));
            var candidatePath = Path.Combine(directory,
                $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.candidate");
            try
            {
                var result = await transport.MaterializeAndVerifyAsync(
                    copy.NativeLocator, candidatePath, snapshot.SizeBytes, snapshot.Sha256,
                    options, attempt.Token);
                if (!string.Equals(Path.GetFullPath(result.Path), candidatePath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    result.SizeBytes != snapshot.SizeBytes || result.Sha256 != snapshot.Sha256)
                    throw new BackupDestinationOperationException(
                        new BackupDestinationFailure(BackupDestinationErrorCode.IntegrityMismatch,
                            BackupDestinationFailureDisposition.Permanent),
                        "Provider вернул candidate с другой identity.");
                await using (var stream = new FileStream(candidatePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, attempt.Token));
                    if (stream.Length != snapshot.SizeBytes || actual != snapshot.Sha256)
                        throw new BackupDestinationOperationException(
                            new BackupDestinationFailure(BackupDestinationErrorCode.IntegrityMismatch,
                                BackupDestinationFailureDisposition.Permanent),
                            "Локальный candidate не совпадает с catalog identity.");
                }
                attempt.Token.ThrowIfCancellationRequested();
                File.Move(candidatePath, finalPath);
                return (finalPath, copy.CopyId, snapshot.Sha256);
            }
            catch (BackupDestinationOperationException) { /* Следующая независимая copy. */ }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Per-copy deadline или overall deadline: сохраняем шанс для следующей copy.
            }
            finally
            {
                try { File.Delete(candidatePath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("Ни одна copy из подписанного каталога не прочитана.");
    }

    private static Dictionary<Guid, BackupOptions> ParseProviders(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 64 * 1024)
            throw new InvalidDataException("Provider config имеет недопустимый размер.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
        var root = document.RootElement;
        RequireFields(root, ["version", "destinations"]);
        if (root.GetProperty("version").GetInt32() != 1 ||
            root.GetProperty("destinations").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Неподдерживаемый provider config.");
        var values = root.GetProperty("destinations").EnumerateArray().ToArray();
        if (values.Length is < 1 or > 16)
            throw new InvalidDataException("Некорректное число providers.");
        var result = new Dictionary<Guid, BackupOptions>();
        foreach (var value in values)
        {
            RequireFields(value, ["destinationId", "endpoint", "region", "bucket", "prefix",
                "usePathStyle", "accessKeyFile", "secretKeyFile"]);
            var id = value.GetProperty("destinationId").GetGuid();
            var accessFile = value.GetProperty("accessKeyFile").GetString();
            var secretFile = value.GetProperty("secretKeyFile").GetString();
            var options = new BackupOptions
            {
                SendToObjectStorage = true,
                ObjectStorageEndpoint = value.GetProperty("endpoint").GetString(),
                ObjectStorageRegion = value.GetProperty("region").GetString() ?? string.Empty,
                ObjectStorageBucket = value.GetProperty("bucket").GetString(),
                ObjectStoragePrefix = value.GetProperty("prefix").GetString() ?? string.Empty,
                ObjectStorageUsePathStyle = value.GetProperty("usePathStyle").GetBoolean(),
                ObjectStorageAccessKey = RuntimeSecretConfiguration.ReadOptionalFile(accessFile, "accessKeyFile"),
                ObjectStorageSecretKey = RuntimeSecretConfiguration.ReadOptionalFile(secretFile, "secretKeyFile")
            };
            if (id == Guid.Empty || !result.TryAdd(id, options) ||
                !BackupOptions.IsObjectStorageConfigurationValid(options))
                throw new InvalidDataException("Некорректный или повторный provider.");
        }
        return result;
    }

    private static void RequireFields(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Ожидался JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("Неизвестное или повторное provider поле.");
        if (seen.Count != fields.Length)
            throw new InvalidDataException("Отсутствует provider поле.");
    }
}
