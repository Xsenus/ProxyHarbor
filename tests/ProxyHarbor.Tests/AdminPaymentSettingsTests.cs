using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class AdminPaymentSettingsTests
{
    [Fact]
    public async Task StoreEncryptsSecretsAndReturnsThemOnlyToServerRuntime()
    {
        await using var fixture = Fixture.Create();
        var configured = Options.Create(DefaultOptions());
        var store = new PaymentConfigurationStore(fixture.Db, configured, fixture.Protection);
        var options = await store.GetAsync();
        options.Providers["yookassa"].SecretKey = "merchant-secret-never-plaintext";
        await store.SaveAsync(options);

        var row = await fixture.Db.PaymentConfigurations.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("merchant-secret-never-plaintext", row.ProtectedSecrets, StringComparison.Ordinal);
        Assert.DoesNotContain("merchant-secret-never-plaintext", row.SettingsJson, StringComparison.Ordinal);
        Assert.Equal("merchant-secret-never-plaintext", (await store.GetAsync()).Providers["yookassa"].SecretKey);

        var response = Assert.IsType<OkObjectResult>(await Controller(store, fixture.Db).Get(default));
        Assert.DoesNotContain("merchant-secret-never-plaintext", JsonSerializer.Serialize(response.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminUpdateAppliesValidSettingsAndRejectsIncompleteEnabledProvider()
    {
        await using var fixture = Fixture.Create();
        var store = new PaymentConfigurationStore(fixture.Db, Options.Create(DefaultOptions()), fixture.Protection);
        var controller = Controller(store, fixture.Db);
        var request = Request(enabled: true, secret: null);
        Assert.IsType<BadRequestObjectResult>(await controller.Update(request, default));

        request.Providers.Single(x => x.Code == "yookassa").SecretKey = "new-yookassa-secret";
        var result = Assert.IsType<OkObjectResult>(await controller.Update(request, default));
        var serialized = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"enabled\":true", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new-yookassa-secret", serialized, StringComparison.Ordinal);
        var saved = await store.GetAsync();
        Assert.True(saved.Enabled);
        Assert.True(PaymentProviderConfiguration.IsReady("yookassa", saved.Providers["yookassa"]));

        // Пустое поле сохраняет прежний ключ, а отдельный clear-флаг удаляет его.
        var preserve = Request(enabled: true, secret: null);
        Assert.IsType<OkObjectResult>(await controller.Update(preserve, default));
        Assert.Equal("new-yookassa-secret", (await store.GetAsync()).Providers["yookassa"].SecretKey);
        var clear = Request(enabled: false, secret: null);
        var yookassa = clear.Providers.Single(x => x.Code == "yookassa");
        yookassa.Enabled = false;
        yookassa.ClearSecretKey = true;
        Assert.IsType<OkObjectResult>(await controller.Update(clear, default));
        Assert.Empty((await store.GetAsync()).Providers["yookassa"].SecretKey);
    }

    [Fact]
    public async Task AdminUpdateRejectsMalformedCatalogSnapshots()
    {
        await using var fixture = Fixture.Create();
        var controller = Controller(new PaymentConfigurationStore(
            fixture.Db, Options.Create(DefaultOptions()), fixture.Protection), fixture.Db);

        var empty = Request(false, null);
        empty.Products.Clear();
        Assert.IsType<BadRequestObjectResult>(await controller.Update(empty, default));

        var duplicate = Request(false, null);
        duplicate.Providers[1].Code = "yookassa";
        Assert.IsType<BadRequestObjectResult>(await controller.Update(duplicate, default));

        var invalidProduct = Request(false, null);
        invalidProduct.Products[0].AmountMinor = 0;
        Assert.IsType<BadRequestObjectResult>(await controller.Update(invalidProduct, default));

        var unknownProvider = Request(false, null);
        unknownProvider.Providers[0].Code = "unknown";
        Assert.IsType<BadRequestObjectResult>(await controller.Update(unknownProvider, default));

        var noProvider = Request(true, null);
        noProvider.Providers.ForEach(provider => provider.Enabled = false);
        Assert.IsType<BadRequestObjectResult>(await controller.Update(noProvider, default));
    }

    [Fact]
    public async Task AdminCanEnableBillingWithTelegramStarsWithoutExternalProvider()
    {
        await using var fixture = Fixture.Create();
        var store = new PaymentConfigurationStore(fixture.Db, Options.Create(DefaultOptions()), fixture.Protection);
        var telegram = ReadyTelegram();
        telegram.AutomaticProductCodes = SubscriptionPricingPolicy.Periods
            .Select(period => $"unlimited-{period.Code}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var controller = new AdminPaymentsController(store, new TelegramStore(telegram), fixture.Db);
        var request = Request(true, null);
        request.Providers.ForEach(provider => provider.Enabled = false);

        Assert.IsType<OkObjectResult>(await controller.Update(request, default));
        Assert.True((await store.GetAsync()).Enabled);
    }

    [Fact]
    public async Task AdminSnapshotHighlightsWebhookOnlyProviderWithoutSuccessfulPayments()
    {
        await using var fixture = Fixture.Create();
        var configured = DefaultOptions();
        configured.Enabled = true;
        configured.Providers["yoomoney"] = new PaymentProviderOptions
        {
            Enabled = true,
            DisplayName = "ЮMoney",
            MerchantId = "410011234567",
            SecretKey = "notification-secret"
        };
        fixture.Db.PaymentOrders.Add(new PaymentOrder
        {
            UserId = Guid.NewGuid(),
            ProductCode = "pro-monthly",
            Plan = SubscriptionPlans.Pro,
            Provider = "yoomoney",
            AmountMinor = 49_900,
            Currency = "RUB",
            DurationDays = 30,
            Status = PaymentStatuses.Canceled
        });
        await fixture.Db.SaveChangesAsync();
        var store = new PaymentConfigurationStore(
            fixture.Db, Options.Create(configured), fixture.Protection);

        var response = Assert.IsType<OkObjectResult>(
            await Controller(store, fixture.Db).Get(default));
        var serialized = JsonSerializer.Serialize(response.Value);

        Assert.Contains("\"state\":\"webhook_attention\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"totalOrders\":1", serialized, StringComparison.Ordinal);
        Assert.Contains("webhook URL", serialized, StringComparison.Ordinal);
        Assert.Contains("\"directReconciliationSupported\":false", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminSnapshotRequiresSuccessfulPaymentAfterLatestConfigurationSave()
    {
        await using var fixture = Fixture.Create();
        var configured = DefaultOptions();
        configured.Enabled = true;
        configured.Providers["yookassa"] = new PaymentProviderOptions
        {
            Enabled = true,
            DisplayName = "ЮKassa",
            MerchantId = "shop-id",
            SecretKey = "production-secret"
        };
        var store = new PaymentConfigurationStore(
            fixture.Db, Options.Create(configured), fixture.Protection);
        var beforeConfiguration = DateTimeOffset.UtcNow.AddHours(-1);
        fixture.Db.PaymentOrders.Add(PaidOrder("yookassa", beforeConfiguration));
        await fixture.Db.SaveChangesAsync();
        await store.SaveAsync(configured);

        var staleEvidence = Assert.IsType<OkObjectResult>(
            await Controller(store, fixture.Db).Get(default));
        var staleJson = JsonSerializer.Serialize(staleEvidence.Value);
        Assert.Contains("\"state\":\"retest_required\"", staleJson, StringComparison.Ordinal);
        Assert.Contains("\"paidAfterConfigurationUpdate\":0", staleJson, StringComparison.Ordinal);

        var configuredAt = await fixture.Db.PaymentConfigurations.AsNoTracking()
            .Select(configuration => configuration.UpdatedAt).SingleAsync();
        fixture.Db.PaymentOrders.Add(PaidOrder("yookassa", configuredAt.AddSeconds(1)));
        await fixture.Db.SaveChangesAsync();

        var currentEvidence = Assert.IsType<OkObjectResult>(
            await Controller(store, fixture.Db).Get(default));
        var currentJson = JsonSerializer.Serialize(currentEvidence.Value);
        Assert.Contains("\"state\":\"healthy\"", currentJson, StringComparison.Ordinal);
        Assert.Contains("\"paidAfterConfigurationUpdate\":1", currentJson, StringComparison.Ordinal);
        Assert.Contains("\"configurationUpdatedAt\":", currentJson, StringComparison.Ordinal);
    }

    private static AdminPaymentsController Controller(
        IPaymentConfigurationStore store,
        ProxyHarborDbContext db) =>
        new(store, new TelegramStore(new TelegramBotOptions()), db);

    private static TelegramBotOptions ReadyTelegram() => new()
    {
        Enabled = true,
        BotId = 1,
        BotToken = "token",
        WebhookSecret = "secret"
    };

    private static PaymentOrder PaidOrder(string provider, DateTimeOffset createdAt) => new()
    {
        UserId = Guid.NewGuid(),
        ProductCode = "pro-monthly",
        Plan = SubscriptionPlans.Pro,
        Provider = provider,
        AmountMinor = 49_900,
        Currency = "RUB",
        DurationDays = 30,
        Status = PaymentStatuses.Paid,
        CreatedAt = createdAt,
        PaidAt = createdAt.AddMinutes(1),
        UpdatedAt = createdAt.AddMinutes(1)
    };

    private sealed class TelegramStore(TelegramBotOptions value) : ITelegramBotConfigurationStore
    {
        public Task<TelegramBotOptions> GetAsync(CancellationToken token = default) => Task.FromResult(value);
        public Task SaveAsync(TelegramBotOptions options, CancellationToken token = default) => Task.CompletedTask;
    }

    private static UpdatePaymentSettingsRequest Request(bool enabled, string? secret) => new()
    {
        Enabled = enabled,
        Products = SubscriptionPricingPolicy.Build(3_700, "RUB").Select(pair => new UpdatePaymentProductRequest
        {
            Code = pair.Key,
            Enabled = pair.Value.Enabled,
            Name = pair.Value.Name,
            Plan = pair.Value.Plan,
            DurationDays = pair.Value.DurationDays,
            AmountMinor = pair.Value.AmountMinor,
            DiscountPercent = pair.Value.DiscountPercent,
            Currency = pair.Value.Currency,
            Description = pair.Value.Description
        }).ToList(),
        Providers = PaymentProviderConfiguration.Codes.Select(code => new UpdatePaymentProviderRequest
        {
            Code = code,
            Enabled = code == "yookassa",
            MerchantId = code == "yookassa" ? "shop-id" : string.Empty,
            SecretKey = code == "yookassa" ? secret : null
        }).ToList()
    };

    private static PaymentOptions DefaultOptions() => new()
    {
        PublicBaseUrl = "https://proxy.example",
        Products = new Dictionary<string, PaymentProductOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["pro-monthly"] = new()
            {
                Enabled = true,
                Name = "Pro",
                Plan = SubscriptionPlans.Pro,
                DurationDays = 30,
                AmountMinor = 49_900,
                Currency = "RUB"
            }
        },
        Providers = PaymentProviderConfiguration.Codes.ToDictionary(
            code => code,
            code => new PaymentProviderOptions { DisplayName = code },
            StringComparer.OrdinalIgnoreCase)
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        internal ProxyHarborDbContext Db { get; }
        internal IDataProtectionProvider Protection { get; }

        private Fixture(ServiceProvider services)
        {
            this.services = services;
            Db = services.GetRequiredService<ProxyHarborDbContext>();
            Protection = services.GetRequiredService<IDataProtectionProvider>();
        }

        internal static Fixture Create()
        {
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddDbContext<ProxyHarborDbContext>(builder =>
                builder.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
            collection.AddDataProtection().UseEphemeralDataProtectionProvider();
            return new Fixture(collection.BuildServiceProvider());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await services.DisposeAsync();
        }
    }
}
