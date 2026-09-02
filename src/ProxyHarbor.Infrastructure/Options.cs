using System.Globalization;
using System.Net;
using System.Text;

namespace ProxyHarbor.Infrastructure;

/// <summary>Параметры периодического сбора и проверки.</summary>
public sealed class CollectorOptions
{
    /// <summary>Имя configuration-секции.</summary>
    public const string Section = "Collector";
    /// <summary>Запускать ли collector/validator background workers в этой реплике.</summary>
    public bool BackgroundWorkersEnabled { get; set; } = true;
    /// <summary>Базовый период планового сбора источников.</summary>
    public int CollectionIntervalMinutes { get; set; } = 5;
    /// <summary>Пауза между проходами validation worker.</summary>
    public int ValidationIntervalMinutes { get; set; } = 2;
    /// <summary>Максимальный возраст Alive-проверки для публичной выдачи.</summary>
    public int PublicFreshnessMinutes { get; set; } = 15;
    /// <summary>Начальная задержка повторной проверки Dead-прокси.</summary>
    public int DeadRetryBaseMinutes { get; set; } = 15;
    /// <summary>Верхняя граница адаптивной задержки повторной проверки Dead-прокси.</summary>
    public int DeadRetryMaxHours { get; set; } = 24;
    /// <summary>Максимальное число одновременных сетевых проверок прокси.</summary>
    public int ValidationConcurrency { get; set; } = 800;
    /// <summary>Максимальное число proxy rows в одной распределённой аренде.</summary>
    public int ValidationBatchSize { get; set; } = 1_600;
    /// <summary>Максимальное число одновременных TCP-проверок VPN endpoint.</summary>
    public int VpnValidationConcurrency { get; set; } = 800;
    /// <summary>Максимальное число VPN endpoint в одной локальной validation-партии.</summary>
    public int VpnValidationBatchSize { get; set; } = 1_600;
    /// <summary>Период повторной проверки доступного VPN endpoint.</summary>
    public int VpnReachableValidationIntervalMinutes { get; set; } = 10;
    /// <summary>Период повторной проверки недоступного VPN endpoint.</summary>
    public int VpnUnreachableRetryMinutes { get; set; } = 30;
    /// <summary>Период повторной классификации неподдерживаемого VPN-транспорта.</summary>
    public int VpnUnsupportedRetryMinutes { get; set; } = 360;
    /// <summary>Максимальный возраст успешной VPN-проверки для публичной выдачи.</summary>
    public int VpnPublicFreshnessMinutes { get; set; } = 15;
    /// <summary>Полный timeout одной proxy-проверки.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 8;
    /// <summary>Полный timeout одной попытки загрузки feed'а.</summary>
    public int SourceTimeoutSeconds { get; set; } = 20;
    /// <summary>Максимальное число одновременно загружаемых feed'ов.</summary>
    public int SourceConcurrency { get; set; } = 8;
    /// <summary>Число повторов transient-ошибки источника после первой попытки.</summary>
    public int SourceRetryCount { get; set; } = 2;
    /// <summary>Начальная задержка source backoff после неуспешного цикла.</summary>
    public int SourceFailureBackoffBaseMinutes { get; set; } = 15;
    /// <summary>Верхняя граница source backoff.</summary>
    public int SourceFailureBackoffMaxHours { get; set; } = 24;
    /// <summary>Максимум уникальных endpoint'ов, принимаемых из одного feed'а.</summary>
    public int MaxProxiesPerSource { get; set; } = 500_000;
    /// <summary>Максимум уникальных endpoint'ов после объединения всех feed'ов цикла.</summary>
    public int MaxCandidatesPerRun { get; set; } = 500_000;
    /// <summary>Минимальный интервал между persistence-обновлениями LastSeenAt.</summary>
    public int LastSeenRefreshMinutes { get; set; } = 360;
    /// <summary>Срок хранения давно не встречавшихся Pending/Dead proxy rows.</summary>
    public int DeadRetentionDays { get; set; } = 3;
    /// <summary>Срок хранения завершённых collection run'ов.</summary>
    public int RunRetentionDays { get; set; } = 30;
    /// <summary>Срок хранения подробных validation-партий; активные аренды не удаляются.</summary>
    public int ValidationRunRetentionHours { get; set; } = 24;
    /// <summary>Публичный контрольный host для TLS-пробы и определения exit IP.</summary>
    public string ProbeHost { get; set; } = "api.ipify.org";
    /// <summary>TCP-порт HTTPS контрольного endpoint.</summary>
    public int ProbePort { get; set; } = 443;
    /// <summary>Канонический HTTP origin-form path/query контрольного endpoint.</summary>
    public string ProbePath { get; set; } = "/?format=json";
    /// <summary>
    /// Независимые HTTPS endpoint'ы, используемые по порядку при недоступности основного.
    /// Ответ может быть JSON-объектом с полем ip, единственным IP или Cloudflare trace.
    /// </summary>
    public string[] ProbeFallbackUrls { get; set; } = [];

