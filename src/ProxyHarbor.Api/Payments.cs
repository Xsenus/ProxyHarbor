using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Настройки каталога и восьми изолированных hosted-checkout шлюзов.</summary>
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
    internal static readonly string[] Codes =
        ["yookassa", "yoomoney", "cloudpayments", "robokassa", "tbank", "stripe", "cryptomus", "nowpayments"];

    internal static bool IsReady(string code, PaymentProviderOptions value) => value.Enabled && code switch
    {
        "yookassa" => Present(value.MerchantId) && Present(value.SecretKey),
        "yoomoney" => Present(value.MerchantId) && Present(value.SecretKey),
        "cloudpayments" => Present(value.PublicId) && Present(value.SecretKey),
        "robokassa" => Present(value.MerchantId) && Present(value.SecretKey) && Present(value.SecondarySecret),
        "tbank" => Present(value.MerchantId) && Present(value.SecretKey),
        "stripe" => Present(value.SecretKey) && Present(value.SecondarySecret),
        "cryptomus" => Present(value.MerchantId) && Present(value.SecretKey),
        "nowpayments" => Present(value.SecretKey) && Present(value.SecondarySecret),
        _ => false
    };

    private static bool Present(string value) => !string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// Создаёт только страницы оплаты самого провайдера и проверяет подписанные уведомления.
