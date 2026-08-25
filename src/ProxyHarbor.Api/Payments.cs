using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Настройки каталога и пяти изолированных hosted-checkout шлюзов.</summary>
public sealed class PaymentOptions
{
    /// <summary>Имя configuration section.</summary>
    public const string Section = "Payments";
    /// <summary>Глобальный выключатель биллинга.</summary>
    public bool Enabled { get; set; }
    /// <summary>Публичный HTTPS origin для возврата и webhooks.</summary>
    public string PublicBaseUrl { get; set; } = "https://proxy.blagodaty.ru";
    /// <summary>Серверный каталог продаваемых продуктов.</summary>
    public Dictionary<string, PaymentProductOptions> Products { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Настройки платёжных шлюзов по стабильному коду.</summary>
    public Dictionary<string, PaymentProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Коммерческий продукт; цены меняются конфигурацией, а не перекомпиляцией.</summary>
public sealed class PaymentProductOptions
{
    /// <summary>Можно ли создать checkout этого продукта.</summary>
    public bool Enabled { get; set; }
    /// <summary>Название для личного кабинета и страницы оплаты.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Активируемый внутренний тариф.</summary>
    public string Plan { get; set; } = SubscriptionPlans.Pro;
    /// <summary>Число оплаченных дней.</summary>
    public int DurationDays { get; set; } = 30;
    /// <summary>Цена в минимальных единицах валюты.</summary>
    public long AmountMinor { get; set; }
    /// <summary>Код валюты ISO 4217.</summary>
    public string Currency { get; set; } = "RUB";
    /// <summary>Краткое описание тарифа.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>Унифицированные реквизиты; неиспользуемые поля конкретный шлюз игнорирует.</summary>
public sealed class PaymentProviderOptions
{
    /// <summary>Разрешено ли создавать новые платежи.</summary>
    public bool Enabled { get; set; }
    /// <summary>Название шлюза для пользователя.</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Shop ID, MerchantLogin или TerminalKey.</summary>
    public string MerchantId { get; set; } = string.Empty;
    /// <summary>Публичный идентификатор CloudPayments.</summary>
    public string PublicId { get; set; } = string.Empty;
    /// <summary>Основной серверный секрет.</summary>
    public string SecretKey { get; set; } = string.Empty;
    /// <summary>Секрет webhook либо второй пароль подписи.</summary>
    public string SecondarySecret { get; set; } = string.Empty;
    /// <summary>Тестовый режим, если шлюз его поддерживает.</summary>
    public bool TestMode { get; set; }
}

internal sealed record CheckoutResult(string? ProviderPaymentId, string CheckoutUrl);
internal sealed record PaymentNotification(Guid OrderId, string ProviderPaymentId, string Status, long AmountMinor, string Currency);

internal static class PaymentProviderConfiguration
{
    internal static bool IsReady(string code, PaymentProviderOptions value) => value.Enabled && code switch
    {
        "yookassa" => Present(value.MerchantId) && Present(value.SecretKey),
        "cloudpayments" => Present(value.PublicId) && Present(value.SecretKey),
        "robokassa" => Present(value.MerchantId) && Present(value.SecretKey) && Present(value.SecondarySecret),
        "tbank" => Present(value.MerchantId) && Present(value.SecretKey),
        "stripe" => Present(value.SecretKey) && Present(value.SecondarySecret),
        _ => false
    };

    private static bool Present(string value) => !string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// Создаёт только страницы оплаты самого провайдера и проверяет подписанные уведомления.
/// Реквизиты карт никогда не принимаются приложением.
/// </summary>
public sealed class PaymentGatewayClient(IHttpClientFactory clients, IOptions<PaymentOptions> configured)
{
    private readonly PaymentOptions options = configured.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<CheckoutResult> CreateAsync(
        PaymentOrder order, ApplicationUser user, CancellationToken token)
    {
        var provider = RequiredProvider(order.Provider);
        return order.Provider switch
        {
            "yookassa" => await CreateYooKassaAsync(order, provider, token),
            "cloudpayments" => await CreateCloudPaymentsAsync(order, user, provider, token),
            "robokassa" => CreateRobokassa(order, user, provider),
            "tbank" => await CreateTBankAsync(order, user, provider, token),
            "stripe" => await CreateStripeAsync(order, user, provider, token),
            _ => throw new InvalidOperationException("Неизвестный платёжный провайдер.")
        };
    }

    internal async Task<PaymentNotification> ReadNotificationAsync(
        string providerCode, HttpRequest request, CancellationToken token)
    {
        var provider = RequiredProvider(providerCode);
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(token);
        request.Body.Position = 0;
        return providerCode switch
        {
            "yookassa" => await ReadYooKassaAsync(body, provider, token),
            "cloudpayments" => ReadCloudPayments(body, request, provider),
            "robokassa" => ReadRobokassa(body, request, provider),
            "tbank" => ReadTBank(body, provider),
            "stripe" => ReadStripe(body, request, provider),
            _ => throw new InvalidOperationException("Неизвестный платёжный провайдер.")
        };
    }

    private PaymentProviderOptions RequiredProvider(string code)
    {
        if (!options.Enabled || !options.Providers.TryGetValue(code, out var provider) ||
            !PaymentProviderConfiguration.IsReady(code, provider))
            throw new InvalidOperationException("Платёжный провайдер пока не настроен.");
        return provider;
    }

    private async Task<CheckoutResult> CreateYooKassaAsync(
        PaymentOrder order, PaymentProviderOptions provider, CancellationToken token)
    {
        var payload = new
        {
            amount = new { value = Major(order.AmountMinor), currency = order.Currency },
            capture = true,
            confirmation = new { type = "redirect", return_url = ReturnUrl(order.Id) },
            description = $"ProxyHarbor {order.ProductCode}",
            metadata = new { order_id = order.Id.ToString("D") }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.yookassa.ru/v3/payments")
        { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = Basic(provider.MerchantId, provider.SecretKey);
        request.Headers.Add("Idempotence-Key", order.IdempotencyKey);
        using var response = await clients.CreateClient().SendAsync(request, token);
        var json = await ReadSuccessJsonAsync(response, token);
        return new(json.GetProperty("id").GetString()!,
            json.GetProperty("confirmation").GetProperty("confirmation_url").GetString()!);
    }

    private async Task<CheckoutResult> CreateCloudPaymentsAsync(
        PaymentOrder order, ApplicationUser user, PaymentProviderOptions provider, CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["Amount"] = order.AmountMinor / 100m, ["Currency"] = order.Currency,
            ["Description"] = $"ProxyHarbor {order.ProductCode}", ["Email"] = user.Email,
            ["InvoiceId"] = order.Id.ToString("D"), ["AccountId"] = user.Id.ToString("D"),
            ["SendEmail"] = false, ["RequireConfirmation"] = false,
            ["SuccessRedirectUrl"] = ReturnUrl(order.Id), ["FailRedirectUrl"] = ReturnUrl(order.Id)
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.cloudpayments.ru/orders/create")
        { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = Basic(provider.PublicId, provider.SecretKey);
        request.Headers.Add("X-Request-ID", order.IdempotencyKey);
        using var response = await clients.CreateClient().SendAsync(request, token);
        var json = await ReadSuccessJsonAsync(response, token);
        var model = json.GetProperty("Model");
        // Pay-notification содержит TransactionId, отличный от ID выставленного счёта.
        // Канонический внешний ID фиксируется только из подписанного уведомления.
        return new(null, model.GetProperty("Url").GetString()!);
    }

    private static CheckoutResult CreateRobokassa(
        PaymentOrder order, ApplicationUser user, PaymentProviderOptions provider)
    {
        var amount = Major(order.AmountMinor);
        var invoice = PositiveInvoice(order.Id);
        var custom = $"Shp_order={Uri.EscapeDataString(order.Id.ToString("D"))}";
        var signature = HashHex(
            $"{provider.MerchantId}:{amount}:{invoice}:{provider.SecretKey}:{custom}");
        var query = new Dictionary<string, string?>
        {
            ["MerchantLogin"] = provider.MerchantId, ["OutSum"] = amount,
            ["InvId"] = invoice, ["Description"] = $"ProxyHarbor {order.ProductCode}",
            ["Email"] = user.Email, ["Culture"] = "ru", ["Encoding"] = "utf-8",
            ["IsTest"] = provider.TestMode ? "1" : "0", ["Shp_order"] = order.Id.ToString("D"),
            ["SignatureValue"] = signature
        };
        return new(invoice, "https://auth.robokassa.ru/Merchant/Index.aspx?" + QueryString(query));
    }

    private async Task<CheckoutResult> CreateTBankAsync(
        PaymentOrder order, ApplicationUser user, PaymentProviderOptions provider, CancellationToken token)
    {
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Amount"] = order.AmountMinor, ["CustomerKey"] = user.Id.ToString("D"),
            ["Description"] = $"ProxyHarbor {order.ProductCode}", ["FailURL"] = ReturnUrl(order.Id),
            ["NotificationURL"] = $"{options.PublicBaseUrl.TrimEnd('/')}/api/v1/payments/webhooks/tbank",
            ["OrderId"] = order.Id.ToString("D"), ["PayType"] = "O",
            ["SuccessURL"] = ReturnUrl(order.Id), ["TerminalKey"] = provider.MerchantId
        };
        payload["Token"] = TBankToken(payload, provider.SecretKey);
        using var response = await clients.CreateClient().PostAsJsonAsync(
            "https://securepay.tinkoff.ru/v2/Init", payload, token);
        var json = await ReadSuccessJsonAsync(response, token);
        if (!json.GetProperty("Success").GetBoolean()) throw new InvalidOperationException("T-Bank отклонил создание платежа.");
        return new(json.GetProperty("PaymentId").ToString(), json.GetProperty("PaymentURL").GetString()!);
    }

    private async Task<CheckoutResult> CreateStripeAsync(
        PaymentOrder order, ApplicationUser user, PaymentProviderOptions provider, CancellationToken token)
    {
        var fields = new Dictionary<string, string>
        {
            ["mode"] = "payment", ["success_url"] = ReturnUrl(order.Id),
            ["cancel_url"] = ReturnUrl(order.Id), ["customer_email"] = user.Email ?? string.Empty,
            ["client_reference_id"] = order.Id.ToString("D"), ["metadata[order_id]"] = order.Id.ToString("D"),
            ["line_items[0][quantity]"] = "1", ["line_items[0][price_data][currency]"] = order.Currency.ToLowerInvariant(),
            ["line_items[0][price_data][unit_amount]"] = order.AmountMinor.ToString(CultureInfo.InvariantCulture),
            ["line_items[0][price_data][product_data][name]"] = $"ProxyHarbor {order.ProductCode}"
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions")
        { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.SecretKey);
        request.Headers.Add("Idempotency-Key", order.IdempotencyKey);
        using var response = await clients.CreateClient().SendAsync(request, token);
        var json = await ReadSuccessJsonAsync(response, token);
        return new(json.GetProperty("id").GetString()!, json.GetProperty("url").GetString()!);
    }

    private async Task<PaymentNotification> ReadYooKassaAsync(
        string body, PaymentProviderOptions provider, CancellationToken token)
    {
        using var incoming = JsonDocument.Parse(body);
        var paymentId = incoming.RootElement.GetProperty("object").GetProperty("id").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.yookassa.ru/v3/payments/{Uri.EscapeDataString(paymentId)}");
        request.Headers.Authorization = Basic(provider.MerchantId, provider.SecretKey);
        using var response = await clients.CreateClient().SendAsync(request, token);
        var verified = await ReadSuccessJsonAsync(response, token);
        return new(Guid.Parse(verified.GetProperty("metadata").GetProperty("order_id").GetString()!),
            paymentId, MapStatus(verified.GetProperty("status").GetString()),
            ParseMinor(verified.GetProperty("amount").GetProperty("value").GetString()!),
            verified.GetProperty("amount").GetProperty("currency").GetString()!);
    }

    private static PaymentNotification ReadCloudPayments(
        string body, HttpRequest request, PaymentProviderOptions provider)
    {
        var content = request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            ? request.QueryString.Value?.TrimStart('?') ?? string.Empty : body;
        VerifyHmacBase64(content, request.Headers["Content-HMAC"].FirstOrDefault(), provider.SecretKey);
        var form = request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            ? request.Query.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase)
            : ParseForm(body);
        return new(Guid.Parse(form["InvoiceId"]), form.GetValueOrDefault("TransactionId") ?? form["InvoiceId"], PaymentStatuses.Paid,
            ParseMinor(form["Amount"]), form.GetValueOrDefault("Currency") ?? "RUB");
    }

    private static PaymentNotification ReadRobokassa(
        string body, HttpRequest request, PaymentProviderOptions provider)
    {
        var form = request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            ? request.Query.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase)
            : ParseForm(body);
        var custom = $"Shp_order={form["Shp_order"]}";
        var expected = HashHex(
            $"{form["OutSum"]}:{form["InvId"]}:{provider.SecondarySecret}:{custom}");
        RequireFixedEquals(expected, form["SignatureValue"]);
        return new(Guid.Parse(form["Shp_order"]), form["InvId"], PaymentStatuses.Paid,
            ParseMinor(form["OutSum"]), "RUB");
    }

    private static PaymentNotification ReadTBank(string body, PaymentProviderOptions provider)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var values = root.EnumerateObject().Where(x => x.Name != "Token" && x.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            .ToDictionary(x => x.Name, x => (object?)ElementText(x.Value), StringComparer.Ordinal);
        RequireFixedEquals(TBankToken(new SortedDictionary<string, object?>(values, StringComparer.Ordinal), provider.SecretKey), root.GetProperty("Token").GetString()!);
        var status = root.GetProperty("Status").GetString();
        return new(Guid.Parse(root.GetProperty("OrderId").GetString()!), root.GetProperty("PaymentId").ToString(),
            status == "CONFIRMED" ? PaymentStatuses.Paid : status is "REFUNDED" or "REVERSED" ? PaymentStatuses.Refunded : PaymentStatuses.Failed,
            root.GetProperty("Amount").GetInt64(), "RUB");
    }

    private static PaymentNotification ReadStripe(
        string body, HttpRequest request, PaymentProviderOptions provider)
    {
        var header = request.Headers["Stripe-Signature"].FirstOrDefault() ?? throw new InvalidOperationException("Нет Stripe-Signature.");
        var parts = header.Split(',').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToLookup(x => x[0], x => x[1]);
        var timestamp = long.Parse(parts["t"].First(), CultureInfo.InvariantCulture);
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > 300) throw new InvalidOperationException("Устаревшая Stripe подпись.");
        var expected = HmacHex($"{timestamp}.{body}", provider.SecondarySecret);
        if (!parts["v1"].Any(candidate => FixedEquals(expected, candidate))) throw new InvalidOperationException("Некорректная Stripe подпись.");
        using var json = JsonDocument.Parse(body);
        var session = json.RootElement.GetProperty("data").GetProperty("object");
        var orderId = Guid.Parse(session.GetProperty("metadata").GetProperty("order_id").GetString()!);
        var status = session.TryGetProperty("payment_status", out var paymentStatus) && paymentStatus.GetString() == "paid"
            ? PaymentStatuses.Paid : PaymentStatuses.Pending;
        return new(orderId, session.GetProperty("id").GetString()!, status,
            session.GetProperty("amount_total").GetInt64(), session.GetProperty("currency").GetString()!.ToUpperInvariant());
    }

    private string ReturnUrl(Guid id) => $"{options.PublicBaseUrl.TrimEnd('/')}/account?payment={id:D}";
    private static string Major(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    private static long ParseMinor(string major) => checked((long)(decimal.Parse(major, CultureInfo.InvariantCulture) * 100m));
    private static string PositiveInvoice(Guid id) => (BitConverter.ToUInt32(id.ToByteArray(), 0) & 0x7fffffff).ToString(CultureInfo.InvariantCulture);
    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    private static string QueryString(IReadOnlyDictionary<string, string?> values) => string.Join('&', values.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value ?? string.Empty)}"));
    private static Dictionary<string, string> ParseForm(string value) => value.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).ToDictionary(
            x => Uri.UnescapeDataString(x[0].Replace('+', ' ')),
            x => x.Length == 2 ? Uri.UnescapeDataString(x[1].Replace('+', ' ')) : string.Empty,
            StringComparer.OrdinalIgnoreCase);
    private static string TBankToken(SortedDictionary<string, object?> values, string password)
    {
        var signed = new SortedDictionary<string, object?>(values, StringComparer.Ordinal) { ["Password"] = password };
        return HashHex(
            string.Concat(signed.Where(x => x.Key != "Token").Select(x => Convert.ToString(x.Value, CultureInfo.InvariantCulture))));
    }
    private static string MapStatus(string? value) => value switch { "succeeded" => PaymentStatuses.Paid, "canceled" => PaymentStatuses.Canceled, _ => PaymentStatuses.Pending };
    private static string ElementText(JsonElement value) => value.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.String => value.GetString()!, _ => value.GetRawText() };
    private static string HashHex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string HmacHex(string value, string key) { using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)); return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant(); }
    private static void VerifyHmacBase64(string value, string? supplied, string key) { using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)); RequireFixedEquals(Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))), supplied ?? string.Empty); }
    private static void RequireFixedEquals(string expected, string actual) { if (!FixedEquals(expected, actual)) throw new InvalidOperationException("Некорректная подпись уведомления."); }
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left.ToLowerInvariant()), Encoding.ASCII.GetBytes(right.ToLowerInvariant()));
    private static async Task<JsonElement> ReadSuccessJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Платёжный шлюз вернул HTTP {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
