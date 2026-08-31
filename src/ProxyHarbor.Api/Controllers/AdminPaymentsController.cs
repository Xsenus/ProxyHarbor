using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Безопасное runtime-управление тарифами и восемью платёжными шлюзами.</summary>
[ApiController, Route("api/v1/admin/payments"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminPaymentsController(
    IPaymentConfigurationStore configurations,
    ITelegramBotConfigurationStore telegramConfigurations,
    ProxyHarborDbContext db) : ControllerBase
{
    /// <summary>Возвращает настройки и только признаки наличия секретов.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        var options = await configurations.GetAsync(token);
        var configurationUpdatedAt = await ConfigurationUpdatedAtAsync(token);
        return Ok(ToResponse(options, await OperationalSummariesAsync(configurationUpdatedAt, token), configurationUpdatedAt));
    }

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
        var annualDiscount = SubscriptionPricingPolicy.Periods.Single(x => x.Days == 365).DefaultDiscountPercent;
        if (request.Products.Any(x => x.Plan != SubscriptionPlans.Unlimited || x.DiscountPercent is < 0 or >= 100) ||
            request.Products.Single(x => x.DurationDays == 1).DiscountPercent != 0 ||
            request.Products.Single(x => x.DurationDays == 365).DiscountPercent != annualDiscount)
            return Invalid($"Тарифы должны давать Unlimited-доступ; скидка дня — 0%, года — {annualDiscount:0.###}%, остальные — от 0% до 99%.");
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

        var telegram = await telegramConfigurations.GetAsync(token);
        var telegramReady = telegram.Ready && products.Any(product =>
            product.Value.Enabled && TelegramStarsPricing.TryResolve(
                telegram, product.Key, product.Value, out _));
        var providerReady = providers.Any(pair =>
            PaymentProviderConfiguration.IsReady(pair.Key, pair.Value));
        if (request.Enabled && (!products.Values.Any(x => x.Enabled) ||
            (!providerReady && !telegramReady)))
            return Invalid("Для включения оплаты нужен активный тариф и хотя бы один готовый шлюз или Telegram Stars.");

        var next = new PaymentOptions
        {
            Enabled = request.Enabled,
            PublicBaseUrl = current.PublicBaseUrl,
            Products = products,
            Providers = providers
        };
        await configurations.SaveAsync(next, token);
        var configurationUpdatedAt = await ConfigurationUpdatedAtAsync(token);
        return Ok(ToResponse(next, await OperationalSummariesAsync(configurationUpdatedAt, token), configurationUpdatedAt));
    }

    private Task<DateTimeOffset?> ConfigurationUpdatedAtAsync(CancellationToken token) =>
        db.PaymentConfigurations.AsNoTracking().Where(configuration => configuration.Id == 1)
            .Select(configuration => (DateTimeOffset?)configuration.UpdatedAt).SingleOrDefaultAsync(token);

    private async Task<IReadOnlyDictionary<string, PaymentProviderOperationalSummary>> OperationalSummariesAsync(
        DateTimeOffset? configurationUpdatedAt,
        CancellationToken token)
    {
        var configuredSince = configurationUpdatedAt ?? DateTimeOffset.MaxValue;
        var values = await db.PaymentOrders.AsNoTracking()
            .Where(order => PaymentProviderConfiguration.Codes.Contains(order.Provider))
            .GroupBy(order => order.Provider)
            .Select(group => new PaymentProviderOperationalSummary(
                group.Key,
                group.Count(),
                group.Count(order => order.Status == PaymentStatuses.Pending),
                group.Count(order => order.Status == PaymentStatuses.Paid),
                group.Count(order => order.Status == PaymentStatuses.Failed),
                group.Count(order => order.Status == PaymentStatuses.Canceled),
                group.Count(order => order.Status == PaymentStatuses.Refunded),
                group.Count(order => order.Status == PaymentStatuses.Paid && order.CreatedAt >= configuredSince),
                group.Max(order => (DateTimeOffset?)order.CreatedAt),
                group.Max(order => order.PaidAt)))
            .ToArrayAsync(token);
        return values.ToDictionary(value => value.Provider, StringComparer.OrdinalIgnoreCase);
    }

    private static object ToResponse(
        PaymentOptions options,
        IReadOnlyDictionary<string, PaymentProviderOperationalSummary> operational,
        DateTimeOffset? configurationUpdatedAt) => new
        {
            options.Enabled,
            configurationUpdatedAt,
            products = options.Products.OrderBy(x => x.Key).Select(x => new
            {
                code = x.Key,
                x.Value.Enabled,
                x.Value.Name,
                x.Value.Plan,
                x.Value.DurationDays,
                x.Value.AmountMinor,
                x.Value.DiscountPercent,
                x.Value.Currency,
                x.Value.Description,
                fullDailyPriceMinor = checked(x.Value.DurationDays * options.Products.Values
                    .Single(product => product.DurationDays == 1).AmountMinor),
                savingsMinor = checked(x.Value.DurationDays * options.Products.Values
                    .Single(product => product.DurationDays == 1).AmountMinor) - x.Value.AmountMinor
            }),
            providers = PaymentProviderConfiguration.Codes.Select(code =>
            {
                options.Providers.TryGetValue(code, out var value);
                value ??= new PaymentProviderOptions { DisplayName = ProviderName(code) };
                operational.TryGetValue(code, out var summary);
                summary ??= PaymentProviderOperationalSummary.Empty(code);
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
                    webhookUrl = $"{options.PublicBaseUrl.TrimEnd('/')}/api/v1/payments/webhooks/{code}",
                    operational = PaymentProviderOperationalHealth.Create(code, value, summary)
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
        "yookassa" => "ЮKassa",
        "yoomoney" => "ЮMoney",
        "cloudpayments" => "CloudPayments",
        "robokassa" => "Robokassa",
        "tbank" => "Т-Банк",
        "stripe" => "Stripe",
        "cryptomus" => "Cryptomus",
        "nowpayments" => "NOWPayments",
        _ => code
    };
}

/// <summary>Агрегированная эксплуатационная статистика шлюза без данных клиента и платёжных идентификаторов.</summary>
internal sealed record PaymentProviderOperationalSummary(
    string Provider,
    int TotalOrders,
    int PendingOrders,
    int PaidOrders,
    int FailedOrders,
    int CanceledOrders,
    int RefundedOrders,
    int PaidAfterConfigurationUpdate,
    DateTimeOffset? LastOrderAt,
    DateTimeOffset? LastPaidAt)
{
    internal static PaymentProviderOperationalSummary Empty(string provider) =>
        new(provider, 0, 0, 0, 0, 0, 0, 0, null, null);
}

/// <summary>Не выдаёт предположение за подтверждённый callback, но явно показывает отсутствие успешной оплаты.</summary>
internal static class PaymentProviderOperationalHealth
{
    private static readonly HashSet<string> DirectReconciliationProviders =
        ["yookassa", "cloudpayments", "robokassa", "tbank", "stripe", "cryptomus"];

    internal static object Create(
        string code,
        PaymentProviderOptions provider,
        PaymentProviderOperationalSummary summary)
    {
        var ready = PaymentProviderConfiguration.IsReady(code, provider);
        var state = !provider.Enabled ? "disabled"
            : !ready ? "configuration_required"
            : summary.PaidAfterConfigurationUpdate > 0 ? "healthy"
            : summary.PendingOrders > 0 ? "pending"
            : summary.PaidOrders > 0 ? "retest_required"
            : summary.TotalOrders == 0 ? "awaiting_first_payment"
            : code is "yoomoney" or "nowpayments" ? "webhook_attention"
            : "no_successful_payments";
        var attention = state switch
        {
            "retest_required" =>
                "После последнего сохранения настроек ещё не было подтверждённой оплаты. Выполните минимальный production-платёж и проверьте начисление подписки.",
            "webhook_attention" when code == "yoomoney" =>
                "Нет подтверждённых оплат. Включите HTTP-уведомления в кошельке ЮMoney, укажите этот webhook URL и тот же секрет, затем выполните тест из кабинета ЮMoney.",
            "webhook_attention" =>
                "Нет подтверждённых оплат. Проверьте IPN/webhook в кабинете провайдера: этот шлюз нельзя безопасно сверить без входящего уведомления.",
            "no_successful_payments" =>
                "Счета создавались, но подтверждённых оплат пока нет. Проверьте webhook и журнал счетов.",
            _ => null
        };
        return new
        {
            state,
            attention,
            totalOrders = summary.TotalOrders,
            pendingOrders = summary.PendingOrders,
            paidOrders = summary.PaidOrders,
            failedOrders = summary.FailedOrders,
            canceledOrders = summary.CanceledOrders,
            refundedOrders = summary.RefundedOrders,
            paidAfterConfigurationUpdate = summary.PaidAfterConfigurationUpdate,
            lastOrderAt = summary.LastOrderAt,
            lastPaidAt = summary.LastPaidAt,
            webhookRequired = true,
            directReconciliationSupported = DirectReconciliationProviders.Contains(code)
        };
    }
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
    [Range(typeof(decimal), "0", "99.99")] public decimal DiscountPercent { get; set; }
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
