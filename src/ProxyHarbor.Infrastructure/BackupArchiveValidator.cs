using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Проверяет структуру расшифрованного backup до любых изменений целевой БД.</summary>
public static class BackupArchiveValidator
{
    private const long MaxManifestBytes = 64 * 1024;
    private const long MaxSettingsEntryBytes = 1024 * 1024;
    private const long MaxDatabaseEntryBytes = 16L * 1024 * 1024 * 1024;
    private const long MaxTotalUncompressedBytes = 32L * 1024 * 1024 * 1024;
    private const long CompressionRatioThresholdBytes = 1024 * 1024;
    private const long MaxCompressionRatio = 200;
    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequiredDatabaseEntries =
    [
        "database/proxies.json",
        "database/sources.json",
        "database/runs.json"
    ];
    private static readonly string[] CurrentSettingsEntries =
    [
        "settings/collector.json",
        "settings/backup.json",
        "settings/runtime.json"
    ];
    private static readonly string[] IdentityEntries =
    [
        "database/users.json",
        "database/roles.json",
        "database/user-roles.json",
        "database/subscriptions.json"
    ];
    private static readonly HashSet<string> AllowedEntries = new(
        RequiredDatabaseEntries
            .Concat(["database/backup-runs.json", "database/validation-runs.json"])
            .Concat(CurrentSettingsEntries)
            .Concat(IdentityEntries)
            .Append("database/payment-orders.json")
            .Append("database/payment-configuration.json")
            .Append("database/subscription-admin-actions.json")
            .Append("database/proxy-access-buckets.json")
            .Append("database/site-visit-logs.json")
            .Append("database/free-proxy-export-grants.json")
            .Append("database/access-block-rules.json")
            .Append("database/telegram-bot-configuration.json")
            .Append("database/backup-configuration.json")
            .Append("database/telegram-chats.json")
            .Append("database/telegram-update-receipts.json")
            .Append("database/telegram-outbound-messages.json")
            .Append("database/telegram-conversation-messages.json")
            .Append("manifest.json"),
        StringComparer.Ordinal);

    /// <summary>Отклоняет неподдерживаемые, неоднозначные и потенциально опасные архивы.</summary>
    public static void Validate(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var duplicate = archive.Entries
            .GroupBy(entry => entry.FullName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Backup содержит повторяющийся файл {duplicate.Key}.");

        var unexpected = archive.Entries.FirstOrDefault(entry => !AllowedEntries.Contains(entry.FullName));
        if (unexpected is not null)
            throw new InvalidDataException($"Backup содержит файл вне разрешённой схемы: {unexpected.FullName}.");

        ValidateArchiveBounds(archive);

        var manifestEntry = RequiredEntry(archive, "manifest.json");
        if (manifestEntry.Length > MaxManifestBytes)
            throw new InvalidDataException("Manifest backup превышает допустимый размер.");

        using var stream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(stream);
        var root = manifest.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Manifest backup должен содержать JSON object.");
        EnsureNoDuplicateProperties(root, "manifest.json");
        if (!root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionNumber) ||
            versionNumber is < 2 or > 6)
            throw new InvalidDataException("Версия manifest backup не поддерживается.");
        if (!root.TryGetProperty("secretsIncluded", out var secretsIncluded) ||
            secretsIncluded.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            secretsIncluded.GetBoolean())
            throw new InvalidDataException("Backup с секретами или без явной политики секретов не поддерживается.");

        if (versionNumber >= 3 &&
            (!root.TryGetProperty("createdAt", out var createdAt) ||
                createdAt.ValueKind != JsonValueKind.String ||
                !createdAt.TryGetDateTimeOffset(out _)))
            throw new InvalidDataException("Manifest текущего backup не содержит корректный createdAt.");
        if (versionNumber >= 5 &&
            (!root.TryGetProperty("settingsSchemaVersion", out var settingsSchemaVersion) ||
                settingsSchemaVersion.ValueKind != JsonValueKind.Number ||
                !settingsSchemaVersion.TryGetInt32(out var settingsSchemaVersionNumber) ||
                settingsSchemaVersionNumber != 1))
            throw new InvalidDataException("Manifest текущего backup не содержит поддерживаемую схему настроек.");
        if (versionNumber >= 5)
            RequireExactProperties(root,
                ["version", "settingsSchemaVersion", "createdAt", "secretsIncluded"], "manifest.json");

        if (versionNumber < 6 &&
            archive.Entries.Any(entry => IdentityEntries.Contains(entry.FullName, StringComparer.Ordinal)))
            throw new InvalidDataException("Identity snapshot поддерживается только backup schema версии 6.");

        foreach (var name in RequiredDatabaseEntries)
            _ = RequiredEntry(archive, name);
        if (versionNumber >= 3)
        {
            _ = RequiredEntry(archive, "database/backup-runs.json");
            foreach (var name in CurrentSettingsEntries)
                _ = RequiredEntry(archive, name);
        }
        if (versionNumber >= 4)
            _ = RequiredEntry(archive, "database/validation-runs.json");
        if (versionNumber >= 6)
            foreach (var name in IdentityEntries) _ = RequiredEntry(archive, name);

        if (versionNumber >= 5)
        {
            ValidateSettingsObject<CollectorOptions>(archive, "settings/collector.json");
            ValidateSettingsObject<BackupSettingsSnapshot>(archive, "settings/backup.json", rootElement =>
                RequireFalse(rootElement, "secretsIncluded", "settings/backup.json"));
            ValidateSettingsObject<BackupRuntimeSettings>(archive, "settings/runtime.json", rootElement =>
            {
                RequireFalse(rootElement, "adminApiKeyIncluded", "settings/runtime.json");
                RequireFalse(rootElement, "connectionStringIncluded", "settings/runtime.json");
                RequireFalse(rootElement, "paymentSecretsIncluded", "settings/runtime.json");
            });
        }
    }

