using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Безопасное runtime-управление тарифами и восемью платёжными шлюзами.</summary>
[ApiController, Route("api/v1/admin/payments"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminPaymentsController(IPaymentConfigurationStore configurations) : ControllerBase
{
    /// <summary>Возвращает настройки и только признаки наличия секретов.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token) =>
        Ok(ToResponse(await configurations.GetAsync(token)));

    /// <summary>Проверяет и атомарно применяет полный снимок настроек без рестарта.</summary>
    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdatePaymentSettingsRequest request, CancellationToken token)
    {
        if (request.Products.Count != SubscriptionPricingPolicy.Periods.Count ||
            request.Providers.Count != PaymentProviderConfiguration.Codes.Length)
            return Invalid("Передайте шесть периодов подписки и настройки всех поддерживаемых провайдеров.");
        if (request.Products.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Products.Count ||
            request.Providers.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Providers.Count)
            return Invalid("Коды тарифов и провайдеров не должны повторяться.");

        var expectedDurations = SubscriptionPricingPolicy.Periods.Select(x => x.Days).ToHashSet();
        if (!request.Products.Select(x => x.DurationDays).ToHashSet().SetEquals(expectedDurations))
            return Invalid("Допустимые сроки: 1, 7, 30, 90, 180 и 365 дней — каждый ровно один раз.");
        if (request.Products.Any(x => x.Plan != SubscriptionPlans.Unlimited || x.DiscountPercent is < 0 or > 20) ||
            request.Products.Single(x => x.DurationDays == 1).DiscountPercent != 0 ||
            request.Products.Single(x => x.DurationDays == 365).DiscountPercent != 20)
            return Invalid("Тарифы должны давать Unlimited-доступ; скидка дня — 0%, года — 20%, остальные — от 0% до 20%.");
        var orderedDiscounts = request.Products.OrderBy(x => x.DurationDays).Select(x => x.DiscountPercent).ToArray();
        if (!orderedDiscounts.SequenceEqual(orderedDiscounts.OrderBy(x => x)))
            return Invalid("Скидка не должна уменьшаться при увеличении срока подписки.");

        var current = await configurations.GetAsync(token);
        var daily = request.Products.Single(x => x.DurationDays == 1);
        var currency = daily.Currency.Trim().ToUpperInvariant();
        if (request.Products.Any(x => !string.Equals(x.Currency.Trim(), currency, StringComparison.OrdinalIgnoreCase)))
            return Invalid("Все периоды должны использовать одну валюту.");
        Dictionary<string, PaymentProductOptions> products;
        try
        {
            products = SubscriptionPricingPolicy.Build(daily.AmountMinor, currency,
                request.Products.ToDictionary(x => x.DurationDays, x => x.DiscountPercent));
        }
        catch (ArgumentException) { return Invalid("Базовая дневная цена, валюта или скидки некорректны."); }
        foreach (var item in request.Products)
        {
            var code = item.Code.Trim().ToLowerInvariant();
            var period = SubscriptionPricingPolicy.Periods.Single(x => x.Days == item.DurationDays);
            if (!string.Equals(code, $"unlimited-{period.Code}", StringComparison.Ordinal) ||
                item.AmountMinor is < 1 or > 1_000_000_000 || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
                return Invalid($"Тариф «{item.Code}» содержит недопустимые параметры.");
            var calculated = products[code];
            calculated.Enabled = item.Enabled;
            calculated.Name = item.Name.Trim();
            calculated.Description = item.Description.Trim();
        }

        var providers = new Dictionary<string, PaymentProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Providers)
        {
            var code = item.Code.Trim().ToLowerInvariant();
            if (!PaymentProviderConfiguration.Codes.Contains(code, StringComparer.Ordinal))
                return Invalid($"Провайдер «{item.Code}» не поддерживается.");
            current.Providers.TryGetValue(code, out var previous);
            var primary = MergeSecret(previous?.SecretKey, item.SecretKey, item.ClearSecretKey);
            var secondary = MergeSecret(previous?.SecondarySecret, item.SecondarySecret, item.ClearSecondarySecret);
            if (!ValidSecret(primary) || !ValidSecret(secondary))
                return Invalid($"Секрет провайдера «{item.Code}» превышает лимит или содержит управляющие символы.");
            var provider = new PaymentProviderOptions
            {
                Enabled = item.Enabled,
                DisplayName = ProviderName(code),
                MerchantId = item.MerchantId.Trim(),
                PublicId = item.PublicId.Trim(),
                SecretKey = primary,
                SecondarySecret = secondary,
                TestMode = item.TestMode
            };
            if (provider.Enabled && !PaymentProviderConfiguration.IsReady(code, provider))
                return Invalid($"Заполните обязательные реквизиты провайдера «{ProviderName(code)}» перед включением.");
            providers[code] = provider;
        }

        if (request.Enabled && (!products.Values.Any(x => x.Enabled) ||
            !providers.Any(pair => PaymentProviderConfiguration.IsReady(pair.Key, pair.Value))))
            return Invalid("Для включения оплаты нужен хотя бы один активный тариф и полностью настроенный провайдер.");

        var next = new PaymentOptions
        {
            Enabled = request.Enabled,
            PublicBaseUrl = current.PublicBaseUrl,
            Products = products,
            Providers = providers
        };
        await configurations.SaveAsync(next, token);
        return Ok(ToResponse(next));
    }

    private static object ToResponse(PaymentOptions options) => new
    {
        options.Enabled,
        products = options.Products.OrderBy(x => x.Key).Select(x => new
        {
            code = x.Key, x.Value.Enabled, x.Value.Name, x.Value.Plan, x.Value.DurationDays,
            x.Value.AmountMinor, x.Value.DiscountPercent, x.Value.Currency, x.Value.Description,
            fullDailyPriceMinor = checked(x.Value.DurationDays * options.Products.Values
                .Single(product => product.DurationDays == 1).AmountMinor),
            savingsMinor = checked(x.Value.DurationDays * options.Products.Values
                .Single(product => product.DurationDays == 1).AmountMinor) - x.Value.AmountMinor
        }),
        providers = PaymentProviderConfiguration.Codes.Select(code =>
        {
            options.Providers.TryGetValue(code, out var value);
            value ??= new PaymentProviderOptions { DisplayName = ProviderName(code) };
            return new
            {
                code,
                name = ProviderName(code),
                value.Enabled,
                value.MerchantId,
                value.PublicId,
                value.TestMode,
                secretConfigured = !string.IsNullOrWhiteSpace(value.SecretKey),
                secondarySecretConfigured = !string.IsNullOrWhiteSpace(value.SecondarySecret),
                ready = PaymentProviderConfiguration.IsReady(code, value),
                webhookUrl = $"{options.PublicBaseUrl.TrimEnd('/')}/api/v1/payments/webhooks/{code}"
            };
        })
    };

    private static string MergeSecret(string? previous, string? replacement, bool clear) =>
        clear ? string.Empty : replacement is null ? previous ?? string.Empty : replacement;
    private static bool ValidSecret(string value) => value.Length <= 4096 && !value.Any(char.IsControl);
    private static bool ValidCode(string value) => value.Length is >= 2 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static BadRequestObjectResult Invalid(string title) => new(new ProblemDetails { Title = title, Status = 400 });
    private static string ProviderName(string code) => code switch
    {
        "yookassa" => "ЮKassa", "yoomoney" => "ЮMoney", "cloudpayments" => "CloudPayments",
        "robokassa" => "Robokassa", "tbank" => "Т-Банк", "stripe" => "Stripe",
        "cryptomus" => "Cryptomus", "nowpayments" => "NOWPayments", _ => code
    };
}

/// <summary>Полный административный снимок биллинга.</summary>
public sealed class UpdatePaymentSettingsRequest
{
    /// <summary>Глобально разрешить новые checkout.</summary>
    public bool Enabled { get; set; }
    /// <summary>Полный каталог тарифов.</summary>
    [Required, MinLength(1), MaxLength(10)] public List<UpdatePaymentProductRequest> Products { get; set; } = [];
    /// <summary>Настройки всех восьми шлюзов.</summary>
    [Required, MinLength(8), MaxLength(8)] public List<UpdatePaymentProviderRequest> Providers { get; set; } = [];
}

/// <summary>Редактируемый тариф в минимальных единицах валюты.</summary>
public sealed class UpdatePaymentProductRequest
{
    /// <summary>Стабильный машинный код.</summary>
    [Required, StringLength(64, MinimumLength = 2)] public string Code { get; set; } = string.Empty;
    /// <summary>Доступность тарифа пользователям.</summary>
    public bool Enabled { get; set; }
    /// <summary>Публичное название.</summary>
    [Required, StringLength(120, MinimumLength = 2)] public string Name { get; set; } = string.Empty;
    /// <summary>Активируемый внутренний план.</summary>
    [Required, StringLength(32)] public string Plan { get; set; } = string.Empty;
    /// <summary>Оплачиваемый срок.</summary>
    [Range(1, 3660)] public int DurationDays { get; set; }
    /// <summary>Цена в копейках/центах.</summary>
    [Range(1, 1_000_000_000)] public long AmountMinor { get; set; }
    /// <summary>Скидка относительно последовательной покупки каждого дня.</summary>
    [Range(typeof(decimal), "0", "20")] public decimal DiscountPercent { get; set; }
    /// <summary>Трёхбуквенная валюта ISO 4217.</summary>
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "RUB";
    /// <summary>Короткое описание возможностей.</summary>
    [StringLength(300)] public string Description { get; set; } = string.Empty;
}

/// <summary>Несекретные реквизиты и команды замены/очистки секретов одного шлюза.</summary>
public sealed class UpdatePaymentProviderRequest
{
    /// <summary>Один из восьми поддерживаемых кодов.</summary>
    [Required, StringLength(32)] public string Code { get; set; } = string.Empty;
    /// <summary>Разрешить создание платежей через шлюз.</summary>
    public bool Enabled { get; set; }
    /// <summary>Shop ID, кошелёк, MerchantLogin, TerminalKey или Merchant UUID.</summary>
    [StringLength(256)] public string MerchantId { get; set; } = string.Empty;
    /// <summary>Публичный идентификатор CloudPayments.</summary>
    [StringLength(256)] public string PublicId { get; set; } = string.Empty;
    /// <summary>Тестовый режим провайдера.</summary>
    public bool TestMode { get; set; }
    /// <summary>Новый основной секрет; null сохраняет прежний.</summary>
    [StringLength(4096)] public string? SecretKey { get; set; }
    /// <summary>Новый второй секрет; null сохраняет прежний.</summary>
    [StringLength(4096)] public string? SecondarySecret { get; set; }
    /// <summary>Явно удалить основной секрет.</summary>
    public bool ClearSecretKey { get; set; }
    /// <summary>Явно удалить второй секрет.</summary>
    public bool ClearSecondarySecret { get; set; }
}
