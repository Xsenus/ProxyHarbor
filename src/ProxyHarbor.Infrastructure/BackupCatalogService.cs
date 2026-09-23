using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ProxyHarbor.Infrastructure;

/// <summary>Несекретная ссылка на одну verified S3-копию immutable ciphertext.</summary>
public sealed record BackupCatalogCopy(
    Guid CopyId,
    Guid DestinationId,
    string Kind,
    string NativeLocator,
    int Priority,
    DateTimeOffset VerifiedAt);

/// <summary>Аутентифицируемый снимок inventory, сохраняемый вне production БД.</summary>
public sealed record BackupCatalogSnapshot(
    Guid BackupRunId,
    string FileName,
    long SizeBytes,
    string Sha256,
    int PolicyVersion,
    DateTimeOffset FinishedAt,
    DateTimeOffset ExportedAt,
    string KeyReference,
    BackupCatalogCopy[] Copies);

/// <summary>
/// Создаёт и проверяет bounded sidecar catalog. Содержит только object keys и IDs:
/// endpoint, bucket, credentials и зашифрованные secrets никогда не сериализуются.
/// </summary>
public sealed class BackupCatalogService(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    TimeProvider? timeProvider = null)
{
    private const string Format = "ProxyHarbor.BackupCatalog";
    private const int Version = 1;
    private const int MaximumBytes = 256 * 1024;
    private const int SigningIterations = 200_000;
    private static ReadOnlySpan<byte> SigningDomain => "ProxyHarbor.BackupCatalog.SigningKey.v1"u8;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>Формирует snapshot только из текущих exact-policy verified S3 read routes.</summary>
    public async Task<BackupCatalogSnapshot> CreateAsync(
        Guid backupRunId,
        string keyReference,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var run = await db.BackupRuns.AsNoTracking()
            .Include(item => item.Copies).ThenInclude(copy => copy.BackupDestination)
            .SingleOrDefaultAsync(item => item.Id == backupRunId, token)
            ?? throw new InvalidOperationException("Backup run не найден.");
        if (run.Status != "completed" || run.FinishedAt is not { } finishedAt ||
            run.BackupPoolId is not { } poolId ||
            run.ProtectionPolicyVersion is not { } policyVersion ||
            string.IsNullOrWhiteSpace(run.ContentSha256) || string.IsNullOrWhiteSpace(run.FileName))
            throw new InvalidOperationException("Backup run не содержит полный immutable snapshot.");

        var routes = await db.BackupPoolDestinations.AsNoTracking()
            .Where(route => route.BackupPoolId == poolId)
            .ToDictionaryAsync(route => route.BackupDestinationId, token);
        var copies = run.Copies
            .Where(copy => copy.State == "verified" && copy.VerifiedAt is not null &&
                copy.PolicyVersion == policyVersion && copy.SizeBytes == run.SizeBytes &&
                string.Equals(copy.ContentSha256, run.ContentSha256, StringComparison.Ordinal) &&
                copy.BackupDestination.Enabled && copy.BackupDestination.Kind == "s3" &&
                !string.IsNullOrWhiteSpace(copy.NativeLocator) &&
                MatchesConfiguredLocator(
                    copy.BackupDestination.SettingsJson, copy.NativeLocator, run.FileName) &&
                routes.TryGetValue(copy.BackupDestinationId, out var route) &&
                (route.Enabled || route.Draining) && AllowsRead(route.AllowedOperations))
            .OrderBy(copy => routes[copy.BackupDestinationId].Priority)
            .ThenBy(copy => copy.BackupDestination.Priority)
            .ThenByDescending(copy => copy.VerifiedAt)
            .ThenBy(copy => copy.Id)
            .Select(copy => new BackupCatalogCopy(
                copy.Id,
                copy.BackupDestinationId,
                "s3",
                copy.NativeLocator!,
                routes[copy.BackupDestinationId].Priority,
                copy.VerifiedAt!.Value))
            .ToArray();
        var snapshot = new BackupCatalogSnapshot(
            run.Id, run.FileName, run.SizeBytes, run.ContentSha256,
            policyVersion, finishedAt, clock.GetUtcNow(), keyReference, copies);
        Validate(snapshot, clock.GetUtcNow());
        return snapshot;
    }

