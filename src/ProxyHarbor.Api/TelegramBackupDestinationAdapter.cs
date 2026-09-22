using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Adapter поверх существующих resolver и Telegram runtime transport.</summary>
public sealed class TelegramBackupDestinationAdapter : IBackupDestinationAdapter
{
    private const int MaximumParts = 20;
    private const long MaximumPartBytes = 49L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ITelegramBackupDeliveryResolver? resolver;
    private readonly ITelegramBackupTransport? transport;
    private readonly IDataProtector? protector;

    /// <summary>Metadata-only constructor для tooling/tests без provider I/O.</summary>
    public TelegramBackupDestinationAdapter() { }

    /// <summary>Production adapter использует существующие native Telegram contracts.</summary>
    public TelegramBackupDestinationAdapter(
        ITelegramBackupDeliveryResolver resolver,
        ITelegramBackupTransport transport,
        IDataProtectionProvider protectionProvider)
    {
        this.resolver = resolver;
        this.transport = transport;
        protector = protectionProvider.CreateProtector("ProxyHarbor.BackupDestination.Secrets.v1");
    }

    /// <inheritdoc />
    public string Kind => "telegram";

    /// <inheritdoc />
    public BackupDestinationCapabilities Capabilities { get; } = new(
        Put: new(true, MaximumPartBytes * MaximumParts, MaximumParts),
        Verify: new(false),
        Materialize: new(false),
        SupportsConditionalCreate: false,
        ProvidesNativeVersion: false,
        ProvidesNativeChecksum: false);

    /// <summary>
    /// Отправляет все части через существующий transport. Результат означает только
    /// подтверждённое Bot API ok=true для каждой части, но не independent verification.
    /// </summary>
    public async Task<TelegramBackupDestinationWriteResult> PutAsync(
        BackupDestination destination,
        string path,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(destination.Kind, Kind, StringComparison.Ordinal) ||
            resolver is null || transport is null || protector is null)
            throw Failure(
                BackupDestinationErrorCode.InvalidConfiguration,
                BackupDestinationFailureDisposition.Permanent);
        var settings = Deserialize<TelegramSettings>(destination.SettingsJson);
        var partLimit = settings.MaximumPartBytes is > 0 and <= MaximumPartBytes
            ? settings.MaximumPartBytes.Value
            : MaximumPartBytes;
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Зашифрованный backup-файл не найден.", path);

        var sentParts = 0;
        try
        {
            _ = BackupFileSplitter.RequiredPartCount(file.Length, partLimit, MaximumParts);
            TelegramBackupDelivery delivery;
            if (settings.RecipientId.HasValue)
            {
                delivery = await resolver.ResolveAsync(settings.RecipientId.Value, token);
            }
            else
            {
                var secrets = Unprotect(destination.ProtectedSecrets);
                if (string.IsNullOrWhiteSpace(secrets.BotToken) || string.IsNullOrWhiteSpace(secrets.ChatId))
                    throw Failure(
                        BackupDestinationErrorCode.InvalidConfiguration,
                        BackupDestinationFailureDisposition.Permanent);
                delivery = new TelegramBackupDelivery(
                    Guid.Empty, secrets.BotToken, secrets.ChatId, "legacy", null);
            }
            if (file.Length <= partLimit)
            {
                await transport.SendAsync(
                    path,
                    "ProxyHarbor: зашифрованная резервная копия",
                    delivery.BotToken,
                    delivery.ChatId,
                    token);
                return new(1);
            }

            await foreach (var part in BackupFileSplitter.SplitAsync(
                path, partLimit, MaximumParts, token))
            {
                await transport.SendAsync(
                    part.Path,
                    $"ProxyHarbor backup — часть {part.Number}/{part.Total}",
                    delivery.BotToken,
                    delivery.ChatId,
                    token);
                sentParts++;
            }
            return new(sentParts);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException("Telegram backup operation отменена.", token);
        }
        catch (BackupDestinationOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = sentParts > 0
                ? new BackupDestinationFailure(
                    BackupDestinationErrorCode.UnknownOutcome,
                    BackupDestinationFailureDisposition.UnknownOutcome)
                : ClassifyProviderFailure(exception);
            throw new BackupDestinationOperationException(
                failure,
                $"Telegram backup operation завершилась ошибкой '{failure.Code}'.");
        }
    }

    internal static BackupDestinationFailure ClassifyProviderFailure(Exception exception)
    {
        if (exception is BackupDeliveryPolicyException)
            return new(BackupDestinationErrorCode.QuotaExceeded,
                BackupDestinationFailureDisposition.Permanent);
        if (exception is HttpRequestException http)
        {
            return http.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new(BackupDestinationErrorCode.AuthenticationFailed,
                    BackupDestinationFailureDisposition.Permanent),
                HttpStatusCode.Forbidden => new(BackupDestinationErrorCode.AuthorizationFailed,
                    BackupDestinationFailureDisposition.Permanent),
                HttpStatusCode.TooManyRequests => new(BackupDestinationErrorCode.RateLimited,
                    BackupDestinationFailureDisposition.Retryable),
                >= HttpStatusCode.InternalServerError => new(BackupDestinationErrorCode.Unavailable,
                    BackupDestinationFailureDisposition.Retryable),
                null => new(BackupDestinationErrorCode.UnknownOutcome,
                    BackupDestinationFailureDisposition.UnknownOutcome),
                _ => new(BackupDestinationErrorCode.ProviderRejected,
                    BackupDestinationFailureDisposition.Permanent)
            };
        }
        return new(BackupDestinationErrorCode.ProviderRejected,
            BackupDestinationFailureDisposition.Permanent);
    }

    private TelegramSecrets Unprotect(string protectedSecrets)
    {
        if (string.IsNullOrWhiteSpace(protectedSecrets)) return new(null, null);
        try
        {
            return Deserialize<TelegramSecrets>(protector!.Unprotect(protectedSecrets));
        }
        catch (CryptographicException)
        {
            throw Failure(
                BackupDestinationErrorCode.InvalidConfiguration,
                BackupDestinationFailureDisposition.Permanent);
        }
    }

    private static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Failure(
                BackupDestinationErrorCode.InvalidConfiguration,
                BackupDestinationFailureDisposition.Permanent);
        }
    }

    private static BackupDestinationOperationException Failure(
        BackupDestinationErrorCode code,
        BackupDestinationFailureDisposition disposition) => new(
            new(code, disposition),
            $"Telegram backup operation завершилась ошибкой '{code}'.");

    private sealed record TelegramSettings(Guid? RecipientId, long? MaximumPartBytes);
    private sealed record TelegramSecrets(string? BotToken, string? ChatId);
}

/// <summary>Число частей, каждая из которых получила Bot API ok=true.</summary>
public sealed record TelegramBackupDestinationWriteResult(int ConfirmedParts);
