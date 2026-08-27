using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Публично сообщает только факт доступности безопасных способов оформления подписки.</summary>
[ApiController, Route("api/v1/commerce"), EnableRateLimiting("public")]
public sealed class CommerceController(
    IPaymentConfigurationStore payments,
    ITelegramBotConfigurationStore telegram,
    IFreeExportAccessService access) : ControllerBase
{
    /// <summary>Возвращает состояние предложения без платёжных реквизитов и секретов.</summary>
    [HttpGet("availability")]
    public async Task<IActionResult> Availability(CancellationToken token)
    {
        var paymentOptions = await payments.GetAsync(token);
        var telegramOptions = await telegram.GetAsync(token);
        var hasProduct = paymentOptions.Products.Any(product =>
            product.Value.Enabled && product.Value.AmountMinor > 0 &&
            product.Value.Plan is SubscriptionPlans.Pro or SubscriptionPlans.Unlimited);
        var providers = hasProduct && paymentOptions.Enabled
            ? PaymentProviderConfiguration.Codes.Count(code =>
                paymentOptions.Providers.TryGetValue(code, out var provider) &&
                PaymentProviderConfiguration.IsReady(code, provider))
            : 0;
        var telegramReady = paymentOptions.Enabled && hasProduct && telegramOptions.Ready && paymentOptions.Products.Any(product =>
            product.Value.Enabled && TelegramStarsPricing.TryResolve(
                telegramOptions, product.Key, product.Value, out _));
        var fullAccess = await access.HasPaidAccessAsync(User, token);
        var available = providers > 0 || telegramReady;

        return Ok(new
        {
            available,
            showOffer = available && !fullAccess,
            fullAccess,
            paymentProviders = providers,
            telegram = telegramReady,
            accountUrl = "/account"
        });
    }
}