    /// <summary>Изолированный массив рекомендуемых независимых fallback-сервисов.</summary>
    public static string[] CreateDefaultProbeFallbackUrls() =>
    [
        "https://api64.ipify.org/?format=json",
        "https://ifconfig.co/json",
        "https://www.cloudflare.com/cdn-cgi/trace"
    ];

    /// <summary>Control host передаётся URI, TLS SNI и сырому HTTP Host одинаковыми ASCII bytes.</summary>
    public static bool IsProbeHostValid(string? host)
    {
        if (host is not { Length: >= 1 and <= 253 } ||
            host.Any(character => character is < '!' or > '~' or '%'))
            return false;

        if (System.Net.IPAddress.TryParse(host, out var address))
            return !address.IsIPv4MappedToIPv6 &&
                string.Equals(address.ToString(), host, StringComparison.OrdinalIgnoreCase);

        return NetworkSafety.IsCanonicalDnsName(host);
    }

    /// <summary>Требует уже канонический ASCII origin-form, одинаковый для direct и proxy request.</summary>
    public static bool IsProbePathValid(string? path)
    {
        if (path is not { Length: >= 1 and <= 2048 } || path[0] != '/' ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.Any(character => character is < '!' or > '~' or '#'))
            return false;

        return Uri.TryCreate($"https://probe.invalid{path}", UriKind.Absolute, out var endpoint) &&
            string.Equals(endpoint.Host, "probe.invalid", StringComparison.Ordinal) &&
            string.Equals(
                endpoint.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped),
                path,
                StringComparison.Ordinal);
    }

    /// <summary>Разбирает только публичный HTTPS endpoint без credentials/fragment.</summary>
    public static bool TryParseProbeUrl(string? value, out ProbeControlEndpoint endpoint)
    {
        endpoint = default!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_304 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port is < 1 or > 65_535 || !IsProbeHostValid(uri.Host) ||
            (IPAddress.TryParse(uri.Host, out var address) && !NetworkSafety.IsPublicAddress(address)))
            return false;

        var path = uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
        if (string.IsNullOrEmpty(path)) path = "/";
        if (!IsProbePathValid(path)) return false;
        endpoint = new ProbeControlEndpoint(uri.Host, uri.Port, path);
        return true;
    }
}

/// <summary>Проверенный HTTPS-адрес контрольной пробы.</summary>
public sealed record ProbeControlEndpoint(string Host, int Port, string Path);

