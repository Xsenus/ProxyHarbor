using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет жизненный цикл заказа, подписки и повторного webhook на уровне контроллера.</summary>
public sealed class PaymentControllerTests
{
    [Fact]
    public async Task CheckoutHistoryAndRepeatedWebhookGrantSubscriptionExactlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var options = OptionsFor("cloudpayments");
        var controller = fixture.Controller(options);

        var checkout = await controller.Checkout(new CreateCheckoutRequest
        { ProductCode = "pro-monthly", Provider = "cloudpayments" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(checkout);
        var order = await fixture.Db.PaymentOrders.SingleAsync();
        Assert.Equal(PaymentStatuses.Pending, order.Status);
        Assert.NotNull(order.CheckoutUrl);

        Assert.IsType<OkObjectResult>(await controller.Orders(CancellationToken.None));
        var body = $"InvoiceId={order.Id:D}&TransactionId=4242&Amount=499.00&Currency=RUB";
        SetWebhookRequest(controller, body, "cloud-secret");
        Assert.IsType<OkObjectResult>(await controller.Webhook("cloudpayments", CancellationToken.None));
        var firstExpiry = (await fixture.Db.Subscriptions.SingleAsync()).ExpiresAt;

        SetWebhookRequest(controller, body, "cloud-secret");
        Assert.IsType<OkObjectResult>(await controller.Webhook("cloudpayments", CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(PaymentStatuses.Paid, (await fixture.Db.PaymentOrders.SingleAsync()).Status);
        Assert.Equal(firstExpiry, (await fixture.Db.Subscriptions.SingleAsync()).ExpiresAt);
        Assert.True(await fixture.Users.IsInRoleAsync(fixture.User, UserRoles.Subscriber));
    }

    [Fact]
    public async Task RefundRevokesOnlySubscriptionActivatedByThatPayment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var order = new PaymentOrder
        {
            UserId = fixture.User.Id, ProductCode = "pro-monthly", Plan = SubscriptionPlans.Pro,
            Provider = "tbank", ProviderPaymentId = "987", AmountMinor = 49_900, Currency = "RUB",
            DurationDays = 30, Status = PaymentStatuses.Paid, PaidAt = DateTimeOffset.UtcNow
        };
        fixture.Db.PaymentOrders.Add(order);
        var subscription = await fixture.Db.Subscriptions.SingleAsync();
        subscription.Plan = SubscriptionPlans.Pro;
        subscription.ExternalSubscriptionId = "987";
        subscription.ExpiresAt = DateTimeOffset.UtcNow.AddDays(30);
        await fixture.Users.AddToRoleAsync(fixture.User, UserRoles.Subscriber);
        await fixture.Db.SaveChangesAsync();
        var controller = fixture.Controller(OptionsFor("tbank"));
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Amount"] = "49900", ["OrderId"] = order.Id.ToString("D"), ["Password"] = "tbank-password",
            ["PaymentId"] = "987", ["Status"] = "REFUNDED", ["Success"] = "true", ["TerminalKey"] = "terminal"
        };
        var token = Sha256Hex(string.Concat(values.Values));
        var body = JsonSerializer.Serialize(new { TerminalKey = "terminal", OrderId = order.Id.ToString("D"), Success = true, Status = "REFUNDED", PaymentId = 987, Amount = 49_900, Token = token });
        controller.ControllerContext.HttpContext.Request.Method = "POST";
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        Assert.IsType<ContentResult>(await controller.Webhook("tbank", CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(PaymentStatuses.Refunded, (await fixture.Db.PaymentOrders.SingleAsync()).Status);
        Assert.Equal(SubscriptionStatuses.Canceled, (await fixture.Db.Subscriptions.SingleAsync()).Status);
        Assert.False(await fixture.Users.IsInRoleAsync(fixture.User, UserRoles.Subscriber));
    }

    [Fact]
    public async Task CatalogAndValidationFailClosedForUnavailableChoices()
    {
        await using var fixture = await Fixture.CreateAsync();
        var disabled = new PaymentOptions { Enabled = false };
        var controller = fixture.Controller(disabled);
        var catalog = Assert.IsType<OkObjectResult>(controller.Catalog());
        Assert.Contains("\"enabled\":false", JsonSerializer.Serialize(catalog.Value), StringComparison.Ordinal);

        Assert.IsType<ObjectResult>(await controller.Checkout(new CreateCheckoutRequest
        { ProductCode = "missing", Provider = "unknown" }, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Webhook("unknown", CancellationToken.None));

        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.IsType<UnauthorizedResult>(await controller.Orders(CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.Checkout(new CreateCheckoutRequest
        { ProductCode = "pro-monthly", Provider = "cloudpayments" }, CancellationToken.None));
    }

    private static PaymentOptions OptionsFor(string provider) => new()
    {
        Enabled = true,
        PublicBaseUrl = "https://proxy.example.com",
        Products = new Dictionary<string, PaymentProductOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["pro-monthly"] = new() { Enabled = true, Name = "Pro", Plan = "pro", DurationDays = 30, AmountMinor = 49_900, Currency = "RUB" }
        },
        Providers = new Dictionary<string, PaymentProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["cloudpayments"] = new() { Enabled = provider == "cloudpayments", DisplayName = "CloudPayments", PublicId = "cloud-public", SecretKey = "cloud-secret" },
            ["tbank"] = new() { Enabled = provider == "tbank", DisplayName = "Т-Банк", MerchantId = "terminal", SecretKey = "tbank-password" }
        }
    };

    private static void SetWebhookRequest(PaymentsController controller, string body, string secret)
    {
        controller.ControllerContext.HttpContext.Request.Method = "POST";
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        controller.ControllerContext.HttpContext.Request.Headers["Content-HMAC"] =
            Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        internal ProxyHarborDbContext Db { get; }
        internal UserManager<ApplicationUser> Users { get; }
        internal ApplicationUser User { get; }

        private Fixture(ServiceProvider services, ProxyHarborDbContext db, UserManager<ApplicationUser> users, ApplicationUser user)
        { this.services = services; Db = db; Users = users; User = user; }

        internal static async Task<Fixture> CreateAsync()
        {
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddDbContext<ProxyHarborDbContext>(builder => builder
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            collection.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<ProxyHarborDbContext>();
            var services = collection.BuildServiceProvider();
            var db = services.GetRequiredService<ProxyHarborDbContext>();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            await services.GetRequiredService<RoleManager<IdentityRole<Guid>>>()
                .CreateAsync(new IdentityRole<Guid>(UserRoles.User));
            await services.GetRequiredService<RoleManager<IdentityRole<Guid>>>()
                .CreateAsync(new IdentityRole<Guid>(UserRoles.Subscriber));
            var user = new ApplicationUser { UserName = "payer", Email = "payer@example.com" };
            Assert.True((await users.CreateAsync(user)).Succeeded);
            await users.AddToRoleAsync(user, UserRoles.User);
            db.Subscriptions.Add(new UserSubscription { UserId = user.Id });
            await db.SaveChangesAsync();
            return new Fixture(services, db, users, user);
        }

        internal PaymentsController Controller(PaymentOptions options)
        {
            var gateway = new PaymentGatewayClient(new StubHttpClientFactory(), Options.Create(options));
            var controller = new PaymentsController(Users, Db, gateway, Options.Create(options));
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, User.Id.ToString())], "test"));
            return controller;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"Model":{"Id":"invoice","Url":"https://pay.example/cloud"}}""", Encoding.UTF8, "application/json") });
    }
}
