using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Даёт каждому checkout/webhook актуальный снимок runtime-настроек биллинга.</summary>
public interface IPaymentConfigurationStore
{
    /// <summary>Читает эффективную конфигурацию без кэширования секретов.</summary>
    Task<PaymentOptions> GetAsync(CancellationToken token = default);
    /// <summary>Атомарно сохраняет полный проверенный снимок.</summary>
    Task SaveAsync(PaymentOptions options, CancellationToken token = default);
}

/// <summary>
/// Хранит открытые параметры в JSONB, а секреты — в Data Protection ciphertext.
/// Постоянный key-ring контейнера позволяет использовать настройки после рестарта.
/// </summary>
public sealed class PaymentConfigurationStore(
    ProxyHarborDbContext db,
    IOptions<PaymentOptions> configured,
    IDataProtectionProvider protectionProvider) : IPaymentConfigurationStore
{
    private const int SingletonId = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector = protectionProvider.CreateProtector(
        "ProxyHarbor.PaymentConfiguration.Secrets.v1");

    /// <inheritdoc />
    public async Task<PaymentOptions> GetAsync(CancellationToken token = default)
    {
        var entity = await db.PaymentConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == SingletonId, token);
        if (entity is null)
        {
            var defaults = Clone(configured.Value);
            defaults.Products = SubscriptionPricingPolicy.Normalize(defaults.Products);
            return defaults;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<StoredPaymentSettings>(entity.SettingsJson, Json)
                ?? throw new InvalidOperationException("Настройки платежей пусты.");
            var secretJson = protector.Unprotect(entity.ProtectedSecrets);
            var secrets = JsonSerializer.Deserialize<Dictionary<string, StoredProviderSecrets>>(secretJson, Json)
                ?? new Dictionary<string, StoredProviderSecrets>(StringComparer.OrdinalIgnoreCase);
            var result = new PaymentOptions
            {
                Enabled = settings.Enabled,
                // Origin остаётся deploy-настройкой: админка не может перенаправить webhook на чужой host.
                PublicBaseUrl = configured.Value.PublicBaseUrl,
                Products = settings.Products,
                Providers = settings.Providers.ToDictionary(
                    pair => pair.Key,
                    pair => new PaymentProviderOptions
                    {
                        Enabled = pair.Value.Enabled,
                        DisplayName = pair.Value.DisplayName,
                        MerchantId = pair.Value.MerchantId,
                        PublicId = pair.Value.PublicId,
                        TestMode = pair.Value.TestMode,
                        SecretKey = secrets.GetValueOrDefault(pair.Key)?.SecretKey ?? string.Empty,
                        SecondarySecret = secrets.GetValueOrDefault(pair.Key)?.SecondarySecret ?? string.Empty
                    }, StringComparer.OrdinalIgnoreCase)
            };
            result.Products = SubscriptionPricingPolicy.Normalize(result.Products);
            return result;
        }
        catch (Exception exception) when (exception is JsonException or System.Security.Cryptography.CryptographicException)
        {
            // Потеря key-ring или повреждение ciphertext всегда выключает checkout, а не раскрывает/игнорирует секрет.
            throw new InvalidOperationException("Сохранённые настройки платежей повреждены или больше не расшифровываются.", exception);
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(PaymentOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = new StoredPaymentSettings(
            options.Enabled,
            options.Products,
            options.Providers.ToDictionary(
                pair => pair.Key,
                pair => new StoredProviderSettings(
                    pair.Value.Enabled, pair.Value.DisplayName, pair.Value.MerchantId,
                    pair.Value.PublicId, pair.Value.TestMode),
                StringComparer.OrdinalIgnoreCase));
        var secrets = options.Providers.ToDictionary(
            pair => pair.Key,
            pair => new StoredProviderSecrets(pair.Value.SecretKey, pair.Value.SecondarySecret),
            StringComparer.OrdinalIgnoreCase);
        var entity = await db.PaymentConfigurations.SingleOrDefaultAsync(x => x.Id == SingletonId, token);
        if (entity is null)
        {
            entity = new PaymentConfiguration { Id = SingletonId };
            db.PaymentConfigurations.Add(entity);
        }
        entity.SettingsJson = JsonSerializer.Serialize(settings, Json);
        entity.ProtectedSecrets = protector.Protect(JsonSerializer.Serialize(secrets, Json));
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
    }

    private static PaymentOptions Clone(PaymentOptions value) => new()
    {
        Enabled = value.Enabled,
        PublicBaseUrl = value.PublicBaseUrl,
        Products = value.Products.ToDictionary(pair => pair.Key, pair => new PaymentProductOptions
        {
            Enabled = pair.Value.Enabled,
            Name = pair.Value.Name,
            Plan = pair.Value.Plan,
            DurationDays = pair.Value.DurationDays,
            AmountMinor = pair.Value.AmountMinor,
            DiscountPercent = pair.Value.DiscountPercent,
            Currency = pair.Value.Currency,
            Description = pair.Value.Description
        }, StringComparer.OrdinalIgnoreCase),
        Providers = value.Providers.ToDictionary(pair => pair.Key, pair => new PaymentProviderOptions
        {
            Enabled = pair.Value.Enabled,
            DisplayName = pair.Value.DisplayName,
            MerchantId = pair.Value.MerchantId,
            PublicId = pair.Value.PublicId,
            SecretKey = pair.Value.SecretKey,
            SecondarySecret = pair.Value.SecondarySecret,
            TestMode = pair.Value.TestMode
        }, StringComparer.OrdinalIgnoreCase)
    };

    private sealed record StoredPaymentSettings(
        bool Enabled,
        Dictionary<string, PaymentProductOptions> Products,
        Dictionary<string, StoredProviderSettings> Providers);
    private sealed record StoredProviderSettings(
        bool Enabled, string DisplayName, string MerchantId, string PublicId, bool TestMode);
    private sealed record StoredProviderSecrets(string SecretKey, string SecondarySecret);
}

/// <summary>Неизменяемый адаптер для unit-тестов и локальных вызовов шлюза.</summary>
internal sealed class StaticPaymentConfigurationStore(IOptions<PaymentOptions> configured) : IPaymentConfigurationStore
{
    public Task<PaymentOptions> GetAsync(CancellationToken token = default) =>
        Task.FromResult(configured.Value);
    public Task SaveAsync(PaymentOptions options, CancellationToken token = default) =>
        throw new NotSupportedException();
}
