using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;

namespace ProxyHarbor.Tests;

/// <summary>Промоблок нельзя показывать для неготовых или выключенных платёжных настроек.</summary>
public sealed class CommerceControllerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ShowsOfferOnlyWhenAProviderAndProductAreReady()
    {
        var payments = Options(true);
        payments.Providers["yookassa"] = new PaymentProviderOptions
        { Enabled = true, MerchantId = "shop", SecretKey = "secret" };
        var controller = Controller(payments, new TelegramBotOptions(), paid: false);

        var result = Assert.IsType<OkObjectResult>(await controller.Availability(CancellationToken.None));
        var json = JsonSerializer.SerializeToElement(result.Value, Json);

        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.True(json.GetProperty("showOffer").GetBoolean());
        Assert.Equal(1, json.GetProperty("paymentProviders").GetInt32());
    }

    [Fact]
    public async Task HidesOfferForPaidUserAndIncompletePaymentConfiguration()
    {
        var incomplete = Options(true);
        incomplete.Providers["yookassa"] = new PaymentProviderOptions { Enabled = true, MerchantId = "shop" };
        var controller = Controller(incomplete, new TelegramBotOptions(), paid: true);

        var result = Assert.IsType<OkObjectResult>(await controller.Availability(CancellationToken.None));
        var json = JsonSerializer.SerializeToElement(result.Value, Json);

        Assert.False(json.GetProperty("available").GetBoolean());
        Assert.False(json.GetProperty("showOffer").GetBoolean());
        Assert.True(json.GetProperty("fullAccess").GetBoolean());
    }

    [Fact]
    public async Task DoesNotAdvertiseTelegramStarsWhileBillingIsDisabled()
    {
        var payments = Options(false);
        var telegram = new TelegramBotOptions
        {
            Enabled = true,
            BotId = 1,
            BotToken = "token",
            WebhookSecret = "secret",
            AutomaticProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pro-30" }
        };
        var controller = Controller(payments, telegram, paid: false);

        var result = Assert.IsType<OkObjectResult>(await controller.Availability(CancellationToken.None));
        var json = JsonSerializer.SerializeToElement(result.Value, Json);

        Assert.False(json.GetProperty("available").GetBoolean());
        Assert.False(json.GetProperty("telegram").GetBoolean());
    }

    private static CommerceController Controller(PaymentOptions payments, TelegramBotOptions telegram, bool paid)
    {
        var controller = new CommerceController(new PaymentStore(payments), new TelegramStore(telegram), new Access(paid));
        controller.ControllerContext = new ControllerContext
        { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() } };
        return controller;
    }

    private static PaymentOptions Options(bool enabled) => new()
    {
        Enabled = enabled,
        Products = new(StringComparer.OrdinalIgnoreCase)
        {
            ["pro-30"] = new PaymentProductOptions
            { Enabled = true, Plan = "pro", AmountMinor = 49_900, DurationDays = 30 }
        }
    };

    private sealed class PaymentStore(PaymentOptions value) : IPaymentConfigurationStore
    {
        public Task<PaymentOptions> GetAsync(CancellationToken token = default) => Task.FromResult(value);
        public Task SaveAsync(PaymentOptions options, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class TelegramStore(TelegramBotOptions value) : ITelegramBotConfigurationStore
    {
        public Task<TelegramBotOptions> GetAsync(CancellationToken token = default) => Task.FromResult(value);
        public Task SaveAsync(TelegramBotOptions options, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class Access(bool paid) : IFreeExportAccessService
    {
        public Task<FreeExportAccess> AcquireAsync(ClaimsPrincipal principal, string? remoteIp, CancellationToken cancellationToken) =>
            Task.FromResult(new FreeExportAccess(true, paid, paid ? int.MaxValue : 10, null, paid ? "paid" : "free"));
        public Task<bool> HasPaidAccessAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) => Task.FromResult(paid);
    }
}