    /// <summary>Готовит однокопийный sidecar для точного S3 locator после verified commit.</summary>
    public async Task<BackupCatalogSnapshot> CreateForCopyAsync(
        Guid backupCopyId,
        string keyReference,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var runId = await db.BackupCopies.AsNoTracking()
            .Where(copy => copy.Id == backupCopyId)
            .Select(copy => (Guid?)copy.BackupRunId)
            .SingleOrDefaultAsync(token)
            ?? throw new InvalidOperationException("Backup copy не найдена.");
        var snapshot = await CreateAsync(runId, keyReference, token);
        var selected = snapshot.Copies.SingleOrDefault(copy => copy.CopyId == backupCopyId)
            ?? throw new InvalidOperationException("Backup copy не пригодна для catalog publication.");
        return snapshot with { Copies = [selected] };
    }

    /// <summary>Подписывает canonical JSON отдельным domain-separated HMAC key.</summary>
    public static byte[] Seal(BackupCatalogSnapshot snapshot, string encryptionKey)
    {
        Validate(snapshot, DateTimeOffset.UtcNow);
        ValidateKey(encryptionKey);
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        var salt = RandomNumberGenerator.GetBytes(16);
        var mac = ComputeMac(payload, encryptionKey, salt);
        var result = JsonSerializer.SerializeToUtf8Bytes(
            new BackupCatalogEnvelope(Format, Version, snapshot,
                Convert.ToHexStringLower(salt), mac), Json);
        if (result.Length > MaximumBytes)
            throw new InvalidDataException("Backup catalog превышает допустимый размер.");
        return result;
    }

