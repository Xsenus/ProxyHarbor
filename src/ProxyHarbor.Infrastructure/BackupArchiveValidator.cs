using System.IO.Compression;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Проверяет структуру расшифрованного backup до любых изменений целевой БД.</summary>
public static class BackupArchiveValidator
{
    private const long MaxManifestBytes = 64 * 1024;
    private const long MaxSettingsEntryBytes = 1024 * 1024;
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
    private static readonly HashSet<string> AllowedEntries = new(
        RequiredDatabaseEntries
            .Concat(["database/backup-runs.json"])
            .Concat(CurrentSettingsEntries)
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

        var manifestEntry = RequiredEntry(archive, "manifest.json");
        if (manifestEntry.Length > MaxManifestBytes)
            throw new InvalidDataException("Manifest backup превышает допустимый размер.");

        using var stream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(stream);
        var root = manifest.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionNumber) ||
            versionNumber is not (2 or 3))
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

        foreach (var name in RequiredDatabaseEntries)
            _ = RequiredEntry(archive, name);
        if (versionNumber >= 3)
        {
            _ = RequiredEntry(archive, "database/backup-runs.json");
            foreach (var name in CurrentSettingsEntries)
                _ = RequiredEntry(archive, name);
        }

        foreach (var name in CurrentSettingsEntries)
        {
            var entry = archive.GetEntry(name);
            if (entry?.Length > MaxSettingsEntryBytes)
                throw new InvalidDataException($"Файл настроек {name} превышает допустимый размер.");
        }
    }

    /// <summary>Возвращает единственную обязательную запись с точным именем.</summary>
    public static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name) =>
        archive.GetEntry(name) ?? throw new InvalidDataException($"В backup отсутствует обязательный файл {name}.");
}
