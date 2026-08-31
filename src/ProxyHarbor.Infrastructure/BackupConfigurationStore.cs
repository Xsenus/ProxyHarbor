using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ProxyHarbor.Infrastructure;

/// <summary>Читает и атомарно сохраняет эффективные настройки резервного копирования.</summary>
public interface IBackupConfigurationStore
{
    /// <summary>Возвращает свежий снимок, включая расшифрованные сервером Telegram-реквизиты.</summary>
    Task<BackupOptions> GetAsync(CancellationToken token = default);
    /// <summary>Сохраняет проверенный снимок, не меняя deploy-каталог и ключ PHB3.</summary>
    Task SaveAsync(BackupOptions options, CancellationToken token = default);
}

/// <summary>
/// Переживающее рестарты хранилище: публичные поля находятся в PostgreSQL JSONB,
/// Telegram-реквизиты защищены постоянным ASP.NET Core Data Protection key-ring.
/// </summary>
public sealed class BackupConfigurationStore(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<BackupOptions> configured,
    IDataProtectionProvider protectionProvider) : IBackupConfigurationStore
{
    private const int SingletonId = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector = protectionProvider.CreateProtector(
        "ProxyHarbor.BackupConfiguration.Secrets.v1");

    /// <inheritdoc />
    public async Task<BackupOptions> GetAsync(CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var entity = await db.BackupConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == SingletonId, token);
        if (entity is null) return CloneConfigured();

        try
        {
            var settings = JsonSerializer.Deserialize<StoredBackupSettings>(entity.SettingsJson, Json)
                ?? throw new InvalidOperationException("Настройки резервного копирования пусты.");
            var secrets = JsonSerializer.Deserialize<StoredBackupSecrets>(
                protector.Unprotect(entity.ProtectedSecrets), Json)
                ?? new StoredBackupSecrets(null, null);
            return new BackupOptions
            {
                Enabled = settings.Enabled,
                IntervalHours = settings.IntervalHours,
                RetentionDays = settings.RetentionDays,
                HistoryRetentionDays = settings.HistoryRetentionDays,
                MaxTelegramFileSizeMb = settings.MaxTelegramFileSizeMb,
                Directory = configured.Value.Directory,
                EncryptionKey = configured.Value.EncryptionKey,
                TelegramBotToken = secrets.TelegramBotToken,
                TelegramChatId = secrets.TelegramChatId,
                TelegramRecipientId = settings.TelegramRecipientId,
                SendToObjectStorage = settings.SendToObjectStorage,
                ObjectStorageEndpoint = settings.ObjectStorageEndpoint,
                ObjectStorageRegion = settings.ObjectStorageRegion ?? "ru-central1",
                ObjectStorageBucket = settings.ObjectStorageBucket,
                ObjectStoragePrefix = settings.ObjectStoragePrefix ?? "proxyharbor/backups",
                ObjectStorageUsePathStyle = settings.ObjectStorageUsePathStyle,
                ObjectStorageAccessKey = secrets.ObjectStorageAccessKey,
                ObjectStorageSecretKey = secrets.ObjectStorageSecretKey
            };
        }
        catch (Exception exception) when (exception is JsonException or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException(
                "Сохранённые настройки резервного копирования повреждены или больше не расшифровываются.", exception);
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(BackupOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var entity = await db.BackupConfigurations.SingleOrDefaultAsync(x => x.Id == SingletonId, token);
        if (entity is null)
        {
            entity = new BackupConfiguration { Id = SingletonId };
            db.BackupConfigurations.Add(entity);
        }
        entity.SettingsJson = JsonSerializer.Serialize(new StoredBackupSettings(
            options.Enabled, options.IntervalHours, options.RetentionDays,
            options.HistoryRetentionDays, options.MaxTelegramFileSizeMb,
            options.TelegramRecipientId, options.SendToObjectStorage,
            options.ObjectStorageEndpoint, options.ObjectStorageRegion,
            options.ObjectStorageBucket, options.ObjectStoragePrefix,
            options.ObjectStorageUsePathStyle), Json);
        entity.ProtectedSecrets = protector.Protect(JsonSerializer.Serialize(new StoredBackupSecrets(
            options.TelegramBotToken, options.TelegramChatId,
            options.ObjectStorageAccessKey, options.ObjectStorageSecretKey), Json));
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
    }

    private BackupOptions CloneConfigured() => new()
    {
        Enabled = configured.Value.Enabled,
        IntervalHours = configured.Value.IntervalHours,
        Directory = configured.Value.Directory,
        RetentionDays = configured.Value.RetentionDays,
        HistoryRetentionDays = configured.Value.HistoryRetentionDays,
        EncryptionKey = configured.Value.EncryptionKey,
        TelegramBotToken = configured.Value.TelegramBotToken,
        TelegramChatId = configured.Value.TelegramChatId,
        TelegramRecipientId = configured.Value.TelegramRecipientId,
        MaxTelegramFileSizeMb = configured.Value.MaxTelegramFileSizeMb,
        SendToObjectStorage = configured.Value.SendToObjectStorage,
        ObjectStorageEndpoint = configured.Value.ObjectStorageEndpoint,
        ObjectStorageRegion = configured.Value.ObjectStorageRegion,
        ObjectStorageBucket = configured.Value.ObjectStorageBucket,
        ObjectStoragePrefix = configured.Value.ObjectStoragePrefix,
        ObjectStorageUsePathStyle = configured.Value.ObjectStorageUsePathStyle,
        ObjectStorageAccessKey = configured.Value.ObjectStorageAccessKey,
        ObjectStorageSecretKey = configured.Value.ObjectStorageSecretKey
    };

    private sealed record StoredBackupSettings(
        bool Enabled, int IntervalHours, int RetentionDays,
        int HistoryRetentionDays, int MaxTelegramFileSizeMb,
        Guid? TelegramRecipientId = null,
        bool SendToObjectStorage = false,
        string? ObjectStorageEndpoint = null,
        string? ObjectStorageRegion = null,
        string? ObjectStorageBucket = null,
        string? ObjectStoragePrefix = null,
        bool ObjectStorageUsePathStyle = true);
    private sealed record StoredBackupSecrets(
        string? TelegramBotToken,
        string? TelegramChatId,
        string? ObjectStorageAccessKey = null,
        string? ObjectStorageSecretKey = null);
}

/// <summary>Актуальные реквизиты основного бота и выбранного CRM-диалога.</summary>
public sealed record TelegramBackupDelivery(
    Guid RecipientId,
    string BotToken,
    string ChatId,
    string DisplayName,
    string? Username);

/// <summary>Разрешает backup-получателя без копирования bot token в настройки backup.</summary>
public interface ITelegramBackupDeliveryResolver
{
    /// <summary>Проверяет основной бот и возвращает реквизиты выбранного активного диалога.</summary>
    Task<TelegramBackupDelivery> ResolveAsync(Guid recipientId, CancellationToken token = default);
}

/// <summary>
/// Доставляет backup через runtime-транспорт Telegram. Реализация API использует те же
/// SOCKS5-маршруты, failover и circuit breaker, что сообщения commerce-бота.
/// </summary>
public interface ITelegramBackupTransport
{
    /// <summary>Отправляет один опубликованный backup-файл выбранному Telegram-получателю.</summary>
    Task SendAsync(
        string path,
        string caption,
        string botToken,
        string chatId,
        CancellationToken token);
}

/// <summary>Загружает уже зашифрованный PHB3-файл и подтверждает его размер/хэш в object storage.</summary>
public interface IBackupObjectStorageTransport
{
    /// <summary>Возвращает безопасный object key только после успешной проверки метаданных.</summary>
    Task<string> UploadAndVerifyAsync(string path, BackupOptions options, CancellationToken token);
}