    /// <summary>Проверяет строгую схему, HMAC и внутреннюю согласованность без БД/provider I/O.</summary>
    public static BackupCatalogSnapshot Open(ReadOnlySpan<byte> bytes, string encryptionKey)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
            throw new InvalidDataException("Backup catalog имеет недопустимый размер.");
        ValidateKey(encryptionKey);
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        ValidateShape(document.RootElement);
        var envelope = JsonSerializer.Deserialize<BackupCatalogEnvelope>(bytes, Json)
            ?? throw new InvalidDataException("Backup catalog пуст.");
        if (envelope.Format != Format || envelope.Version != Version ||
            envelope.Snapshot is null || envelope.Salt is null ||
            envelope.Mac is null || !IsLowerHex(envelope.Salt, 32) ||
            !IsLowerHex(envelope.Mac, 64))
            throw new InvalidDataException("Backup catalog имеет неподдерживаемую схему.");
        Validate(envelope.Snapshot, DateTimeOffset.UtcNow);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope.Snapshot, Json);
        var expected = Convert.FromHexString(ComputeMac(
            payload, encryptionKey, Convert.FromHexString(envelope.Salt)));
        var supplied = Convert.FromHexString(envelope.Mac);
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            throw new InvalidDataException("Подпись backup catalog не совпадает.");
        return envelope.Snapshot;
    }

    private static void Validate(BackupCatalogSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.BackupRunId == Guid.Empty || !IsSafeFileName(snapshot.FileName) ||
            snapshot.SizeBytes <= 0 || !IsLowerHex(snapshot.Sha256, 64) ||
            snapshot.PolicyVersion < 1 || !IsSafeToken(snapshot.KeyReference, 64) ||
            snapshot.FinishedAt.Offset != TimeSpan.Zero ||
            snapshot.ExportedAt.Offset != TimeSpan.Zero ||
            snapshot.FinishedAt == default || snapshot.ExportedAt < snapshot.FinishedAt ||
            snapshot.ExportedAt > now.AddMinutes(5) ||
            snapshot.Copies is not { Length: >= 1 and <= 16 })
            throw new InvalidDataException("Backup catalog содержит некорректный snapshot.");
        var copyIds = new HashSet<Guid>();
        var destinations = new HashSet<Guid>();
        foreach (var copy in snapshot.Copies)
        {
            if (copy.CopyId == Guid.Empty || copy.DestinationId == Guid.Empty ||
                !copyIds.Add(copy.CopyId) || !destinations.Add(copy.DestinationId) ||
                copy.Kind != "s3" || copy.Priority < 0 ||
                copy.VerifiedAt.Offset != TimeSpan.Zero ||
                copy.VerifiedAt < snapshot.FinishedAt || copy.VerifiedAt > snapshot.ExportedAt ||
                !IsSafeLocator(copy.NativeLocator, snapshot.FileName))
                throw new InvalidDataException("Backup catalog содержит некорректную copy.");
        }
    }

    private static bool AllowsRead(string operations) =>
        operations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("read", StringComparer.Ordinal);

    private static bool MatchesConfiguredLocator(string settingsJson, string? locator, string fileName)
    {
        try
        {
            using var settings = JsonDocument.Parse(settingsJson);
            if (settings.RootElement.ValueKind != JsonValueKind.Object ||
                !settings.RootElement.TryGetProperty("prefix", out var prefixElement) ||
                prefixElement.ValueKind != JsonValueKind.String)
                return false;
            var prefix = prefixElement.GetString();
            return prefix is not null && string.Equals(
                locator,
                S3BackupObjectStorageTransport.BuildObjectKey(prefix, fileName),
                StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSafeFileName(string? fileName) =>
        fileName is { Length: >= 10 and <= 200 } &&
        fileName.EndsWith(".phbackup", StringComparison.Ordinal) &&
        fileName.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSafeLocator(string? locator, string fileName)
    {
        if (locator is not { Length: >= 10 and <= 1024 } ||
            !(locator == fileName || locator.EndsWith('/' + fileName, StringComparison.Ordinal)))
            return false;
        return locator.Split('/').All(segment => segment is not "." and not ".." &&
            segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
    }

    private static bool IsSafeToken(string? value, int maximumLength) =>
        value is { Length: >= 1 } && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsLowerHex(string? value, int length) => value?.Length == length &&
        value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static void ValidateKey(string key)
    {
        if (!BackupOptions.IsLegacyDecryptionKeyValid(key))
            throw new ArgumentException("Недопустимый ключ подписи backup catalog.");
    }

    private static string ComputeMac(byte[] payload, string encryptionKey, byte[] salt)
    {
        var secret = Encoding.UTF8.GetBytes(encryptionKey);
        var separatedSalt = new byte[SigningDomain.Length + salt.Length];
        SigningDomain.CopyTo(separatedSalt);
        salt.CopyTo(separatedSalt.AsSpan(SigningDomain.Length));
        try
        {
            var derived = Rfc2898DeriveBytes.Pbkdf2(
                secret, separatedSalt, SigningIterations, HashAlgorithmName.SHA256, 32);
            try { return Convert.ToHexStringLower(HMACSHA256.HashData(derived, payload)); }
            finally { CryptographicOperations.ZeroMemory(derived); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(separatedSalt);
        }
    }

    private static void ValidateShape(JsonElement root)
    {
        RequireObject(root, ["format", "version", "snapshot", "salt", "mac"]);
        var snapshot = root.GetProperty("snapshot");
        RequireObject(snapshot, ["backupRunId", "fileName", "sizeBytes", "sha256",
            "policyVersion", "finishedAt", "exportedAt", "keyReference", "copies"]);
        var copies = snapshot.GetProperty("copies");
        if (copies.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Backup catalog copies должны быть массивом.");
        foreach (var copy in copies.EnumerateArray())
            RequireObject(copy, ["copyId", "destinationId", "kind", "nativeLocator",
                "priority", "verifiedAt"]);
    }

    private static void RequireObject(JsonElement element, string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Backup catalog содержит объект неверного типа.");
        var expected = fields.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("Backup catalog содержит неизвестное или повторное поле.");
        }
        if (seen.Count != expected.Count)
            throw new InvalidDataException("Backup catalog не содержит обязательное поле.");
    }

    private sealed record BackupCatalogEnvelope(
        string Format, int Version, BackupCatalogSnapshot Snapshot, string Salt, string Mac);
}
