using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;

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

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Network is not expected.");
    }
}
