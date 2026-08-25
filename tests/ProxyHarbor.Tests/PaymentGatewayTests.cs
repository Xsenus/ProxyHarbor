using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет fail-closed границу платёжных уведомлений без обращения к внешней сети.</summary>
public sealed class PaymentGatewayTests
{
    [Fact]
    public async Task CloudPaymentsAcceptsOnlyCorrectHmacAndPreservesTrustedAmount()
    {
        const string secret = "cloud-test-secret";
        var order = Guid.NewGuid();
        var body = $"InvoiceId={order:D}&TransactionId=42&Amount=499.00&Currency=RUB";
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        context.Request.Headers["Content-HMAC"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));

        var notification = await Client(secret).ReadNotificationAsync("cloudpayments", context.Request, CancellationToken.None);

        Assert.Equal(order, notification.OrderId);
        Assert.Equal(49_900, notification.AmountMinor);
        Assert.Equal("RUB", notification.Currency);
        Assert.Equal("paid", notification.Status);
    }

    [Fact]
    public async Task CloudPaymentsRejectsTamperedBody()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            $"InvoiceId={Guid.NewGuid():D}&TransactionId=42&Amount=1.00&Currency=RUB"));
        context.Request.Headers["Content-HMAC"] = Convert.ToBase64String(new byte[32]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client("cloud-test-secret").ReadNotificationAsync("cloudpayments", context.Request, CancellationToken.None));
    }

    [Theory]
    [InlineData("yookassa")]
    [InlineData("cloudpayments")]
    [InlineData("robokassa")]
    [InlineData("tbank")]
    [InlineData("stripe")]
    public void ProviderRequiresItsCompleteCredentialSet(string code)
    {
        var incomplete = new PaymentProviderOptions { Enabled = true, SecretKey = "secret" };
        Assert.False(PaymentProviderConfiguration.IsReady(code, incomplete));
    }

    [Theory]
    [InlineData("yookassa")]
    [InlineData("cloudpayments")]
    [InlineData("robokassa")]
    [InlineData("tbank")]
    [InlineData("stripe")]
    public void ProviderAcceptsItsCompleteCredentialSet(string code)
    {
        var complete = new PaymentProviderOptions
        {
            Enabled = true, MerchantId = "merchant", PublicId = "public",
            SecretKey = "secret", SecondarySecret = "secondary"
        };
        Assert.True(PaymentProviderConfiguration.IsReady(code, complete));
    }

    [Theory]
    [InlineData("yookassa", "https://pay.example/yoo")]
    [InlineData("cloudpayments", "https://pay.example/cloud")]
    [InlineData("robokassa", "https://auth.robokassa.ru/")]
    [InlineData("tbank", "https://pay.example/tbank")]
    [InlineData("stripe", "https://pay.example/stripe")]
    public async Task EveryProviderCreatesHostedCheckoutWithoutCardData(string provider, string expectedUrl)
    {
        var order = Order(provider);
        var client = FullClient(request => request.RequestUri!.Host switch
        {
            "api.yookassa.ru" => JsonResponse("""{"id":"yoo-1","confirmation":{"confirmation_url":"https://pay.example/yoo"}}"""),
            "api.cloudpayments.ru" => JsonResponse("""{"Model":{"Id":"cloud-1","Url":"https://pay.example/cloud"}}"""),
            "securepay.tinkoff.ru" => JsonResponse("""{"Success":true,"PaymentId":"tbank-1","PaymentURL":"https://pay.example/tbank"}"""),
            "api.stripe.com" => JsonResponse("""{"id":"stripe-1","url":"https://pay.example/stripe"}"""),
            _ => throw new InvalidOperationException("Unexpected host")
        });

        var result = await client.CreateAsync(order,
            new ApplicationUser { Id = order.UserId, Email = "payer@example.com" }, CancellationToken.None);

        Assert.StartsWith(expectedUrl, result.CheckoutUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("card", result.CheckoutUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task YooKassaNotificationIsVerifiedByFetchingCanonicalPayment()
    {
        var order = Guid.NewGuid();
        var context = Request("POST", """{"object":{"id":"yoo-payment"}}""");
        var verifiedBody = JsonSerializer.Serialize(new { id = "yoo-payment", status = "succeeded", amount = new { value = "499.00", currency = "RUB" }, metadata = new { order_id = order.ToString("D") } });
        var client = FullClient(_ => JsonResponse(verifiedBody));

        var result = await client.ReadNotificationAsync("yookassa", context.Request, CancellationToken.None);

        Assert.Equal(order, result.OrderId);
        Assert.Equal(49_900, result.AmountMinor);
        Assert.Equal(PaymentStatuses.Paid, result.Status);
    }

    [Fact]
    public async Task RobokassaResultUsesSecondPasswordAndExactAmount()
    {
        var order = Guid.NewGuid();
        const string amount = "499.00";
        const string invoice = "123";
        var signature = Sha256Hex($"{amount}:{invoice}:robo-password-2:Shp_order={order:D}");
        var context = Request("POST", $"OutSum={amount}&InvId={invoice}&Shp_order={order:D}&SignatureValue={signature}");

        var result = await FullClient().ReadNotificationAsync("robokassa", context.Request, CancellationToken.None);

        Assert.Equal(order, result.OrderId);
        Assert.Equal(49_900, result.AmountMinor);
        Assert.Equal(PaymentStatuses.Paid, result.Status);
    }

    [Fact]
    public async Task TBankNotificationRequiresSortedToken()
    {
        var order = Guid.NewGuid();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Amount"] = "49900", ["OrderId"] = order.ToString("D"), ["Password"] = "tbank-password",
            ["PaymentId"] = "987", ["Status"] = "CONFIRMED", ["Success"] = "true", ["TerminalKey"] = "terminal"
        };
        var token = Sha256Hex(string.Concat(values.Values));
        var context = Request("POST", JsonSerializer.Serialize(new { TerminalKey = "terminal", OrderId = order.ToString("D"), Success = true, Status = "CONFIRMED", PaymentId = 987, Amount = 49_900, Token = token }));

        var result = await FullClient().ReadNotificationAsync("tbank", context.Request, CancellationToken.None);

        Assert.Equal(order, result.OrderId);
        Assert.Equal(PaymentStatuses.Paid, result.Status);
    }

    [Fact]
    public async Task StripeNotificationRequiresFreshSignature()
    {
        var order = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new { type = "checkout.session.completed", data = new { @object = new { id = "cs_1", payment_status = "paid", amount_total = 49_900, currency = "rub", metadata = new { order_id = order.ToString("D") } } } });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("stripe-webhook"));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
        var context = Request("POST", body);
        context.Request.Headers["Stripe-Signature"] = $"t={timestamp},v1={signature}";

        var result = await FullClient().ReadNotificationAsync("stripe", context.Request, CancellationToken.None);

        Assert.Equal(order, result.OrderId);
        Assert.Equal("RUB", result.Currency);
        Assert.Equal(PaymentStatuses.Paid, result.Status);
    }

    private static PaymentGatewayClient Client(string secret)
    {
        var options = new PaymentOptions
        {
            Enabled = true,
            Providers = new Dictionary<string, PaymentProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["cloudpayments"] = new() { Enabled = true, PublicId = "public", SecretKey = secret }
            }
        };
        return new PaymentGatewayClient(new UnusedHttpClientFactory(), Options.Create(options));
    }

    private static PaymentGatewayClient FullClient(Func<HttpRequestMessage, HttpResponseMessage>? response = null)
    {
        var options = new PaymentOptions
        {
            Enabled = true, PublicBaseUrl = "https://proxy.example.com",
            Providers = new Dictionary<string, PaymentProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["yookassa"] = new() { Enabled = true, MerchantId = "shop", SecretKey = "yoo-secret" },
                ["cloudpayments"] = new() { Enabled = true, PublicId = "public", SecretKey = "cloud-secret" },
                ["robokassa"] = new() { Enabled = true, MerchantId = "merchant", SecretKey = "robo-password-1", SecondarySecret = "robo-password-2" },
                ["tbank"] = new() { Enabled = true, MerchantId = "terminal", SecretKey = "tbank-password" },
                ["stripe"] = new() { Enabled = true, SecretKey = "stripe-secret", SecondarySecret = "stripe-webhook" }
            }
        };
        return new PaymentGatewayClient(
            response is null ? new UnusedHttpClientFactory() : new StubHttpClientFactory(response), Options.Create(options));
    }

    private static PaymentOrder Order(string provider) => new()
    {
        Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ProductCode = "pro-monthly", Plan = "pro",
        Provider = provider, AmountMinor = 49_900, Currency = "RUB", DurationDays = 30,
        IdempotencyKey = Guid.NewGuid().ToString("N")
    };

    private static DefaultHttpContext Request(string method, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Network is not expected.");
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(response));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
