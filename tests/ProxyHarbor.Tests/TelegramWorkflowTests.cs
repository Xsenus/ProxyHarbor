using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class TelegramWorkflowTests
{
    [Fact]
    public async Task MessageCommandsProvisionAccountAndQueuePersonalCabinetFeatures()
    {
        await using var fixture = await Fixture.CreateAsync();
        var processor = fixture.Processor();
        await processor.ProcessAsync(Message(1, "/start"), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Callback(2, "buy:pro-30"), TelegramUpdateModes.Webhook, CancellationToken.None);
        Assert.Empty(fixture.Db.PaymentOrders);
        await processor.ProcessAsync(Callback(3, "legal:offer"), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Callback(4, "legal:personal-data"), TelegramUpdateModes.Webhook, CancellationToken.None);
        var commands = new[]
        {
            "/account", "/buy", "/proxies", "/notifications", "/support", "/help",
            "как оплатить stars", "где файл прокси", "как проверяется скорость", "срок подписки", "вопрос оператору"
        };
        for (var index = 0; index < commands.Length; index++)
            await processor.ProcessAsync(Message(index + 5, commands[index]), TelegramUpdateModes.Webhook, CancellationToken.None);

        Assert.Single(fixture.Db.TelegramChats);
        Assert.Single(fixture.Db.Subscriptions);
        Assert.Equal(commands.Length + 4, await fixture.Db.TelegramUpdateReceipts.CountAsync());
        Assert.True(await fixture.Db.TelegramOutboundMessages.CountAsync() >= commands.Length);
        Assert.Contains(await fixture.Db.TelegramOutboundMessages.ToArrayAsync(), x => x.Kind == TelegramOutboundKinds.ProxyFile);
        Assert.Contains(await fixture.Db.TelegramConversationMessages.ToArrayAsync(), x =>
            x.Direction == "bot" && x.Text.Contains("Stars", StringComparison.Ordinal));
        var user = await fixture.Db.Users.SingleAsync();
        Assert.Equal("tg.9001@telegram.proxyharbor.invalid", user.Email);
        Assert.Equal(LegalDocumentVersions.Offer, user.OfferVersion);
        Assert.NotNull(user.OfferAcceptedAt);
        Assert.Equal(LegalDocumentVersions.PersonalDataConsent, user.PersonalDataConsentVersion);
        Assert.NotNull(user.PersonalDataConsentAcceptedAt);
        Assert.True(await fixture.Users.IsInRoleAsync(user, UserRoles.User));

        await processor.ProcessAsync(Message(1, "/start"), TelegramUpdateModes.Webhook, CancellationToken.None);
        Assert.Equal(commands.Length + 4, await fixture.Db.TelegramUpdateReceipts.CountAsync());

        var subscription = await fixture.Db.Subscriptions.SingleAsync();
        subscription.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();
        await processor.ProcessAsync(Message(50, "/proxies"), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Message(51, "/account"), TelegramUpdateModes.Webhook, CancellationToken.None);
        Assert.Contains(await fixture.Db.TelegramConversationMessages.ToArrayAsync(), x =>
            x.Direction == "bot" && x.Text.Contains("нужна активная подписка", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarketingConsentIsOffByDefaultAndWithdrawalIsAudited()
    {
        await using var fixture = await Fixture.CreateAsync();
        var processor = fixture.Processor();
        await processor.ProcessAsync(Message(1, "/start"), TelegramUpdateModes.Webhook, CancellationToken.None);
        var chat = await fixture.Db.TelegramChats.SingleAsync();
        Assert.False(chat.MarketingNotificationsEnabled);
        Assert.Null(chat.MarketingConsentGrantedAt);

        await processor.ProcessAsync(Callback(2, "notifications:marketing"), TelegramUpdateModes.Webhook, CancellationToken.None);
        Assert.True(chat.MarketingNotificationsEnabled);
        Assert.Equal(LegalDocumentVersions.MarketingConsent, chat.MarketingConsentVersion);
        Assert.NotNull(chat.MarketingConsentGrantedAt);
        Assert.Null(chat.MarketingConsentWithdrawnAt);

        await processor.ProcessAsync(Callback(3, "notifications:marketing"), TelegramUpdateModes.Webhook, CancellationToken.None);
        Assert.False(chat.MarketingNotificationsEnabled);
        Assert.NotNull(chat.MarketingConsentWithdrawnAt);
    }

    [Fact]
    public async Task CallbackAcknowledgementFailureDoesNotReplayCompletedBusinessAction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var processor = fixture.Processor();
        await processor.ProcessAsync(Message(1, "/start"), TelegramUpdateModes.Polling, CancellationToken.None);
        fixture.Telegram.FailMethod = "answerCallbackQuery";

        await processor.ProcessAsync(
            Callback(2, "notifications:marketing"), TelegramUpdateModes.Polling, CancellationToken.None);

        var chat = await fixture.Db.TelegramChats.SingleAsync();
        Assert.True(chat.MarketingNotificationsEnabled);
        Assert.Contains(fixture.Db.TelegramUpdateReceipts, receipt => receipt.UpdateId == 2);
    }

    [Fact]
    public async Task DeployPolicyPreventsMarketingConsentAndBroadcast()
    {
        await using var fixture = await Fixture.CreateAsync(marketingBroadcastsEnabled: false);
        var processor = fixture.Processor();
        await processor.ProcessAsync(Message(1, "/start"), TelegramUpdateModes.Webhook, CancellationToken.None);
        var chat = await fixture.Db.TelegramChats.SingleAsync();

        await processor.ProcessAsync(Callback(2, "notifications:marketing"), TelegramUpdateModes.Webhook, CancellationToken.None);

        Assert.False(chat.MarketingNotificationsEnabled);
        Assert.Null(chat.MarketingConsentGrantedAt);
        var response = Assert.IsType<ObjectResult>(await fixture.AdminController().Send(
            new SendTelegramMessageRequest { Broadcast = true, Text = "рассылка" }, CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Empty(fixture.Db.TelegramOutboundMessages.Where(x => x.IdempotencyKey.StartsWith("broadcast:")));
    }

    [Fact]
    public async Task TelegramStartDeepLinkCreatesReferralAndRewardsOwnerOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.CreateWebUserAsync("referral-owner");
        owner.ReferralCode = "abc123def456";
        await fixture.Users.UpdateAsync(owner);
        var processor = fixture.Processor();

        await processor.ProcessAsync(Message(1, "/start ref_abc123def456"), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Message(2, "/start ref_abc123def456"), TelegramUpdateModes.Webhook, CancellationToken.None);

        var relationship = await fixture.Db.ReferralRelationships.Include(x => x.Rewards).SingleAsync();
        Assert.Equal(owner.Id, relationship.ReferrerUserId);
        Assert.Single(relationship.Rewards);
        Assert.Equal(ReferralRewardKinds.Signup, relationship.Rewards.Single().Kind);
        Assert.Equal(1, relationship.Rewards.Single().DaysGranted);
    }

    [Fact]
    public async Task MembershipUpdateMarksKnownChatBlockedAndIgnoresUnknownOrNonPrivateMessages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var processor = fixture.Processor();
        await processor.ProcessAsync(Message(1, "/start"), TelegramUpdateModes.Polling, CancellationToken.None);
        await processor.ProcessAsync(Json("""{"update_id":2,"my_chat_member":{"chat":{"id":7001},"new_chat_member":{"status":"kicked"}}}"""), TelegramUpdateModes.Polling, CancellationToken.None);
        Assert.True((await fixture.Db.TelegramChats.SingleAsync()).IsBlocked);

        await processor.ProcessAsync(Json("""{"update_id":3,"my_chat_member":{"chat":{"id":9999},"new_chat_member":{"status":"left"}}}"""), TelegramUpdateModes.Polling, CancellationToken.None);
        await processor.ProcessAsync(Json("""{"update_id":4,"message":{"chat":{"id":10,"type":"group"},"from":{"id":10},"text":"/start"}}"""), TelegramUpdateModes.Polling, CancellationToken.None);
        Assert.Single(fixture.Db.TelegramChats);
    }

    [Fact]
    public async Task MalformedAndMinimalUpdatesAreHandledWithoutInventingData()
    {
        await using var fixture = await Fixture.CreateAsync();
        var processor = fixture.Processor();
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(
            Json("""{"message":{"text":"missing update id"}}"""), TelegramUpdateModes.Webhook, CancellationToken.None));
        await processor.ProcessAsync(Json("""{"update_id":60,"message":{"from":{"id":1},"text":"missing chat"}}"""), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Json("""{"update_id":61,"message":{"chat":{"id":1,"type":"private"},"text":"missing from"}}"""), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Json("""{"update_id":62,"message":{"chat":{"id":8002,"type":"private"},"from":{"id":9002},"text":"   "}}"""), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Json("""{"update_id":63,"message":{"chat":{"id":8002,"type":"private"},"from":{"id":9002}}}"""), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Json("""{"update_id":64,"my_chat_member":{"new_chat_member":{"status":"member"}}}"""), TelegramUpdateModes.Webhook, CancellationToken.None);

        Assert.Single(fixture.Db.TelegramChats);
        var chat = await fixture.Db.TelegramChats.SingleAsync();
        Assert.Equal("Telegram 9002", chat.DisplayName);
        Assert.Null(chat.Username);
        Assert.Null(chat.LanguageCode);
        Assert.Equal(5, await fixture.Db.TelegramUpdateReceipts.CountAsync());
        Assert.Equal(new string('x', 12), TelegramDispatchService.Limit(new string('x', 20), 12));
    }

    [Fact]
    public async Task CallbackCheckoutAndSuccessfulStarsPaymentActivateSubscriptionExactlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var processor = fixture.Processor();
        await processor.ProcessAsync(Message(1, "/start"), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Json("""{"update_id":2,"callback_query":{"id":"bad","from":{"id":9001},"data":"account"}}"""), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Callback(3, "legal:offer"), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Callback(4, "legal:personal-data"), TelegramUpdateModes.Webhook, CancellationToken.None);

        var callbacks = new[] { "account", "products", "proxies", "notifications", "unknown", "buy:missing", "buy:pro-30", "pay:telegram_stars:pro-30" };
        for (var index = 0; index < callbacks.Length; index++)
            await processor.ProcessAsync(Callback(10 + index, callbacks[index]), TelegramUpdateModes.Webhook, CancellationToken.None);
        var order = await fixture.Db.PaymentOrders.SingleAsync();
        Assert.Equal(PaymentStatuses.Pending, order.Status);

        await processor.ProcessAsync(PreCheckout(30, "invalid", "BAD", 1), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(PreCheckout(32, "invalid", "XTR", order.AmountMinor), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(PreCheckout(33, order.Id.ToString("N"), "XTR", order.AmountMinor + 1), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(PreCheckout(31, order.Id.ToString("N"), "XTR", order.AmountMinor), TelegramUpdateModes.Webhook, CancellationToken.None);
        // Имитируем гонку: pre-checkout уже подтверждён Telegram, а фоновая
        // очистка успела завершить старый invoice до доставки successful_payment.
        order.Status = PaymentStatuses.Canceled;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();
        await processor.ProcessAsync(SuccessfulPayment(40, order.Id, order.AmountMinor, "charge-1"), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(SuccessfulPayment(41, order.Id, order.AmountMinor, "charge-1"), TelegramUpdateModes.Webhook, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(PaymentStatuses.Paid, (await fixture.Db.PaymentOrders.SingleAsync()).Status);
        var subscription = await fixture.Db.Subscriptions.SingleAsync();
        Assert.Equal(SubscriptionPlans.Pro, subscription.Plan);
        Assert.NotNull(subscription.ExpiresAt);
        var account = await fixture.Db.Users.SingleAsync();
        Assert.True(await fixture.Users.IsInRoleAsync(account, UserRoles.Subscriber));
        Assert.Contains("answerPreCheckoutQuery", fixture.Telegram.Methods);
        Assert.Contains("answerCallbackQuery", fixture.Telegram.Methods);
        Assert.Contains(fixture.Telegram.Requests, request =>
            request.Method == "answerCallbackQuery" &&
            request.Body.Contains("callback-10", StringComparison.Ordinal) &&
            !request.Body.Contains("\"text\"", StringComparison.Ordinal));
        await processor.ProcessAsync(Message(42, "/account"), TelegramUpdateModes.Webhook, CancellationToken.None);
    }

    [Fact]
    public async Task BuyOffersEveryReadyProviderAndCreatesExternalCheckout()
    {
        await using var fixture = await Fixture.CreateAsync();
        var processor = fixture.Processor();
        await processor.ProcessAsync(Message(1, "/start"), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Callback(2, "legal:offer"), TelegramUpdateModes.Webhook, CancellationToken.None);
        await processor.ProcessAsync(Callback(3, "legal:personal-data"), TelegramUpdateModes.Webhook, CancellationToken.None);

        await processor.ProcessAsync(Callback(4, "buy:pro-30"), TelegramUpdateModes.Webhook, CancellationToken.None);
        var methodMessage = await fixture.Db.TelegramOutboundMessages
            .Where(x => x.Kind == TelegramOutboundKinds.Text).OrderByDescending(x => x.CreatedAt).FirstAsync();
        Assert.Contains("telegram_stars", methodMessage.PayloadJson, StringComparison.Ordinal);
        foreach (var provider in new[] { "yookassa", "yoomoney", "cloudpayments", "robokassa", "tbank", "stripe", "cryptomus", "nowpayments" })
            Assert.Contains($"pay:{provider}:pro-30", methodMessage.PayloadJson, StringComparison.Ordinal);

        await processor.ProcessAsync(Callback(5, "pay:yoomoney:pro-30"), TelegramUpdateModes.Webhook, CancellationToken.None);

        var order = await fixture.Db.PaymentOrders.SingleAsync();
        Assert.Equal("yoomoney", order.Provider);
        Assert.Equal(PaymentStatuses.Pending, order.Status);
        var checkoutUrl = Assert.IsType<string>(order.CheckoutUrl);
        Assert.StartsWith("https://proxy.example.test/api/v1/payments/hosted/yoomoney/", checkoutUrl, StringComparison.Ordinal);
        Assert.Contains(await fixture.Db.TelegramOutboundMessages.ToArrayAsync(), x =>
            x.PayloadJson.Contains(checkoutUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdminControllerCoversSettingsStatisticsCrmAndQueueActions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.CreateWebUserAsync("crm-user");
        var chat = new TelegramChat
        {
            ChatId = 8123,
            TelegramUserId = 8123,
            UserId = user.Id,
            User = user,
            Username = "crm_user",
            DisplayName = "CRM User",
            NotificationsEnabled = true,
            MarketingNotificationsEnabled = true,
            MarketingConsentGrantedAt = DateTimeOffset.UtcNow,
            MarketingConsentVersion = LegalDocumentVersions.MarketingConsent
        };
        fixture.Db.TelegramChats.Add(chat);
        fixture.Db.TelegramConversationMessages.Add(new TelegramConversationMessage
        { TelegramChatId = chat.Id, Text = "Здравствуйте", Direction = "inbound" });
        await fixture.Db.SaveChangesAsync();
        var controller = fixture.AdminController();

        Assert.IsType<OkObjectResult>(await controller.Get(CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.Chats(1, 10, "crm_user", CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.Chats(1, 10, "8123", CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.Messages(chat.Id, 100, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Messages(Guid.NewGuid(), 100, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Send(new SendTelegramMessageRequest { Text = " " }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Send(new SendTelegramMessageRequest { Text = "ответ" }, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Send(new SendTelegramMessageRequest { ChatId = Guid.NewGuid(), Text = "ответ" }, CancellationToken.None));
        Assert.IsType<AcceptedResult>(await controller.Send(new SendTelegramMessageRequest { ChatId = chat.Id, Text = "ответ" }, CancellationToken.None));
        Assert.IsType<AcceptedResult>(await controller.Send(new SendTelegramMessageRequest { Broadcast = true, Text = "рассылка" }, CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.UpdateChat(chat.Id,
            new UpdateTelegramChatRequest { IsBlocked = true, NotificationsEnabled = false }, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.UpdateChat(Guid.NewGuid(), new UpdateTelegramChatRequest(), CancellationToken.None));
        Assert.True((await fixture.Db.TelegramChats.SingleAsync()).IsBlocked);
    }

    [Fact]
    public async Task AdminSettingsValidateThenProvisionAllTelegramProfileParts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var controller = fixture.AdminController();
        await fixture.BotStore.SaveAsync(new TelegramBotOptions { PublicBaseUrl = "https://proxy.example.test" });
        Assert.IsType<BadRequestObjectResult>(await controller.Provision(CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Update(new UpdateTelegramBotRequest
        { Description = "слишком коротко", ShortDescription = "short", SupportText = "support", BotToken = "invalid token" }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Update(new UpdateTelegramBotRequest
        {
            Description = "Достаточно длинное описание Telegram-бота.",
            ShortDescription = "Короткое описание",
            SupportText = "Ответ оператора",
            BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN",
            ProductStars = new Dictionary<string, int> { ["unknown"] = 50 }
        }, CancellationToken.None));

        var result = await controller.Update(new UpdateTelegramBotRequest
        {
            Enabled = true,
            UpdateMode = TelegramUpdateModes.Webhook,
            Name = "ProxyHarbor",
            Description = "Проверенные прокси и управление подпиской ProxyHarbor.",
            ShortDescription = "Прокси, подписка и личный кабинет.",
            SupportText = "Оператор ответит в этом чате.",
            ProxyFileMaxItems = 500,
            WebhookMaxConnections = 20,
            BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN",
            ProductStars = new Dictionary<string, int> { ["pro-30"] = 250 }
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        var saved = await fixture.BotStore.GetAsync();
        Assert.True(saved.Enabled);
        Assert.Equal("ProxyHarborTestBot", saved.BotUsername);
        Assert.NotNull(saved.ProvisionedAt);
        Assert.Contains("getMe", fixture.Telegram.Methods);
        Assert.Contains("setMyProfilePhoto", fixture.Telegram.Methods);
        Assert.Contains("setWebhook", fixture.Telegram.Methods);
        Assert.IsType<OkObjectResult>(await controller.Provision(CancellationToken.None));
    }

    [Fact]
    public async Task FailedProvisionKeepsPreviousActiveTelegramConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.BotStore.GetAsync();
        fixture.Telegram.FailMethod = "deleteWebhook";

        var result = await fixture.AdminController().Update(new UpdateTelegramBotRequest
        {
            Enabled = true,
            UpdateMode = TelegramUpdateModes.Polling,
            Name = "Changed name",
            Description = "Изменённое достаточно длинное описание Telegram-бота.",
            ShortDescription = "Изменённое короткое описание",
            SupportText = "Изменённый ответ оператора.",
            ProxyFileMaxItems = 750,
            WebhookMaxConnections = 30,
            BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN",
            ProductStars = new Dictionary<string, int> { ["pro-30"] = 300 }
        }, CancellationToken.None);

        Assert.IsType<ObjectResult>(result);
        var saved = await fixture.BotStore.GetAsync();
        Assert.True(saved.Enabled);
        Assert.Equal(before.UpdateMode, saved.UpdateMode);
        Assert.Equal(before.Name, saved.Name);
        Assert.Equal(before.ProductStars, saved.ProductStars);
    }

    private static JsonElement Message(int updateId, string text) => JsonSerializer.SerializeToElement(new
    {
        update_id = updateId,
        message = new
        {
            chat = new { id = 7001, type = "private" },
            from = new
            {
                id = 9001,
                is_bot = false,
                first_name = "Иван",
                last_name = "Тест",
                username = "ivan_test",
                language_code = "ru"
            },
            text
        }
    });

    private static JsonElement Callback(int updateId, string data) => JsonSerializer.SerializeToElement(new
    {
        update_id = updateId,
        callback_query = new
        {
            id = $"callback-{updateId}",
            from = new { id = 9001, first_name = "Иван", username = "ivan_test" },
            message = new { chat = new { id = 7001, type = "private" } },
            data
        }
    });

    private static JsonElement PreCheckout(int updateId, string payload, string currency, long amount) =>
        JsonSerializer.SerializeToElement(new
        {
            update_id = updateId,
            pre_checkout_query = new
            {
                id = $"checkout-{updateId}",
                from = new { id = 9001 },
                currency,
                total_amount = amount,
                invoice_payload = payload
            }
        });

    private static JsonElement SuccessfulPayment(int updateId, Guid orderId, long amount, string chargeId) =>
        JsonSerializer.SerializeToElement(new
        {
            update_id = updateId,
            message = new
            {
                chat = new { id = 7001, type = "private" },
                from = new { id = 9001, first_name = "Иван", username = "ivan_test" },
                successful_payment = new
                {
                    currency = "XTR",
                    total_amount = amount,
                    invoice_payload = orderId.ToString("N"),
                    telegram_payment_charge_id = chargeId,
                    provider_payment_charge_id = ""
                }
            }
        });

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        internal ProxyHarborDbContext Db { get; }
        internal UserManager<ApplicationUser> Users { get; }
        internal TelegramBotConfigurationStore BotStore { get; }
        internal StaticPaymentStore Payments { get; }
        internal RecordingTelegramFactory Telegram { get; }

        private Fixture(ServiceProvider services, ProxyHarborDbContext db, UserManager<ApplicationUser> users,
            TelegramBotConfigurationStore botStore, StaticPaymentStore payments, RecordingTelegramFactory telegram)
        { this.services = services; Db = db; Users = users; BotStore = botStore; Payments = payments; Telegram = telegram; }

        internal static async Task<Fixture> CreateAsync(bool marketingBroadcastsEnabled = true)
        {
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddDbContext<ProxyHarborDbContext>(builder => builder
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            collection.AddIdentityCore<ApplicationUser>(options => options.User.RequireUniqueEmail = true)
                .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<ProxyHarborDbContext>();
            var services = collection.BuildServiceProvider();
            var db = services.GetRequiredService<ProxyHarborDbContext>();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            foreach (var role in new[] { UserRoles.User, UserRoles.Subscriber, UserRoles.Administrator })
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);
            var protection = new EphemeralDataProtectionProvider();
            var botStore = new TelegramBotConfigurationStore(db,
                Options.Create(new TelegramBotHostOptions
                {
                    PublicBaseUrl = "https://proxy.example.test",
                    MarketingBroadcastsEnabled = marketingBroadcastsEnabled
                }), protection);
            await botStore.SaveAsync(new TelegramBotOptions
            {
                Enabled = true,
                UpdateMode = TelegramUpdateModes.Webhook,
                PublicBaseUrl = "https://proxy.example.test",
                BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN",
                WebhookSecret = "webhook_secret_for_tests",
                BotId = 42,
                BotUsername = "ProxyHarborTestBot",
                ProductStars = new Dictionary<string, int> { ["pro-30"] = 250 }
            });
            var paymentOptions = new PaymentOptions
            {
                Enabled = true,
                PublicBaseUrl = "https://proxy.example.test",
                Products = new Dictionary<string, PaymentProductOptions>
                {
                    ["pro-30"] = new() { Enabled = true, Name = "Pro 30", Plan = SubscriptionPlans.Pro, DurationDays = 30, AmountMinor = 49_900, Currency = "RUB", Description = "Pro" }
                },
                Providers = new Dictionary<string, PaymentProviderOptions>
                {
                    ["yookassa"] = new() { Enabled = true, MerchantId = "shop", SecretKey = "secret" },
                    ["yoomoney"] = new()
                    {
                        Enabled = true,
                        DisplayName = "ЮMoney",
                        MerchantId = "410011234567890",
                        SecretKey = "test-notification-secret"
                    },
                    ["cloudpayments"] = new() { Enabled = true, PublicId = "public", SecretKey = "secret" },
                    ["robokassa"] = new() { Enabled = true, MerchantId = "merchant", SecretKey = "secret", SecondarySecret = "secret2" },
                    ["tbank"] = new() { Enabled = true, MerchantId = "terminal", SecretKey = "secret" },
                    ["stripe"] = new() { Enabled = true, SecretKey = "secret", SecondarySecret = "webhook" },
                    ["cryptomus"] = new() { Enabled = true, MerchantId = "merchant", SecretKey = "secret" },
                    ["nowpayments"] = new() { Enabled = true, SecretKey = "secret", SecondarySecret = "ipn" }
                }
            };
            return new Fixture(services, db, users, botStore, new StaticPaymentStore(paymentOptions), new RecordingTelegramFactory());
        }

        internal TelegramUpdateProcessor Processor() => new(Db, Users, BotStore, Payments,
            new PaymentGatewayClient(Telegram, Payments),
            new TelegramBotApiClient(Telegram), new TelegramDispatchService(Db), NullLogger<TelegramUpdateProcessor>.Instance);

        internal AdminTelegramController AdminController()
        {
            var controller = new AdminTelegramController(Db, BotStore, Payments,
                new TelegramBotApiClient(Telegram), new TelegramDispatchService(Db));
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, UserRoles.Administrator)], "test"));
            return controller;
        }

        internal async Task<ApplicationUser> CreateWebUserAsync(string username)
        {
            var user = new ApplicationUser
            {
                UserName = username,
                DisplayName = username,
                Email = $"{username}@example.test"
            };
            Assert.True((await Users.CreateAsync(user)).Succeeded);
            Db.Subscriptions.Add(new UserSubscription { UserId = user.Id, User = user });
            await Db.SaveChangesAsync();
            return user;
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await services.DisposeAsync(); Telegram.Dispose(); }
    }

    private sealed class StaticPaymentStore(PaymentOptions value) : IPaymentConfigurationStore
    {
        public Task<PaymentOptions> GetAsync(CancellationToken token = default) => Task.FromResult(value);
        public Task SaveAsync(PaymentOptions options, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class RecordingTelegramFactory : IHttpClientFactory, IDisposable
    {
        private readonly RecordingTelegramHandler handler = new();
        internal IReadOnlyCollection<string> Methods => handler.Methods;
        internal IReadOnlyCollection<(string Method, string Body)> Requests => handler.Requests;
        internal string? FailMethod { set => handler.FailMethod = value; }
        public HttpClient CreateClient(string name) => new(handler, false);
        public void Dispose() => handler.Dispose();
    }

    private sealed class RecordingTelegramHandler : HttpMessageHandler
    {
        internal List<string> Methods { get; } = [];
        internal List<(string Method, string Body)> Requests { get; } = [];
        internal string? FailMethod { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.Segments[^1];
            Methods.Add(method);
            Requests.Add((method, request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (method == FailMethod)
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                { Content = new StringContent("{\"ok\":false,\"error_code\":502,\"description\":\"temporary failure\"}", Encoding.UTF8, "application/json") };
            var result = method == "getMe" ? "{\"id\":42,\"username\":\"ProxyHarborTestBot\"}" : "true";
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"{{\"ok\":true,\"result\":{result}}}", Encoding.UTF8, "application/json") };
        }
    }
}
