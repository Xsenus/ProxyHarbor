namespace ProxyHarbor.Infrastructure;

/// <summary>Параметры периодического сбора и проверки.</summary>
public sealed class CollectorOptions
{
    public const string Section = "Collector";
    public bool BackgroundWorkersEnabled { get; set; } = true;
    public int CollectionIntervalMinutes { get; set; } = 15;
    public int ValidationIntervalMinutes { get; set; } = 5;
    public int PublicFreshnessMinutes { get; set; } = 15;
    public int DeadRetryBaseMinutes { get; set; } = 15;
    public int DeadRetryMaxHours { get; set; } = 24;
    public int ValidationConcurrency { get; set; } = 800;
    public int ValidationBatchSize { get; set; } = 1_600;
    public int ProbeTimeoutSeconds { get; set; } = 8;
    public int SourceTimeoutSeconds { get; set; } = 20;
    public int SourceConcurrency { get; set; } = 8;
    public int SourceRetryCount { get; set; } = 2;
    public int SourceFailureBackoffBaseMinutes { get; set; } = 15;
    public int SourceFailureBackoffMaxHours { get; set; } = 24;
    public int MaxProxiesPerSource { get; set; } = 500_000;
    public int MaxCandidatesPerRun { get; set; } = 500_000;
    public int LastSeenRefreshMinutes { get; set; } = 360;
    public int DeadRetentionDays { get; set; } = 3;
    public int RunRetentionDays { get; set; } = 30;
    public string ProbeHost { get; set; } = "api.ipify.org";
    public int ProbePort { get; set; } = 443;
    public string ProbePath { get; set; } = "/?format=json";
}

/// <summary>Параметры шифрованного резервного копирования.</summary>
public sealed class BackupOptions
{
    public const string Section = "Backup";
    public const int MinimumEncryptionKeyLength = 32;
    public const int MinimumLegacyDecryptionKeyLength = 16;
    public const int MaximumEncryptionKeyLength = 1024;
    public const int MaximumDirectoryLength = 1024;
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
    public string Directory { get; set; } = "/app/backups";
    public int RetentionDays { get; set; } = 7;
    public int HistoryRetentionDays { get; set; } = 365;
    public string? EncryptionKey { get; set; }
    public string? TelegramBotToken { get; set; }
    public string? TelegramChatId { get; set; }
    public int MaxTelegramFileSizeMb { get; set; } = 49;

    /// <summary>Новые снимки требуют сильный bounded ключ без неоднозначных управляющих символов.</summary>
    public static bool IsNewEncryptionKeyValid(string? key) =>
        IsEncryptionKeyValid(key, MinimumEncryptionKeyLength);

    /// <summary>Restore сохраняет совместимость с ранее разрешёнными 16-символьными ключами.</summary>
    public static bool IsLegacyDecryptionKeyValid(string? key) =>
        IsEncryptionKeyValid(key, MinimumLegacyDecryptionKeyLength);

    private static bool IsEncryptionKeyValid(string? key, int minimumLength) =>
        !string.IsNullOrWhiteSpace(key) && key.Length is <= MaximumEncryptionKeyLength &&
        key.Length >= minimumLength && !key.Any(char.IsControl);

    /// <summary>Backup должен записываться только в явно заданный абсолютный каталог текущей ОС.</summary>
    internal static bool IsDirectoryValid(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > MaximumDirectoryLength ||
            directory.Any(char.IsControl))
            return false;
        if (!Path.IsPathFullyQualified(directory)) return false;
        try
        {
            var fullPath = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath)?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !string.IsNullOrEmpty(fullPath) && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