/// <summary>Параметры шифрованного резервного копирования.</summary>
public sealed class BackupOptions
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>Имя configuration-секции.</summary>
    public const string Section = "Backup";
    /// <summary>Минимальная длина ключа для новых PHB3-архивов.</summary>
    public const int MinimumEncryptionKeyLength = 32;
    /// <summary>Минимальная длина ключа при чтении совместимых legacy-архивов.</summary>
    public const int MinimumLegacyDecryptionKeyLength = 16;
    /// <summary>Максимальная длина ключа, ограничивающая PBKDF2 input.</summary>
    public const int MaximumEncryptionKeyLength = 1024;
    /// <summary>Максимальная длина абсолютного backup path.</summary>
    public const int MaximumDirectoryLength = 1024;
    /// <summary>Включить плановое создание и обязательную Telegram-доставку backup.</summary>
    public bool Enabled { get; set; }
    /// <summary>Период между успешными плановыми архивами.</summary>
    public int IntervalHours { get; set; } = 24;
    /// <summary>Абсолютный каталог атомарно публикуемых PHB3-файлов.</summary>
    public string Directory { get; set; } = "/app/backups";
    /// <summary>Срок локального хранения опубликованных архивов.</summary>
    public int RetentionDays { get; set; } = 7;
    /// <summary>Срок хранения audit rows резервного копирования.</summary>
    public int HistoryRetentionDays { get; set; } = 365;
    /// <summary>Секретный ключ PHB3; никогда не включается в snapshot настроек.</summary>
    public string? EncryptionKey { get; set; }
    /// <summary>Секретный Bot API token; никогда не включается в backup или log.</summary>
    public string? TelegramBotToken { get; set; }
    /// <summary>Числовой идентификатор администратора/группы для доставки архива.</summary>
    public string? TelegramChatId { get; set; }
    /// <summary>Внутренний идентификатор CRM-диалога основного Telegram-бота.</summary>
    public Guid? TelegramRecipientId { get; set; }
    /// <summary>Максимальный размер одного Telegram document перед разбиением.</summary>
    public int MaxTelegramFileSizeMb { get; set; } = 49;
    /// <summary>Дублировать опубликованный PHB3-архив в S3-совместимое объектное хранилище.</summary>
    public bool SendToObjectStorage { get; set; }
    /// <summary>HTTPS endpoint российского или иного S3-совместимого провайдера.</summary>
    public string? ObjectStorageEndpoint { get; set; }
    /// <summary>Регион, используемый при подписи AWS Signature V4.</summary>
    public string ObjectStorageRegion { get; set; } = "ru-central1";
    /// <summary>Имя заранее созданного непубличного bucket.</summary>
    public string? ObjectStorageBucket { get; set; }
    /// <summary>Безопасный префикс ключей внутри bucket.</summary>
    public string ObjectStoragePrefix { get; set; } = "proxyharbor/backups";
    /// <summary>S3 access key; хранится только в защищённой server-side конфигурации.</summary>
    public string? ObjectStorageAccessKey { get; set; }
    /// <summary>S3 secret key; хранится только в защищённой server-side конфигурации.</summary>
    public string? ObjectStorageSecretKey { get; set; }
    /// <summary>Использовать path-style URL для провайдеров, не поддерживающих virtual-hosted style.</summary>
    public bool ObjectStorageUsePathStyle { get; set; } = true;

    /// <summary>Проверяет полноту и безопасные границы S3-совместимой конфигурации.</summary>
    public static bool IsObjectStorageConfigurationValid(BackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Uri.TryCreate(options.ObjectStorageEndpoint, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(endpoint.UserInfo) &&
            string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) &&
            endpoint.AbsolutePath is "" or "/" && !string.IsNullOrWhiteSpace(endpoint.Host) &&
            options.ObjectStorageRegion is { Length: >= 1 and <= 100 } &&
            options.ObjectStorageRegion.All(IsSafeObjectStorageCharacter) &&
            IsObjectStorageBucketValid(options.ObjectStorageBucket) &&
            options.ObjectStoragePrefix is { Length: <= 512 } &&
            IsSafeObjectStoragePrefix(options.ObjectStoragePrefix) &&
            options.ObjectStorageAccessKey is { Length: >= 3 and <= 256 } &&
            options.ObjectStorageAccessKey.All(character => character is >= '!' and <= '~') &&
            options.ObjectStorageSecretKey is { Length: >= 8 and <= 1024 } &&
            options.ObjectStorageSecretKey.All(character => !char.IsControl(character) && !char.IsSurrogate(character));
    }

    private static bool IsSafeObjectStorageCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

    private static bool IsObjectStorageBucketValid(string? bucket) =>
        bucket is { Length: >= 3 and <= 63 } &&
        char.IsAsciiLetterOrDigit(bucket[0]) && char.IsAsciiLetterOrDigit(bucket[^1]) &&
        bucket.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '.') &&
        !bucket.Contains("..", StringComparison.Ordinal) &&
        !System.Net.IPAddress.TryParse(bucket, out _);

    private static bool IsSafeObjectStoragePrefix(string prefix) =>
        !prefix.StartsWith('/') && !prefix.Contains('\\') &&
        prefix.Split('/', StringSplitOptions.RemoveEmptyEntries).All(segment =>
            segment is not "." and not ".." && segment.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));

    /// <summary>Проверяет bounded path-safe ASCII token без недокументированных предположений о его структуре.</summary>
    public static bool IsTelegramBotTokenValid(string? token)
    {
        return token is { Length: >= 20 and <= 256 } &&
            token.All(character => character is >= '!' and <= '~' and
                not ('/' or '\\' or '?' or '#' or '%'));
    }

    /// <summary>Общий API/Alertmanager secret обязан быть ненулевым signed 64-bit chat ID.</summary>
    public static bool IsTelegramChatIdValid(string? chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId) || chatId.Length > 20) return false;
        var digits = chatId[0] == '-' ? chatId.AsSpan(1) : chatId.AsSpan();
        return digits.Length > 0 && digits.IndexOfAnyExceptInRange('0', '9') < 0 &&
            long.TryParse(chatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) &&
            value != 0;
    }

    /// <summary>Новые снимки требуют сильный bounded ключ без неоднозначных управляющих символов.</summary>
    public static bool IsNewEncryptionKeyValid(string? key) =>
        IsEncryptionKeyValid(key, MinimumEncryptionKeyLength);

    /// <summary>Restore сохраняет совместимость с ранее разрешёнными 16-символьными ключами.</summary>
    public static bool IsLegacyDecryptionKeyValid(string? key) =>
        IsEncryptionKeyValid(key, MinimumLegacyDecryptionKeyLength);

    private static bool IsEncryptionKeyValid(string? key, int minimumLength)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumEncryptionKeyLength ||
            key.Length < minimumLength || key.Any(char.IsControl))
            return false;
        try
        {
            // Строгий encoder запрещает unpaired surrogate. Иначе разные строки ключа
            // превращаются стандартным UTF-8 encoder в одинаковые replacement bytes.
            _ = StrictUtf8.GetByteCount(key);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

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