/// Реквизиты карт никогда не принимаются приложением.
/// </summary>
public sealed class PaymentGatewayClient(IHttpClientFactory clients, IPaymentConfigurationStore configurations)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Упрощённый конструктор сохраняет совместимость unit-тестов со статической конфигурацией.</summary>
    internal PaymentGatewayClient(IHttpClientFactory clients, IOptions<PaymentOptions> configured)
        : this(clients, new StaticPaymentConfigurationStore(configured)) { }

    internal async Task<CheckoutResult> CreateAsync(
        PaymentOrder order, ApplicationUser user, CancellationToken token)
    {
        var (provider, publicBaseUrl) = await RequiredProviderAsync(order.Provider, token);
        return order.Provider switch
        {
            "yookassa" => await CreateYooKassaAsync(order, provider, publicBaseUrl, token),
            "yoomoney" => CreateYooMoney(order, publicBaseUrl),
            "cloudpayments" => await CreateCloudPaymentsAsync(order, user, provider, publicBaseUrl, token),
            "robokassa" => CreateRobokassa(order, user, provider),
            "tbank" => await CreateTBankAsync(order, user, provider, publicBaseUrl, token),
            "stripe" => await CreateStripeAsync(order, user, provider, publicBaseUrl, token),
            "cryptomus" => await CreateCryptomusAsync(order, provider, publicBaseUrl, token),
            "nowpayments" => await CreateNowPaymentsAsync(order, provider, publicBaseUrl, token),
            _ => throw new InvalidOperationException("Неизвестный платёжный провайдер.")
        };
    }

    internal async Task<PaymentNotification> ReadNotificationAsync(
        string providerCode, HttpRequest request, CancellationToken token)
    {
        var (provider, _) = await RequiredProviderAsync(providerCode, token);
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(token);
        request.Body.Position = 0;
        return providerCode switch
        {
            "yookassa" => await ReadYooKassaAsync(body, provider, token),
            "yoomoney" => ReadYooMoney(body, provider),
            "cloudpayments" => ReadCloudPayments(body, request, provider),
            "robokassa" => ReadRobokassa(body, request, provider),
            "tbank" => ReadTBank(body, provider),
            "stripe" => ReadStripe(body, request, provider),
            "cryptomus" => ReadCryptomus(body, provider),
            "nowpayments" => ReadNowPayments(body, request, provider),
            _ => throw new InvalidOperationException("Неизвестный платёжный провайдер.")
        };
    }

    private async Task<(PaymentProviderOptions Provider, string PublicBaseUrl)> RequiredProviderAsync(
        string code, CancellationToken token)
    {
        var options = await configurations.GetAsync(token);
        if (!options.Enabled || !options.Providers.TryGetValue(code, out var provider) ||
            !PaymentProviderConfiguration.IsReady(code, provider))
            throw new InvalidOperationException("Платёжный провайдер пока не настроен.");
        return (provider, options.PublicBaseUrl);
    }

    private async Task<CheckoutResult> CreateYooKassaAsync(
        PaymentOrder order, PaymentProviderOptions provider, string publicBaseUrl, CancellationToken token)
    {
        var payload = new
        {
            amount = new { value = Major(order.AmountMinor), currency = order.Currency },
            capture = true,
            confirmation = new { type = "redirect", return_url = ReturnUrl(publicBaseUrl, order.Id) },
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

    private static CheckoutResult CreateYooMoney(PaymentOrder order, string publicBaseUrl) =>
        new(null, $"{publicBaseUrl.TrimEnd('/')}/api/v1/payments/hosted/yoomoney/{order.Id:D}/{order.IdempotencyKey}");

    private async Task<CheckoutResult> CreateCloudPaymentsAsync(
        PaymentOrder order, ApplicationUser user, PaymentProviderOptions provider, string publicBaseUrl, CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["Amount"] = order.AmountMinor / 100m, ["Currency"] = order.Currency,
            ["Description"] = $"ProxyHarbor {order.ProductCode}", ["Email"] = user.Email,
            ["InvoiceId"] = order.Id.ToString("D"), ["AccountId"] = user.Id.ToString("D"),
            ["SendEmail"] = false, ["RequireConfirmation"] = false,
            ["SuccessRedirectUrl"] = ReturnUrl(publicBaseUrl, order.Id), ["FailRedirectUrl"] = ReturnUrl(publicBaseUrl, order.Id)
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
        PaymentOrder order, ApplicationUser user, PaymentProviderOptions provider, string publicBaseUrl, CancellationToken token)
    {
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Amount"] = order.AmountMinor, ["CustomerKey"] = user.Id.ToString("D"),
            ["Description"] = $"ProxyHarbor {order.ProductCode}", ["FailURL"] = ReturnUrl(publicBaseUrl, order.Id),
            ["NotificationURL"] = $"{publicBaseUrl.TrimEnd('/')}/api/v1/payments/webhooks/tbank",
            ["OrderId"] = order.Id.ToString("D"), ["PayType"] = "O",
            ["SuccessURL"] = ReturnUrl(publicBaseUrl, order.Id), ["TerminalKey"] = provider.MerchantId
        };
        payload["Token"] = TBankToken(payload, provider.SecretKey);
        using var response = await clients.CreateClient().PostAsJsonAsync(
            "https://securepay.tinkoff.ru/v2/Init", payload, token);
        var json = await ReadSuccessJsonAsync(response, token);
        if (!json.GetProperty("Success").GetBoolean()) throw new InvalidOperationException("T-Bank отклонил создание платежа.");
        return new(json.GetProperty("PaymentId").ToString(), json.GetProperty("PaymentURL").GetString()!);
    }

    private async Task<CheckoutResult> CreateStripeAsync(
        PaymentOrder order, ApplicationUser user, PaymentProviderOptions provider, string publicBaseUrl, CancellationToken token)
    {
        var fields = new Dictionary<string, string>
        {
            ["mode"] = "payment", ["success_url"] = ReturnUrl(publicBaseUrl, order.Id),
            ["cancel_url"] = ReturnUrl(publicBaseUrl, order.Id), ["customer_email"] = user.Email ?? string.Empty,
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

    private async Task<CheckoutResult> CreateCryptomusAsync(
        PaymentOrder order, PaymentProviderOptions provider, string publicBaseUrl, CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["amount"] = Major(order.AmountMinor), ["currency"] = order.Currency,
            ["order_id"] = order.Id.ToString("N"), ["url_return"] = ReturnUrl(publicBaseUrl, order.Id),
            ["url_success"] = ReturnUrl(publicBaseUrl, order.Id),
            ["url_callback"] = $"{publicBaseUrl.TrimEnd('/')}/api/v1/payments/webhooks/cryptomus",
            ["is_payment_multiple"] = true, ["lifetime"] = 3600
        };
        var body = JsonSerializer.Serialize(payload, Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.cryptomus.com/v1/payment")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("merchant", provider.MerchantId);
        request.Headers.Add("sign", CryptomusSignature(body, provider.SecretKey));
        using var response = await clients.CreateClient().SendAsync(request, token);
        var json = await ReadSuccessJsonAsync(response, token);
        var result = json.GetProperty("result");
        return new(result.GetProperty("uuid").GetString()!, result.GetProperty("url").GetString()!);
    }

    private async Task<CheckoutResult> CreateNowPaymentsAsync(
        PaymentOrder order, PaymentProviderOptions provider, string publicBaseUrl, CancellationToken token)
    {
        var payload = new
        {
            price_amount = order.AmountMinor / 100m,
            price_currency = order.Currency.ToLowerInvariant(),
            order_id = order.Id.ToString("D"),
            order_description = $"ProxyHarbor {order.ProductCode}",
            ipn_callback_url = $"{publicBaseUrl.TrimEnd('/')}/api/v1/payments/webhooks/nowpayments",
            success_url = ReturnUrl(publicBaseUrl, order.Id), cancel_url = ReturnUrl(publicBaseUrl, order.Id),
            is_fixed_rate = true
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nowpayments.io/v1/invoice")
        { Content = JsonContent.Create(payload) };
        request.Headers.Add("x-api-key", provider.SecretKey);
        using var response = await clients.CreateClient().SendAsync(request, token);
        var json = await ReadSuccessJsonAsync(response, token);
        // Invoice ID и payment ID у NOWPayments различаются. Канонический payment ID
        // фиксируется из подписанного IPN, иначе первый callback был бы отклонён.
        return new(null, json.GetProperty("invoice_url").GetString()!);
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

    private static PaymentNotification ReadYooMoney(string body, PaymentProviderOptions provider)
    {
        var form = ParseForm(body);
        var supplied = form.GetValueOrDefault("sign") ?? throw new InvalidOperationException("Нет подписи ЮMoney.");
        var canonical = FormString(form.Where(item => !item.Key.Equals("sign", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
        RequireFixedEquals(HmacHex(canonical, provider.SecretKey), supplied);
        if (form.GetValueOrDefault("test_notification")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("Тестовое уведомление ЮMoney не активирует подписку.");
        if (form.GetValueOrDefault("unaccepted")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("Перевод ЮMoney ещё не принят.");
        if (!string.Equals(form.GetValueOrDefault("currency"), "643", StringComparison.Ordinal))
            throw new InvalidOperationException("ЮMoney прислал неподдерживаемую валюту.");
        var amount = form.GetValueOrDefault("withdraw_amount") ?? form["amount"];
        return new(Guid.Parse(form["label"]), form["operation_id"], PaymentStatuses.Paid,
            ParseMinor(amount), "RUB");
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

    private static PaymentNotification ReadCryptomus(string body, PaymentProviderOptions provider)
    {
        var root = JsonNode.Parse(body)?.AsObject() ?? throw new InvalidOperationException("Пустой webhook Cryptomus.");
        var supplied = root["sign"]?.GetValue<string>() ?? throw new InvalidOperationException("Нет подписи Cryptomus.");
        root.Remove("sign");
        // Cryptomus формирует webhook-подпись совместимым с PHP json_encode способом:
        // Unicode остаётся открытым, а косые черты экранируются.
        var canonical = root.ToJsonString(WebhookJson).Replace("/", "\\/", StringComparison.Ordinal);
        RequireFixedEquals(CryptomusSignature(canonical, provider.SecretKey), supplied);
        var status = NodeText(root["status"] ?? root["payment_status"]);
        return new(Guid.Parse(NodeText(root["order_id"])), NodeText(root["uuid"]), status switch
        {
            "paid" or "paid_over" => PaymentStatuses.Paid,
            "refund_paid" => PaymentStatuses.Refunded,
            "cancel" => PaymentStatuses.Canceled,
            "fail" or "wrong_amount" or "system_fail" or "refund_fail" => PaymentStatuses.Failed,
            _ => PaymentStatuses.Pending
        }, ParseMinor(NodeText(root["amount"])), NodeText(root["currency"]).ToUpperInvariant());
    }

    private static PaymentNotification ReadNowPayments(
        string body, HttpRequest request, PaymentProviderOptions provider)
    {
        var root = JsonNode.Parse(body) ?? throw new InvalidOperationException("Пустой IPN NOWPayments.");
        var canonical = SortJson(root).ToJsonString(WebhookJson);
        var supplied = request.Headers["x-nowpayments-sig"].FirstOrDefault()
            ?? throw new InvalidOperationException("Нет подписи NOWPayments.");
        RequireFixedEquals(HmacSha512Hex(canonical, provider.SecondarySecret), supplied);
        var objectRoot = root.AsObject();
        var providerId = NodeText(objectRoot["payment_id"] ?? objectRoot["invoice_id"]);
        var status = NodeText(objectRoot["payment_status"]);
        return new(Guid.Parse(NodeText(objectRoot["order_id"])), providerId, status switch
        {
            "finished" => PaymentStatuses.Paid,
            "refunded" => PaymentStatuses.Refunded,
            "expired" => PaymentStatuses.Canceled,
            "failed" => PaymentStatuses.Failed,
            _ => PaymentStatuses.Pending
        }, ParseMinor(NodeText(objectRoot["price_amount"])), NodeText(objectRoot["price_currency"]).ToUpperInvariant());
    }

    private static string ReturnUrl(string publicBaseUrl, Guid id) =>
        $"{publicBaseUrl.TrimEnd('/')}/account?payment={id:D}";
    private static string Major(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    private static long ParseMinor(string major) => checked((long)(decimal.Parse(major, CultureInfo.InvariantCulture) * 100m));
    private static string PositiveInvoice(Guid id) => (BitConverter.ToUInt32(id.ToByteArray(), 0) & 0x7fffffff).ToString(CultureInfo.InvariantCulture);
    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    private static string QueryString(IReadOnlyDictionary<string, string?> values) => string.Join('&', values.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value ?? string.Empty)}"));
    private static string FormString(IReadOnlyDictionary<string, string> values) => string.Join('&',
        values.Select(item => $"{FormEncode(item.Key)}={FormEncode(item.Value)}"));
    private static string FormEncode(string value) => Uri.EscapeDataString(value);
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
    private static string HmacSha512Hex(string value, string key) { using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(key)); return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant(); }
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Cryptomus protocol mandates MD5 for request and webhook signatures; TLS and exact-body validation are also enforced.")]
    private static string CryptomusSignature(string body, string key) => Convert.ToHexString(MD5.HashData(
        Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)) + key))).ToLowerInvariant();
    private static string NodeText(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString().Trim('"'),
        _ => throw new InvalidOperationException("Webhook не содержит обязательного поля.")
    };
    private static JsonNode SortJson(JsonNode node) => node switch
    {
        JsonObject value => new JsonObject(value.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => KeyValuePair.Create(item.Key, item.Value is null ? null : SortJson(item.Value)))),
        JsonArray value => new JsonArray(value.Select(item => item is null ? null : SortJson(item)).ToArray()),
        _ => node.DeepClone()
    };
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

    private static readonly JsonSerializerOptions WebhookJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };
}