    /// <summary>Отклоняет ZIP-bomb и архив, выходящий за эксплуатационные пределы restore.</summary>
    private static void ValidateArchiveBounds(ZipArchive archive)
    {
        long totalLength = 0;
        foreach (var entry in archive.Entries)
            totalLength = AccumulateValidatedEntrySize(
                entry.FullName, entry.Length, entry.CompressedLength, totalLength);
    }

    /// <summary>Чистая bounded-проверка одного ZIP entry, используемая до распаковки.</summary>
    internal static long AccumulateValidatedEntrySize(
        string name,
        long length,
        long compressedLength,
        long currentTotal)
    {
        if (name.StartsWith("settings/", StringComparison.Ordinal) && length > MaxSettingsEntryBytes)
            throw new InvalidDataException($"Файл настроек {name} превышает допустимый размер.");
        if (name.StartsWith("database/", StringComparison.Ordinal) && length > MaxDatabaseEntryBytes)
            throw new InvalidDataException($"Файл данных {name} превышает лимит 16 ГиБ.");
        if (length > MaxTotalUncompressedBytes - currentTotal)
            throw new InvalidDataException("Распакованный backup превышает общий лимит 32 ГиБ.");

        // Небольшие JSON-файлы могут иметь высокий ratio без существенного расхода.
        // Для крупных entry отношение 200:1 оставляет большой запас обычному JSON,
        // но останавливает классические deflate-bomb до начала транзакции restore.
        if (length >= CompressionRatioThresholdBytes &&
            (compressedLength <= 0 || (double)length / compressedLength > MaxCompressionRatio))
            throw new InvalidDataException($"Файл {name} имеет опасную степень ZIP-сжатия.");
        return currentTotal + length;
    }

    /// <summary>Требует точную, недублированную и десериализуемую схему JSON settings.</summary>
    private static void ValidateSettingsObject<T>(
        ZipArchive archive,
        string name,
        Action<JsonElement>? semanticValidation = null)
    {
        try
        {
            using var stream = RequiredEntry(archive, name).Open();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Файл настроек {name} должен содержать JSON object.");

            var expected = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
                .ToHashSet(StringComparer.Ordinal);
            RequireExactProperties(root, expected, name);
            _ = root.Deserialize<T>(SettingsJsonOptions) ??
                throw new InvalidDataException($"Файл настроек {name} не удалось десериализовать.");
            semanticValidation?.Invoke(root);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Файл настроек {name} содержит некорректный JSON.", exception);
        }
    }

    private static void RequireFalse(JsonElement root, string propertyName, string entryName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || property.GetBoolean())
            throw new InvalidDataException($"Файл настроек {entryName} нарушает политику исключения секретов.");
    }

    private static void RequireExactProperties(
        JsonElement root,
        IEnumerable<string> expectedProperties,
        string entryName)
    {
        var actual = EnsureNoDuplicateProperties(root, entryName);
        if (!actual.SetEquals(expectedProperties))
            throw new InvalidDataException($"Файл {entryName} не соответствует полной схеме версии 1.");
    }

    private static HashSet<string> EnsureNoDuplicateProperties(JsonElement root, string entryName)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!actual.Add(property.Name))
                throw new InvalidDataException($"Файл {entryName} содержит повторяющееся поле {property.Name}.");
        }
        return actual;
    }

    /// <summary>Возвращает единственную обязательную запись с точным именем.</summary>
    public static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name) =>
        archive.GetEntry(name) ?? throw new InvalidDataException($"В backup отсутствует обязательный файл {name}.");
}
