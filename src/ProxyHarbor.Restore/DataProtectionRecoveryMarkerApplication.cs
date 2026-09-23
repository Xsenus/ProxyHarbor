using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

/// <summary>
/// Синтетический DP marker доказывает, что восстановленная копия key ring
/// расшифровывает ciphertext, созданный до аварии, без БД и provider I/O.
/// </summary>
internal static class DataProtectionRecoveryMarkerApplication
{
    private const string Format = "ProxyHarbor.DataProtectionRecoveryMarker";
    private const string Purpose = "ProxyHarbor.RecoveryMarker.v1";
    private const int MaximumMarkerBytes = 16 * 1024;

    internal static readonly string Help = """
        ProxyHarbor dp-marker — офлайн-проверка восстановления Data Protection key ring.

        dp-marker create --keys-directory <absolute-isolated-ring-copy> --output <absolute-new.marker.json>
        dp-marker verify --keys-directory <absolute-restored-ring-copy> --input <absolute.marker.json>

        Создавайте marker заранее и храните его отдельно от копии key ring.
        Используйте только изолированные копии, не действующий production mount.
        Команда не создаёт новые DP keys и не читает БД или облачные аккаунты.
        Один marker проверяет только ключ, которым он был создан; сохраняйте markers после ротации.
        """;

    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                Console.WriteLine(Help);
                return 0;
            }
            var options = Parse(args);
            if (options.Create)
            {
                await CreateAsync(options.KeysDirectory, options.MarkerPath, token);
                Console.WriteLine("Data Protection marker создан в новом файле.");
            }
            else
            {
                await VerifyAsync(options.KeysDirectory, options.MarkerPath, token);
                Console.WriteLine("Data Protection marker проверен восстановленным key ring.");
            }
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Console.Error.WriteLine("Проверка Data Protection marker прервана.");
            return 130;
        }
        catch (Exception)
        {
            // Ошибки DP/IO могут содержать чувствительные локальные пути.
            Console.Error.WriteLine("Data Protection marker отклонён: проверьте параметры, key ring и marker.");
            return 1;
        }
    }

    internal static async Task CreateAsync(string keysDirectory, string outputPath, CancellationToken token)
    {
        ValidateRing(keysDirectory);
        ValidateNewOutput(outputPath);
        token.ThrowIfCancellationRequested();
        var protector = CreateProtector(keysDirectory);
        var payload = RandomNumberGenerator.GetBytes(32);
        byte[] protectedPayload;
        string sha256;
        try
        {
            protectedPayload = protector.Protect(payload);
            sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
        byte[] marker;
        try
        {
            marker = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = Format,
                version = 1,
                protectedPayload = Convert.ToBase64String(protectedPayload),
                sha256
            });
        }
        finally { CryptographicOperations.ZeroMemory(protectedPayload); }
        if (marker.Length > MaximumMarkerBytes)
            throw new InvalidDataException("Data Protection marker превышает размер.");
        var created = false;
        var completed = false;
        try
        {
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            created = true;
            await output.WriteAsync(marker, token);
            await output.FlushAsync(token);
            completed = true;
        }
        finally
        {
            if (created && !completed) File.Delete(outputPath);
        }
    }

    internal static async Task VerifyAsync(string keysDirectory, string markerPath, CancellationToken token)
    {
        ValidateRing(keysDirectory);
        if (!Path.IsPathFullyQualified(markerPath) || !File.Exists(markerPath))
            throw new ArgumentException("Marker path недоступен.");
        await using var input = new FileStream(markerPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is < 1 or > MaximumMarkerBytes)
            throw new InvalidDataException("Data Protection marker имеет недопустимый размер.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token);
        if (await input.ReadAsync(new byte[1], token) != 0)
            throw new InvalidDataException("Data Protection marker изменился при чтении.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Data Protection marker имеет неверную схему.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not
                ("format" or "version" or "protectedPayload" or "sha256"))
                throw new InvalidDataException("Data Protection marker имеет неверную схему.");
        if (names.Count != 4 || root.GetProperty("format").GetString() != Format ||
            root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Data Protection marker имеет неверную схему.");
        var hash = root.GetProperty("sha256").GetString();
        var encoded = root.GetProperty("protectedPayload").GetString();
        if (hash is not { Length: 64 } ||
            !hash.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f') ||
            encoded is not { Length: >= 1 and <= 8192 })
            throw new InvalidDataException("Data Protection marker имеет неверную схему.");
        var protectedPayload = Convert.FromBase64String(encoded);
        var payload = CreateProtector(keysDirectory).Unprotect(protectedPayload);
        try
        {
            var expected = Convert.FromHexString(hash);
            var actual = SHA256.HashData(payload);
            if (payload.Length != 32 || !CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new InvalidDataException("Data Protection key ring не расшифровал marker.");
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static IDataProtector CreateProtector(string keysDirectory) =>
        DataProtectionProvider.Create(new DirectoryInfo(keysDirectory), builder =>
            builder.SetApplicationName("ProxyHarbor").DisableAutomaticKeyGeneration())
            .CreateProtector(Purpose);

    private static void ValidateRing(string keysDirectory)
    {
        if (string.IsNullOrWhiteSpace(keysDirectory) ||
            !Path.IsPathFullyQualified(keysDirectory) || !Directory.Exists(keysDirectory) ||
            !Directory.EnumerateFiles(keysDirectory, "key-*.xml").Any())
            throw new ArgumentException("Изолированная копия Data Protection key ring отсутствует.");
    }

    private static void ValidateNewOutput(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || !Path.IsPathFullyQualified(outputPath) ||
            !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(outputPath))) ||
            File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new ArgumentException("Новый marker output недоступен.");
    }

    private static (bool Create, string KeysDirectory, string MarkerPath) Parse(string[] args)
    {
        if (args.Length != 5 || args[0] is not ("create" or "verify"))
            throw new ArgumentException("Некорректные параметры dp-marker.");
        string? ring = null, marker = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (args[index] == "--keys-directory" && ring is null)
                ring = args[index + 1];
            else if (args[index] == (args[0] == "create" ? "--output" : "--input") && marker is null)
                marker = args[index + 1];
            else
                throw new ArgumentException("Некорректные параметры dp-marker.");
        }
        if (string.IsNullOrWhiteSpace(ring) || string.IsNullOrWhiteSpace(marker))
            throw new ArgumentException("Некорректные параметры dp-marker.");
        return (args[0] == "create", ring, marker);
    }
}
