using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Каталог подписок, hosted checkout и идемпотентная обработка webhooks.</summary>
[ApiController]
[Route("api/v1/payments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PaymentsController(
    UserManager<ApplicationUser> users,
    ProxyHarborDbContext db,
    PaymentGatewayClient gateways,
    IPaymentConfigurationStore configurations) : ControllerBase
{
    /// <summary>Совместимый статический конструктор для изолированных unit-тестов.</summary>
    internal PaymentsController(
        UserManager<ApplicationUser> users,
        ProxyHarborDbContext db,
        PaymentGatewayClient gateways,
        IOptions<PaymentOptions> configured)
        : this(users, db, gateways, new StaticPaymentConfigurationStore(configured)) { }

    /// <summary>Возвращает разрешённые продукты и состояние всех поддерживаемых шлюзов.</summary>
    [Authorize, HttpGet("catalog"), EnableRateLimiting("account")]
    public async Task<IActionResult> Catalog(CancellationToken token = default)
    {
        var options = await configurations.GetAsync(token);
        // Старые/тестовые конфигурации могли содержать только месячный продукт.
        // Публичный каталог остаётся совместимым, а runtime-store постепенно
        // нормализует реальную конфигурацию до полной сетки из шести периодов.
        var dailyPrice = options.Products.Values.FirstOrDefault(x => x.DurationDays == 1)?.AmountMinor;
        return Ok(new
        {
            enabled = options.Enabled,
            products = options.Products.Where(x => x.Value.Enabled).Select(x => new
            {
                code = x.Key,
                x.Value.Name,
                x.Value.Plan,
                x.Value.DurationDays,
                x.Value.AmountMinor,
                x.Value.DiscountPercent,
                fullDailyPriceMinor = dailyPrice is null ? x.Value.AmountMinor : checked(dailyPrice.Value * x.Value.DurationDays),
                savingsMinor = dailyPrice is null ? 0 : Math.Max(0, checked(dailyPrice.Value * x.Value.DurationDays) - x.Value.AmountMinor),
                currency = x.Value.Currency.ToUpperInvariant(),
                x.Value.Description
            }),
            providers = PaymentProviderConfiguration.Codes.Select(code => new
            {
                code,
                name = options.Providers.TryGetValue(code, out var value) && !string.IsNullOrWhiteSpace(value.DisplayName)
                    ? value.DisplayName : ProviderName(code),
                available = options.Enabled && value is not null && PaymentProviderConfiguration.IsReady(code, value)
            })
        });
    }

    /// <summary>Создаёт локальный заказ и возвращает URL страницы выбранного провайдера.</summary>
    [Authorize, HttpPost("checkout"), EnableRateLimiting("account")]
    public async Task<IActionResult> Checkout([FromBody] CreateCheckoutRequest request, CancellationToken token)
    {
        var options = await configurations.GetAsync(token);
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        var productCode = request.ProductCode.Trim().ToLowerInvariant();
        var providerCode = request.Provider.Trim().ToLowerInvariant();
        if (!options.Enabled || !options.Products.TryGetValue(productCode, out var product) || !product.Enabled)
            return Problem("Выбранный продукт недоступен.", statusCode: 400);
        if (!PaymentProviderConfiguration.Codes.Contains(providerCode, StringComparer.Ordinal) ||
            !options.Providers.TryGetValue(providerCode, out var provider) ||
            !PaymentProviderConfiguration.IsReady(providerCode, provider))
            return Problem("Выбранный способ оплаты недоступен.", statusCode: 400);
        if (!SubscriptionPlans.All.Contains(product.Plan, StringComparer.Ordinal) || product.Plan == SubscriptionPlans.Free ||
            product.AmountMinor <= 0 || product.DurationDays is < 1 or > 3660)
            return Problem("Конфигурация тарифа некорректна.", statusCode: 503);

        var order = new PaymentOrder
        {
            UserId = user.Id,
            ProductCode = productCode,
            Plan = product.Plan,
            Provider = providerCode,
            AmountMinor = product.AmountMinor,
            PaymentMethod = DefaultPaymentMethod(providerCode),
            PaymentInstrument = ProviderName(providerCode),
            Currency = product.Currency.ToUpperInvariant(),
            DurationDays = product.DurationDays
        };
        db.PaymentOrders.Add(order);
        await db.SaveChangesAsync(token);
        try
        {
            var checkout = await gateways.CreateAsync(order, user, token);
            order.ProviderPaymentId = checkout.ProviderPaymentId;
            order.CheckoutUrl = checkout.CheckoutUrl;
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            return Ok(new { order.Id, checkoutUrl = checkout.CheckoutUrl });
        }
        catch
        {
            order.Status = PaymentStatuses.Failed;
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>История платежей текущего пользователя.</summary>
    [Authorize, HttpGet("orders"), EnableRateLimiting("account")]
    public async Task<IActionResult> Orders(CancellationToken token)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        return Ok(await db.PaymentOrders.AsNoTracking().Where(x => x.UserId == user.Id)
            .OrderByDescending(x => x.CreatedAt).Take(50)
            .Select(x => new
            {
                x.Id,
                x.ProductCode,
                x.Plan,
                x.Provider,
                x.PaymentMethod,
                x.PaymentInstrument,
                x.AmountMinor,
                x.Currency,
                x.Status,
                x.CreatedAt,
                x.PaidAt
            })
            .ToArrayAsync(token));
    }

    /// <summary>Создаёт короткоживущую самопередающую форму для официального hosted checkout ЮMoney.</summary>
    [AllowAnonymous, HttpGet("hosted/yoomoney/{orderId:guid}/{checkoutToken}"), EnableRateLimiting("account")]
    public async Task<IActionResult> YooMoneyHosted(Guid orderId, string checkoutToken, CancellationToken token)
    {
        var notBefore = DateTimeOffset.UtcNow.AddHours(-1);
        var order = await db.PaymentOrders.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == orderId && x.Provider == "yoomoney" && x.Status == PaymentStatuses.Pending &&
            x.CreatedAt >= notBefore, token);
        if (order is null || !FixedTokenEquals(order.IdempotencyKey, checkoutToken)) return NotFound();

        var options = await configurations.GetAsync(token);
        if (!options.Providers.TryGetValue("yoomoney", out var provider) ||
            !PaymentProviderConfiguration.IsReady("yoomoney", provider)) return NotFound();

        var fields = new Dictionary<string, string>
        {
            ["receiver"] = provider.MerchantId,
            ["quickpay-form"] = "button",
            ["paymentType"] = "PC",
            ["sum"] = (order.AmountMinor / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["label"] = order.Id.ToString("D"),
            ["successURL"] = $"{options.PublicBaseUrl.TrimEnd('/')}/account?payment={order.Id:D}"
        };
        var inputs = string.Concat(fields.Select(item =>
            $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(item.Key)}\" value=\"{WebUtility.HtmlEncode(item.Value)}\">"));
        var html = $"""
            <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="referrer" content="no-referrer">
            <meta name="viewport" content="width=device-width,initial-scale=1"><title>Переход в ЮMoney</title></head>
            <body><form id="payment" method="post" action="https://yoomoney.ru/quickpay/confirm">{inputs}
            <noscript><button type="submit">Перейти к оплате в ЮMoney</button></noscript></form>
            <script>document.getElementById('payment').submit()</script></body></html>
            """;
        Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'unsafe-inline'; form-action https://yoomoney.ru; base-uri 'none'; frame-ancestors 'none'";
        return Content(html, "text/html", Encoding.UTF8);
    }

    /// <summary>Публичная точка уведомлений; доверие основано на подписи/повторной проверке шлюза.</summary>
    [AllowAnonymous, AcceptVerbs("GET", "POST", Route = "webhooks/{providerCode}"), IgnoreAntiforgeryToken, EnableRateLimiting("payment-webhook")]
    public async Task<IActionResult> Webhook(string providerCode, CancellationToken token)
    {
        providerCode = providerCode.Trim().ToLowerInvariant();
        if (!PaymentProviderConfiguration.Codes.Contains(providerCode, StringComparer.Ordinal)) return NotFound();
        PaymentNotification notification;
        try { notification = await gateways.ReadNotificationAsync(providerCode, Request, token); }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
        { return Problem("Уведомление не прошло проверку.", statusCode: 400); }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var order = await db.PaymentOrders.SingleOrDefaultAsync(x => x.Id == notification.OrderId, token);
        if (order is null || order.Provider != providerCode || order.AmountMinor != notification.AmountMinor ||
            !string.Equals(order.Currency, notification.Currency, StringComparison.OrdinalIgnoreCase))
            return Problem("Параметры уведомления не соответствуют заказу.", statusCode: 400);
        if (order.Status == notification.Status ||
            order.Status == PaymentStatuses.Paid && notification.Status != PaymentStatuses.Refunded)
        {
            await transaction.CommitAsync(token);
            return Acknowledgement(providerCode, order);
        }

        order.ProviderPaymentId ??= notification.ProviderPaymentId;
        if (!string.Equals(order.ProviderPaymentId, notification.ProviderPaymentId, StringComparison.Ordinal))
            return Problem("Идентификатор операции не соответствует заказу.", statusCode: 400);
        order.Status = notification.Status;
        order.PaymentMethod = notification.PaymentMethod ?? order.PaymentMethod;
        order.PaymentInstrument = notification.PaymentInstrument ?? order.PaymentInstrument;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        if (notification.Status == PaymentStatuses.Paid)
        {
            order.PaidAt = DateTimeOffset.UtcNow;
            var subscription = await db.Subscriptions.SingleAsync(x => x.UserId == order.UserId, token);
            var begins = subscription.ExpiresAt is { } expires && expires > order.PaidAt ? expires : order.PaidAt.Value;
            subscription.Plan = order.Plan;
            subscription.Status = SubscriptionStatuses.Active;
            if (subscription.ExpiresAt is null || subscription.ExpiresAt <= order.PaidAt.Value)
                subscription.StartedAt = order.PaidAt.Value;
            subscription.ExpiresAt = begins.AddDays(order.DurationDays);
            subscription.ExternalCustomerId ??= order.UserId.ToString("D");
            subscription.ExternalSubscriptionId = notification.ProviderPaymentId;
            subscription.UpdatedAt = order.PaidAt.Value;
            var account = await users.FindByIdAsync(order.UserId.ToString());
            if (account is not null && !await users.IsInRoleAsync(account, UserRoles.Subscriber))
                await users.AddToRoleAsync(account, UserRoles.Subscriber);
            await ReferralRewards.GrantForPurchaseAsync(db, users, order, order.PaidAt.Value, token);
        }
        else if (notification.Status == PaymentStatuses.Refunded)
        {
            // Отзыв относится только к подписке, последней активированной этим заказом.
            // Более свежая оплаченная подписка не должна пострадать от старого refund.
            var subscription = await db.Subscriptions.SingleAsync(x => x.UserId == order.UserId, token);
            if (string.Equals(subscription.ExternalSubscriptionId, notification.ProviderPaymentId, StringComparison.Ordinal))
            {
                subscription.Status = SubscriptionStatuses.Canceled;
                subscription.ExpiresAt = DateTimeOffset.UtcNow;
                subscription.UpdatedAt = DateTimeOffset.UtcNow;
                var account = await users.FindByIdAsync(order.UserId.ToString());
                if (account is not null && await users.IsInRoleAsync(account, UserRoles.Subscriber))
                    await users.RemoveFromRoleAsync(account, UserRoles.Subscriber);
            }
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Acknowledgement(providerCode, order);
    }

    private IActionResult Acknowledgement(string provider, PaymentOrder order) => provider switch
    {
        "robokassa" => Content($"OK{order.ProviderPaymentId}"),
        "tbank" => Content("OK"),
        "cloudpayments" => Ok(new { code = 0 }),
        _ => Ok()
    };

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

    private static string DefaultPaymentMethod(string code) => code switch
    {
        "yoomoney" => "wallet",
        "cryptomus" or "nowpayments" => "crypto",
        _ => "payment_gateway"
    };

    private static bool FixedTokenEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

}

/// <summary>Минимальный запрос: цена и срок всегда берутся с доверенного сервера.</summary>
public sealed class CreateCheckoutRequest
{
    /// <summary>Код продукта из опубликованного каталога.</summary>
    [Required, StringLength(64, MinimumLength = 1)] public string ProductCode { get; set; } = string.Empty;
    /// <summary>Код выбранного платёжного шлюза.</summary>
    [Required, StringLength(32, MinimumLength = 1)] public string Provider { get; set; } = string.Empty;
}
