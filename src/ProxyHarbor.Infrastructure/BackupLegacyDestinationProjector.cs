using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Идемпотентно проецирует legacy singleton-настройки в destination model. Legacy store
/// остаётся источником runtime-поведения, пока BackupRouting.Enabled=false.
/// </summary>
public sealed class BackupLegacyDestinationProjector(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IBackupConfigurationStore configurationStore,
    IDataProtectionProvider protectionProvider,
    IOptions<BackupRoutingOptions> routingOptions,
    BackupDestinationRegistry registry,
    ILogger<BackupLegacyDestinationProjector> logger)
{
    internal static readonly Guid LegacyPoolId = Guid.Parse("bba00000-0000-0000-0000-000000000001");
    internal static readonly Guid LegacyS3DestinationId = Guid.Parse("bba00000-0000-0000-0000-000000000002");
    internal static readonly Guid LegacyTelegramDestinationId = Guid.Parse("bba00000-0000-0000-0000-000000000003");
    private const string ProjectionLockStatement = "SELECT pg_advisory_xact_lock(5787767920693105153)";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, Exception?> ProjectionSkipped = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1501, "BackupDestinationProjectionSkipped"),
        "Legacy backup destination projection пропущена: сохранённая конфигурация недоступна, routing выключен.");
    private readonly IDataProtector protector = protectionProvider.CreateProtector(
        "ProxyHarbor.BackupDestination.Secrets.v1");

    /// <summary>Обновляет только deterministic projection rows и никогда не создаёт jobs/copies.</summary>
    public async Task ProjectAsync(CancellationToken token = default)
    {
        BackupOptions options;
        try
        {
            options = await configurationStore.GetAsync(token);
        }
        catch (InvalidOperationException exception) when (!routingOptions.Value.Enabled)
        {
            // До включения нового routing повреждённый/чужой DP key ring не должен
            // превращать совместимый upgrade в startup outage старого пути.
            ProjectionSkipped(logger, exception);
            return;
        }
        // Production enables NpgsqlRetryingExecutionStrategy. The complete transaction,
        // including the transaction-scoped advisory lock, must run inside its execution
        // scope and every retry must receive a fresh DbContext.
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(() => ProjectTransactionAsync(options, token));
    }

    private async Task ProjectTransactionAsync(BackupOptions options, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlRawAsync(ProjectionLockStatement, token);

        var s3Configured = options.SendToObjectStorage &&
            BackupOptions.IsObjectStorageConfigurationValid(options);
        var telegramConfigured = options.TelegramRecipientId.HasValue ||
            !string.IsNullOrWhiteSpace(options.TelegramBotToken) &&
            !string.IsNullOrWhiteSpace(options.TelegramChatId);
        var configuredCount = (s3Configured ? 1 : 0) + (telegramConfigured ? 1 : 0);

        var pool = await db.BackupPools.SingleOrDefaultAsync(x => x.Id == LegacyPoolId, token);
        if (pool is null)
        {
            pool = new BackupPool { Id = LegacyPoolId, Name = "legacy-default" };
            db.BackupPools.Add(pool);
        }
        pool.RequiredVerifiedCopies = 1;
        pool.DesiredVerifiedCopies = Math.Max(1, configuredCount);
        pool.MaxAttemptsPerCycle = 3;
        pool.OverallDeadlineSeconds = 600;
        pool.FailbackHealthyForSeconds = 900;
        pool.PolicyVersion = 1;

        var s3 = await UpsertDestinationAsync(db, LegacyS3DestinationId, "legacy-s3", "s3", token);
        var s3Capabilities = JsonSerializer.Serialize(registry.GetRequired("s3").Capabilities, Json);
        var s3Settings = JsonSerializer.Serialize(new
        {
            endpoint = options.ObjectStorageEndpoint,
            region = options.ObjectStorageRegion,
            bucket = options.ObjectStorageBucket,
            prefix = options.ObjectStoragePrefix,
            usePathStyle = options.ObjectStorageUsePathStyle
        }, Json);
        var s3Secrets = ProtectSecrets(s3.ProtectedSecrets, new
        {
            accessKey = options.ObjectStorageAccessKey,
            secretKey = options.ObjectStorageSecretKey
        }, options.ObjectStorageAccessKey, options.ObjectStorageSecretKey);
        UpdateDestination(
            s3,
            s3Configured,
            S3FailureDomain(options.ObjectStorageEndpoint, options.ObjectStorageRegion),
            priority: 10,
            s3Capabilities,
            s3Settings,
            s3Secrets);

        var telegram = await UpsertDestinationAsync(
            db, LegacyTelegramDestinationId, "legacy-telegram", "telegram", token);
        var telegramCapabilities = JsonSerializer.Serialize(
            registry.GetRequired("telegram").Capabilities, Json);
        var telegramSettings = JsonSerializer.Serialize(new
        {
            recipientId = options.TelegramRecipientId,
            maximumPartBytes = (long)options.MaxTelegramFileSizeMb * 1024 * 1024
        }, Json);
        var telegramSecrets = ProtectSecrets(telegram.ProtectedSecrets, new
        {
            botToken = options.TelegramBotToken,
            chatId = options.TelegramChatId
        }, options.TelegramBotToken, options.TelegramChatId);
        UpdateDestination(
            telegram,
            telegramConfigured,
            "telegram",
            priority: 20,
            telegramCapabilities,
            telegramSettings,
            telegramSecrets);

        await UpsertRouteAsync(
            db, s3, priority: 10, role: "primary", enabled: s3Configured, "put,verify,read", token);
        await UpsertRouteAsync(
            db,
            telegram,
            priority: 20,
            role: s3Configured ? "fallback" : "primary",
            enabled: telegramConfigured,
            "put",
            token);

        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private static async Task<BackupDestination> UpsertDestinationAsync(
        ProxyHarborDbContext db,
        Guid id,
        string name,
        string kind,
        CancellationToken token)
    {
        var destination = await db.BackupDestinations.SingleOrDefaultAsync(x => x.Id == id, token);
        if (destination is null)
        {
            destination = new BackupDestination
            {
                Id = id,
                Name = name,
                Kind = kind,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.BackupDestinations.Add(destination);
        }
        else if (!string.Equals(destination.Name, name, StringComparison.Ordinal) ||
            !string.Equals(destination.Kind, kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Legacy backup projection identity повреждена.");
        }
        return destination;
    }

    private static void UpdateDestination(
        BackupDestination destination,
        bool enabled,
        string failureDomain,
        int priority,
        string capabilitiesJson,
        string settingsJson,
        string protectedSecrets)
    {
        if (destination.Enabled == enabled &&
            string.Equals(destination.FailureDomain, failureDomain, StringComparison.Ordinal) &&
            destination.Priority == priority &&
            JsonEquivalent(destination.CapabilitiesJson, capabilitiesJson) &&
            JsonEquivalent(destination.SettingsJson, settingsJson) &&
            string.Equals(destination.ProtectedSecrets, protectedSecrets, StringComparison.Ordinal))
            return;

        destination.Enabled = enabled;
        destination.FailureDomain = failureDomain;
        destination.Priority = priority;
        destination.CapabilitiesJson = capabilitiesJson;
        destination.SettingsJson = settingsJson;
        destination.ProtectedSecrets = protectedSecrets;
        destination.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static bool JsonEquivalent(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static async Task UpsertRouteAsync(
        ProxyHarborDbContext db,
        BackupDestination destination,
        int priority,
        string role,
        bool enabled,
        string allowedOperations,
        CancellationToken token)
    {
        var route = await db.BackupPoolDestinations.SingleOrDefaultAsync(
            x => x.BackupPoolId == LegacyPoolId && x.BackupDestinationId == destination.Id, token);
        if (route is null)
        {
            route = new BackupPoolDestination
            {
                BackupPoolId = LegacyPoolId,
                BackupDestinationId = destination.Id
            };
            db.BackupPoolDestinations.Add(route);
        }
        route.Priority = priority;
        route.Role = role;
        route.Enabled = enabled;
        route.Draining = false;
        route.AllowedOperations = allowedOperations;
    }

    private string ProtectSecrets<T>(string existing, T value, params string?[] secrets)
    {
        if (!secrets.Any(secret => !string.IsNullOrWhiteSpace(secret))) return string.Empty;
        var plaintext = JsonSerializer.Serialize(value, Json);
        if (!string.IsNullOrEmpty(existing))
        {
            try
            {
                if (string.Equals(protector.Unprotect(existing), plaintext, StringComparison.Ordinal))
                    return existing;
            }
            catch (CryptographicException)
            {
                // Legacy store уже успешно расшифровал актуальные credentials. Повреждённый
                // projection ciphertext безопасно заменяется новым, не раскрывая plaintext.
            }
        }
        return protector.Protect(plaintext);
    }

    private static string S3FailureDomain(string? endpoint, string? region)
    {
        var host = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.IdnHost
            : "unconfigured";
        var value = $"s3:{host}:{region ?? "unconfigured"}";
        return value.Length <= 120
            ? value
            : $"s3:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";
    }
}
