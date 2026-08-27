using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Ставит сообщения в постоянную очередь и одновременно ведёт CRM-историю.</summary>
public sealed class TelegramDispatchService(ProxyHarborDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Ставит текст в очередь с идемпотентностью и CRM-аудитом.</summary>
    public async Task<Guid> EnqueueTextAsync(
        TelegramChat chat, string text, string idempotencyKey, string direction = "bot",
        Guid? administratorId = null, object? replyMarkup = null, DateTimeOffset? availableAt = null,
        CancellationToken token = default)
    {
        var existing = await db.TelegramOutboundMessages.AsNoTracking()
            .Where(x => x.IdempotencyKey == idempotencyKey).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        if (existing.HasValue) return existing.Value;
        var payload = new TelegramTextPayload(text, replyMarkup is null
            ? null : JsonSerializer.SerializeToElement(replyMarkup, Json));
        var message = New(chat, TelegramOutboundKinds.Text, payload, idempotencyKey, availableAt);
        db.TelegramOutboundMessages.Add(message);
        db.TelegramConversationMessages.Add(new TelegramConversationMessage
        {
            TelegramChatId = chat.Id,
            Direction = direction,
            Text = Limit(text, 4096),
            AdministratorId = administratorId,
            OutboundMessageId = message.Id
        });
        await db.SaveChangesAsync(token);
        return message.Id;
    }

    /// <summary>Ставит счёт Stars в очередь.</summary>
    public async Task<Guid> EnqueueInvoiceAsync(
        TelegramChat chat, TelegramInvoicePayload payload, string idempotencyKey, CancellationToken token)
    {
        var existing = await db.TelegramOutboundMessages.AsNoTracking()
            .Where(x => x.IdempotencyKey == idempotencyKey).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        if (existing.HasValue) return existing.Value;
        var message = New(chat, TelegramOutboundKinds.Invoice, payload, idempotencyKey, null);
        db.TelegramOutboundMessages.Add(message);
        db.TelegramConversationMessages.Add(new TelegramConversationMessage
        {
            TelegramChatId = chat.Id,
            Direction = "bot",
            Text = $"Счёт: {payload.Title}, {payload.Stars} ⭐",
            OutboundMessageId = message.Id
        });
        await db.SaveChangesAsync(token);
        return message.Id;
    }

    /// <summary>Ставит генерацию свежего proxy-файла в очередь.</summary>
    public async Task<Guid> EnqueueProxyFileAsync(
        TelegramChat chat, int count, string idempotencyKey, CancellationToken token)
    {
        var existing = await db.TelegramOutboundMessages.AsNoTracking()
            .Where(x => x.IdempotencyKey == idempotencyKey).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        if (existing.HasValue) return existing.Value;
        var message = New(chat, TelegramOutboundKinds.ProxyFile,
            new TelegramProxyFilePayload(Math.Clamp(count, 1, 10_000)), idempotencyKey, null);
        db.TelegramOutboundMessages.Add(message);
        db.TelegramConversationMessages.Add(new TelegramConversationMessage
        {
            TelegramChatId = chat.Id,
            Direction = "bot",
            Text = $"Запрошен TXT-файл: до {count:N0} проверенных прокси.",
            OutboundMessageId = message.Id
        });
        await db.SaveChangesAsync(token);
        return message.Id;
    }

    /// <summary>
    /// Потоково ставит broadcast bounded-партиями: объёмная рассылка не выполняет
    /// отдельный SELECT/COMMIT для каждого адресата и не удерживает весь каталог в памяти.
    /// </summary>
    public async Task<int> EnqueueBroadcastAsync(
        string text, Guid batchId, Guid? administratorId, CancellationToken token)
    {
        const int batchSize = 1000;
        var queued = 0;
        Guid? cursor = null;
        while (queued < 100_000)
        {
            var query = db.TelegramChats.AsNoTracking()
                .Where(x => x.NotificationsEnabled && !x.IsBlocked);
            if (cursor.HasValue) query = query.Where(x => x.Id.CompareTo(cursor.Value) > 0);
            var chats = await query.OrderBy(x => x.Id).Take(Math.Min(batchSize, 100_000 - queued)).ToArrayAsync(token);
            if (chats.Length == 0) break;
            foreach (var chat in chats)
            {
                var outbound = New(chat, TelegramOutboundKinds.Text,
                    new TelegramTextPayload(text, null), $"broadcast:{batchId:N}:{chat.Id:N}", null);
                db.TelegramOutboundMessages.Add(outbound);
                db.TelegramConversationMessages.Add(new TelegramConversationMessage
                {
                    TelegramChatId = chat.Id,
                    Direction = "admin",
                    Text = Limit(text, 4096),
                    AdministratorId = administratorId,
                    OutboundMessageId = outbound.Id
                });
            }
            await db.SaveChangesAsync(token);
            queued += chats.Length;
            cursor = chats[^1].Id;
            db.ChangeTracker.Clear();
        }
        return queued;
    }

    private static TelegramOutboundMessage New<T>(
        TelegramChat chat, string kind, T payload, string key, DateTimeOffset? availableAt) => new()
    {
        TelegramChatId = chat.Id,
        Kind = kind,
        PayloadJson = JsonSerializer.Serialize(payload, Json),
        IdempotencyKey = Limit(key, 160),
        AvailableAt = availableAt ?? DateTimeOffset.UtcNow
    };

    internal static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}

internal sealed record TelegramTextPayload(string Text, JsonElement? ReplyMarkup);
internal sealed record TelegramProxyFilePayload(int Count);

/// <summary>Идемпотентно обрабатывает один update независимо от webhook/polling транспорта.</summary>
public sealed class TelegramUpdateProcessor(
    ProxyHarborDbContext db,
    UserManager<ApplicationUser> users,
    ITelegramBotConfigurationStore botConfigurations,
    IPaymentConfigurationStore payments,
    TelegramBotApiClient api,
    TelegramDispatchService queue,
    ILogger<TelegramUpdateProcessor> logger)
{
    private static readonly Action<ILogger, long, Exception?> UpdateFailed = LoggerMessage.Define<long>(
        LogLevel.Warning, new EventId(1701, nameof(UpdateFailed)),
        "Telegram update {UpdateId} не обработан.");

    /// <summary>Обрабатывает update ровно один раз по update_id.</summary>
    public async Task ProcessAsync(JsonElement update, string transport, CancellationToken token)
    {
        if (!update.TryGetProperty("update_id", out var updateIdElement) || !updateIdElement.TryGetInt64(out var updateId))
            throw new InvalidOperationException("Telegram update не содержит корректный update_id.");
        if (await db.TelegramUpdateReceipts.AsNoTracking().AnyAsync(x => x.UpdateId == updateId, token)) return;
        var receipt = new TelegramUpdateReceipt { UpdateId = updateId, Transport = transport };
        db.TelegramUpdateReceipts.Add(receipt);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return;
        }

        try
        {
            var options = await botConfigurations.GetAsync(token);
            if (!options.Ready) throw new InvalidOperationException("Telegram-бот выключен или не настроен.");
            if (update.TryGetProperty("pre_checkout_query", out var preCheckout))
                await HandlePreCheckoutAsync(options, preCheckout, token);
            else if (update.TryGetProperty("callback_query", out var callback))
                await HandleCallbackAsync(options, callback, token);
            else if (update.TryGetProperty("message", out var message))
                await HandleMessageAsync(options, message, token);
            else if (update.TryGetProperty("my_chat_member", out var membership))
                await HandleMembershipAsync(membership, token);
            receipt.ProcessedAt = DateTimeOffset.UtcNow;
            receipt.Error = null;
            await db.SaveChangesAsync(token);
        }
        catch (Exception exception)
        {
            UpdateFailed(logger, updateId, exception);
            db.ChangeTracker.Clear();
            await db.TelegramUpdateReceipts.Where(x => x.UpdateId == updateId).ExecuteDeleteAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task HandleMessageAsync(TelegramBotOptions options, JsonElement message, CancellationToken token)
    {
        if (!message.TryGetProperty("chat", out var chatElement) ||
            chatElement.GetProperty("type").GetString() != "private" ||
            !message.TryGetProperty("from", out var from)) return;
        var messageText = message.TryGetProperty("text", out var rawText)
            ? TelegramDispatchService.Limit(rawText.GetString()?.Trim() ?? string.Empty, 4096)
            : string.Empty;
        var chat = await EnsureChatAsync(chatElement, from, token, StartReferralCode(messageText));
        if (message.TryGetProperty("successful_payment", out var successful))
        {
            await HandleSuccessfulPaymentAsync(chat, successful, token);
            return;
        }
        if (messageText.Length == 0) return;
        var text = messageText;
        if (text.Length == 0) return;
        db.TelegramConversationMessages.Add(new TelegramConversationMessage
        {
            TelegramChatId = chat.Id, Direction = "inbound", Text = text
        });
        await db.SaveChangesAsync(token);
        var command = text.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
        switch (command)
        {
            case "/start": await SendMainMenuAsync(chat, token); break;
            case "/account": await SendAccountAsync(chat, token); break;
            case "/buy": await SendProductsAsync(chat, options, token); break;
            case "/proxies": await RequestProxyFileAsync(chat, options, token); break;
            case "/notifications": await ToggleNotificationsAsync(chat, token); break;
            case "/language": await SendLanguageMenuAsync(chat, token); break;
            case "/support": await ReplyAsync(chat, TelegramLocalization.Get("supportForwarded", Language(chat), ("support", options.SupportText)), $"support:{chat.Id:N}:{Guid.NewGuid():N}", token); break;
            case "/help": await SendHelpAsync(chat, token); break;
            default: await AnswerFaqAsync(chat, text, options, token); break;
        }
    }

    private async Task HandleCallbackAsync(TelegramBotOptions options, JsonElement callback, CancellationToken token)
    {
        var queryId = callback.GetProperty("id").GetString()!;
        if (!callback.TryGetProperty("from", out var from) || !callback.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("chat", out var chatElement))
        {
            await api.AnswerCallbackAsync(options, queryId, "Command expired.", token);
            return;
        }
        var chat = await EnsureChatAsync(chatElement, from, token);
        var data = callback.TryGetProperty("data", out var dataElement) ? dataElement.GetString() ?? string.Empty : string.Empty;
        if (data.StartsWith("buy:", StringComparison.Ordinal))
            await CreateStarsInvoiceAsync(chat, data[4..], options, token);
        else if (data == "account") await SendAccountAsync(chat, token);
        else if (data == "products") await SendProductsAsync(chat, options, token);
        else if (data == "proxies") await RequestProxyFileAsync(chat, options, token);
        else if (data == "notifications") await ToggleNotificationsAsync(chat, token);
        else if (data == "language") await SendLanguageMenuAsync(chat, token);
        else if (data.StartsWith("language:", StringComparison.Ordinal)) await ChangeLanguageAsync(chat, data[9..], token);
        else await SendMainMenuAsync(chat, token);
        await api.AnswerCallbackAsync(options, queryId, null, token);
    }

    private async Task HandlePreCheckoutAsync(
        TelegramBotOptions options, JsonElement query, CancellationToken token)
    {
        var queryId = query.GetProperty("id").GetString()!;
        var valid = false;
        string? error = "Счёт устарел или его параметры изменились.";
        if (query.GetProperty("currency").GetString() == "XTR" &&
            query.TryGetProperty("invoice_payload", out var payload) &&
            Guid.TryParseExact(payload.GetString(), "N", out var orderId) &&
            query.TryGetProperty("from", out var from) &&
            from.TryGetProperty("id", out var fromId))
        {
            var telegramUserId = fromId.GetInt64();
            var amount = query.GetProperty("total_amount").GetInt64();
            valid = await db.PaymentOrders.AsNoTracking().AnyAsync(x =>
                x.Id == orderId && x.Provider == "telegram_stars" && x.Currency == "XTR" &&
                x.AmountMinor == amount && x.Status == PaymentStatuses.Pending &&
                x.User.TelegramChat != null && x.User.TelegramChat.TelegramUserId == telegramUserId, token);
            if (valid) error = null;
        }
        await api.AnswerPreCheckoutAsync(options, queryId, valid, error, token);
    }

    private async Task HandleSuccessfulPaymentAsync(
        TelegramChat chat, JsonElement payment, CancellationToken token)
    {
        if (payment.GetProperty("currency").GetString() != "XTR" ||
            !Guid.TryParseExact(payment.GetProperty("invoice_payload").GetString(), "N", out var orderId))
            throw new InvalidOperationException("Некорректный payload успешной Telegram-оплаты.");
        var chargeId = payment.GetProperty("telegram_payment_charge_id").GetString() ?? string.Empty;
        var amount = payment.GetProperty("total_amount").GetInt64();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var order = await db.PaymentOrders.SingleOrDefaultAsync(x => x.Id == orderId, token)
            ?? throw new InvalidOperationException("Заказ Telegram Stars не найден.");
        if (order.UserId != chat.UserId || order.Provider != "telegram_stars" ||
            order.Currency != "XTR" || order.AmountMinor != amount)
            throw new InvalidOperationException("Параметры Telegram-оплаты не соответствуют заказу.");
        if (order.Status == PaymentStatuses.Paid)
        {
            if (!string.Equals(order.ProviderPaymentId, chargeId, StringComparison.Ordinal))
                throw new InvalidOperationException("Заказ уже оплачен другой Telegram-операцией.");
            await transaction.CommitAsync(token);
            return;
        }
        if (order.Status != PaymentStatuses.Pending)
            throw new InvalidOperationException("Заказ больше не принимает оплату.");
        var paidAt = DateTimeOffset.UtcNow;
        order.ProviderPaymentId = chargeId;
        order.Status = PaymentStatuses.Paid;
        order.PaidAt = paidAt;
        order.UpdatedAt = paidAt;
        var subscription = await db.Subscriptions.SingleAsync(x => x.UserId == chat.UserId, token);
        var begins = subscription.ExpiresAt is { } expires && expires > paidAt ? expires : paidAt;
        subscription.Plan = order.Plan;
        subscription.Status = SubscriptionStatuses.Active;
        subscription.StartedAt = paidAt;
        subscription.ExpiresAt = begins.AddDays(order.DurationDays);
        subscription.ExternalCustomerId ??= $"telegram:{chat.TelegramUserId}";
        subscription.ExternalSubscriptionId = chargeId;
        subscription.UpdatedAt = paidAt;
        var account = await users.FindByIdAsync(chat.UserId.ToString());
        if (account is not null && !await users.IsInRoleAsync(account, UserRoles.Subscriber))
            await users.AddToRoleAsync(account, UserRoles.Subscriber);
        await ReferralRewards.GrantForPurchaseAsync(db, users, order, paidAt, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        await ReplyAsync(chat, TelegramLocalization.Get("paymentConfirmed", Language(chat),
                ("plan", WebUtility.HtmlEncode(order.Plan)), ("expires", $"{subscription.ExpiresAt:yyyy-MM-dd HH:mm} UTC")),
            $"paid:{order.Id:N}", token);
    }

    private async Task CreateStarsInvoiceAsync(
        TelegramChat chat, string productCode, TelegramBotOptions bot, CancellationToken token)
    {
        var catalog = await payments.GetAsync(token);
        productCode = productCode.Trim().ToLowerInvariant();
        if (!catalog.Enabled || !catalog.Products.TryGetValue(productCode, out var product) || !product.Enabled ||
            !TelegramStarsPricing.TryResolve(bot, productCode, product, out var stars))
        {
            await ReplyAsync(chat, TelegramLocalization.Get("productUnavailable", Language(chat)), $"unavailable:{Guid.NewGuid():N}", token);
            return;
        }
        var order = new PaymentOrder
        {
            UserId = chat.UserId,
            ProductCode = productCode,
            Plan = product.Plan,
            Provider = "telegram_stars",
            AmountMinor = stars,
            Currency = "XTR",
            DurationDays = product.DurationDays
        };
        db.PaymentOrders.Add(order);
        await db.SaveChangesAsync(token);
        await queue.EnqueueInvoiceAsync(chat, new TelegramInvoicePayload(
            order.Id, Cut(product.Name, 32), Cut(product.Description, 255), stars),
            $"invoice:{order.Id:N}", token);
    }

    private async Task SendProductsAsync(TelegramChat chat, TelegramBotOptions bot, CancellationToken token)
    {
        var catalog = await payments.GetAsync(token);
        var products = catalog.Products.Where(x => x.Value.Enabled &&
                TelegramStarsPricing.TryResolve(bot, x.Key, x.Value, out _))
            .OrderBy(x => x.Value.DurationDays).ToArray();
        if (!catalog.Enabled || products.Length == 0)
        {
            await ReplyAsync(chat, TelegramLocalization.Get("productsEmpty", Language(chat)), $"products-empty:{Guid.NewGuid():N}", token);
            return;
        }
        var rows = products.Select(x => new[]
        {
            new
            {
                text = $"{x.Value.Name} · {ResolvedStars(bot, x.Key, x.Value)} ⭐",
                callback_data = $"buy:{x.Key}"
            }
        }).ToArray();
        await queue.EnqueueTextAsync(chat,
            TelegramLocalization.Get("products", Language(chat)),
            $"products:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: new { inline_keyboard = rows }, token: token);
    }

    private static int ResolvedStars(TelegramBotOptions options, string code, PaymentProductOptions product)
    {
        _ = TelegramStarsPricing.TryResolve(options, code, product, out var stars);
        return stars;
    }

    private async Task SendAccountAsync(TelegramChat chat, CancellationToken token)
    {
        var subscription = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.UserId == chat.UserId, token);
        var paidOrders = await db.PaymentOrders.AsNoTracking()
            .Where(x => x.UserId == chat.UserId && x.Provider == "telegram_stars" && x.Status == PaymentStatuses.Paid)
            .Select(x => new { x.AmountMinor, x.PaidAt })
            .ToArrayAsync(token);
        var deliveredFiles = await db.TelegramOutboundMessages.AsNoTracking().CountAsync(x =>
            x.TelegramChatId == chat.Id && x.Kind == TelegramOutboundKinds.ProxyFile &&
            x.Status == TelegramOutboundStatuses.Sent, token);
        var active = subscription.Status == SubscriptionStatuses.Active &&
            (subscription.ExpiresAt is null || subscription.ExpiresAt > DateTimeOffset.UtcNow);
        var language = Language(chat);
        var expires = subscription.ExpiresAt is null ? TelegramLocalization.Get("noExpiry", language) :
            subscription.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var paidStars = paidOrders.Sum(x => x.AmountMinor);
        var lastPayment = paidOrders.MaxBy(x => x.PaidAt)?.PaidAt?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "—";
        await queue.EnqueueTextAsync(chat, TelegramLocalization.Get("account", language,
                ("status", TelegramLocalization.Get(active ? "activeSubscription" : "inactiveSubscription", language)),
                ("plan", WebUtility.HtmlEncode(subscription.Plan)), ("expires", expires),
                ("payments", paidOrders.Length), ("stars", paidStars), ("last", lastPayment),
                ("files", deliveredFiles), ("notifications", TelegramLocalization.Get(chat.NotificationsEnabled ? "enabled" : "disabled", language))),
            $"account:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: MainKeyboard(language), token: token);
    }

    private async Task RequestProxyFileAsync(TelegramChat chat, TelegramBotOptions options, CancellationToken token)
    {
        var subscription = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.UserId == chat.UserId, token);
        if (subscription.Status != SubscriptionStatuses.Active ||
            subscription.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            await queue.EnqueueTextAsync(chat, TelegramLocalization.Get("proxyDenied", Language(chat)),
                $"proxy-denied:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: BuyKeyboard(Language(chat)), token: token);
            return;
        }
        await queue.EnqueueProxyFileAsync(chat, options.ProxyFileMaxItems,
            $"proxy-file:{chat.Id:N}:{DateTimeOffset.UtcNow:yyyyMMddHHmmss}:{Guid.NewGuid():N}", token);
        await ReplyAsync(chat, TelegramLocalization.Get("proxyQueued", Language(chat)),
            $"proxy-queued:{Guid.NewGuid():N}", token);
    }

    private async Task ToggleNotificationsAsync(TelegramChat chat, CancellationToken token)
    {
        chat.NotificationsEnabled = !chat.NotificationsEnabled;
        await db.SaveChangesAsync(token);
        await ReplyAsync(chat, TelegramLocalization.Get(chat.NotificationsEnabled ? "notificationsOn" : "notificationsOff", Language(chat)),
            $"notifications:{chat.Id:N}:{Guid.NewGuid():N}", token);
    }

    private async Task SendMainMenuAsync(TelegramChat chat, CancellationToken token) =>
        await queue.EnqueueTextAsync(chat,
            TelegramLocalization.Get("main", Language(chat)),
            $"start:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: MainKeyboard(Language(chat)), token: token);

    private async Task SendHelpAsync(TelegramChat chat, CancellationToken token) =>
        await queue.EnqueueTextAsync(chat,
            TelegramLocalization.Get("help", Language(chat)),
            $"help:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: MainKeyboard(Language(chat)), token: token);

    private async Task AnswerFaqAsync(
        TelegramChat chat, string text, TelegramBotOptions options, CancellationToken token)
    {
        var lower = text.ToLowerInvariant();
        var answer = ContainsAny(lower, "оплат", "звезд", "stars", "payment", "pay", "zahlung", "paiement", "支付", "付款")
            ? TelegramLocalization.Get("faqPayment", Language(chat))
            : ContainsAny(lower, "прокс", "файл", "proxy", "file", "datei", "fichier", "代理", "文件")
                ? TelegramLocalization.Get("faqProxy", Language(chat))
                : ContainsAny(lower, "скорост", "провер", "speed", "latency", "check", "geschwindigkeit", "latenz", "vitesse", "延迟", "速度")
                    ? TelegramLocalization.Get("faqSpeed", Language(chat))
                    : ContainsAny(lower, "подпис", "срок", "subscription", "expiry", "abo", "abonnement", "订阅", "到期")
                        ? TelegramLocalization.Get("faqSubscription", Language(chat))
                        : TelegramLocalization.Get("supportForwarded", Language(chat), ("support", options.SupportText));
        await ReplyAsync(chat, answer, $"faq:{chat.Id:N}:{Guid.NewGuid():N}", token);
    }

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.Ordinal));

    private async Task<TelegramChat> EnsureChatAsync(
        JsonElement chatElement, JsonElement from, CancellationToken token, string? referralCode = null)
    {
        var chatId = chatElement.GetProperty("id").GetInt64();
        var telegramUserId = from.GetProperty("id").GetInt64();
        var chat = await db.TelegramChats.Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TelegramUserId == telegramUserId, token);
        var displayName = string.Join(' ', new[]
        {
            from.TryGetProperty("first_name", out var first) ? first.GetString() : null,
            from.TryGetProperty("last_name", out var last) ? last.GetString() : null
        }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (chat is null)
        {
            var user = new ApplicationUser
            {
                UserName = $"tg.{telegramUserId}",
                // Identity требует уникальный email и отклоняет null даже для аккаунта,
                // созданного Telegram. Зарезервированный домен .invalid исключает
                // случайную доставку писем и остаётся стабильным уникальным ключом.
                Email = $"tg.{telegramUserId}@telegram.proxyharbor.invalid",
                DisplayName = displayName.Length == 0 ? $"Telegram {telegramUserId}" : TelegramDispatchService.Limit(displayName, 120),
                ReferralCode = ReferralCodes.New(),
                PreferredLanguage = SupportedLanguages.Normalize(from.TryGetProperty("language_code", out var initialLanguage) ? initialLanguage.GetString() : null),
                EmailConfirmed = false,
                IsActive = true
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
                throw new InvalidOperationException(string.Join("; ", created.Errors.Select(x => x.Description)));
            var role = await users.AddToRoleAsync(user, UserRoles.User);
            if (!role.Succeeded)
                throw new InvalidOperationException(string.Join("; ", role.Errors.Select(x => x.Description)));
            db.Subscriptions.Add(new UserSubscription { UserId = user.Id });
            chat = new TelegramChat
            {
                ChatId = chatId, TelegramUserId = telegramUserId, UserId = user.Id,
                User = user, DisplayName = user.DisplayName
            };
            db.TelegramChats.Add(chat);
            await ApplyTelegramReferralAsync(user, referralCode, token);
        }
        chat.ChatId = chatId;
        chat.Username = from.TryGetProperty("username", out var username) ? TelegramDispatchService.Limit(username.GetString() ?? string.Empty, 64) : null;
        chat.DisplayName = TelegramDispatchService.Limit(displayName.Length == 0 ? chat.DisplayName : displayName, 160);
        chat.LanguageCode = from.TryGetProperty("language_code", out var language) ? TelegramDispatchService.Limit(language.GetString() ?? string.Empty, 16) : null;
        chat.IsBlocked = false;
        chat.LastInteractionAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        return chat;
    }

    /// <summary>Применяет Telegram deep-link только при создании нового аккаунта.</summary>
    private async Task ApplyTelegramReferralAsync(
        ApplicationUser referred, string? referralCode, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(referralCode)) return;
        var referrer = await db.Users.SingleOrDefaultAsync(x =>
            x.ReferralCode == referralCode && x.IsActive && x.Id != referred.Id, token);
        if (referrer is null || await db.ReferralRelationships.AnyAsync(x => x.ReferredUserId == referred.Id, token)) return;
        var occupiedSlots = await db.ReferralRelationships.Where(x => x.ReferrerUserId == referrer.Id)
            .Select(x => x.Slot).ToArrayAsync(token);
        var slot = Enumerable.Range(1, ReferralRewards.MaximumReferralsPerUser)
            .FirstOrDefault(candidate => !occupiedSlots.Contains(candidate));
        if (slot == 0) return;
        var relationship = new ReferralRelationship
        {
            ReferrerUserId = referrer.Id,
            ReferredUserId = referred.Id,
            Slot = slot
        };
        db.ReferralRelationships.Add(relationship);
        db.ReferralRewards.Add(new ReferralReward
        {
            ReferralRelationship = relationship,
            RewardKey = $"signup:{referred.Id:N}",
            Kind = ReferralRewardKinds.Signup,
            DaysGranted = 1
        });
        await db.SaveChangesAsync(token);
        await ReferralRewards.ExtendSubscriptionAsync(
            db, users, referrer.Id, 1, DateTimeOffset.UtcNow, token);
    }

    private static string? StartReferralCode(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !parts[0].Split('@', 2)[0].Equals("/start", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].StartsWith("ref_", StringComparison.OrdinalIgnoreCase)) return null;
        var code = parts[1][4..].ToLowerInvariant();
        return code.Length == 12 && code.All(character => char.IsAsciiHexDigit(character)) ? code : null;
    }

    private async Task HandleMembershipAsync(JsonElement membership, CancellationToken token)
    {
        if (!membership.TryGetProperty("chat", out var chatElement) ||
            !chatElement.TryGetProperty("id", out var chatIdElement)) return;
        var chatId = chatIdElement.GetInt64();
        var status = membership.GetProperty("new_chat_member").GetProperty("status").GetString();
        var chat = await db.TelegramChats.SingleOrDefaultAsync(x => x.ChatId == chatId, token);
        if (chat is null) return;
        chat.IsBlocked = status is "kicked" or "left";
        chat.LastInteractionAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
    }

    private Task<Guid> ReplyAsync(TelegramChat chat, string text, string key, CancellationToken token) =>
        queue.EnqueueTextAsync(chat, text, key, token: token);

    private async Task SendLanguageMenuAsync(TelegramChat chat, CancellationToken token) =>
        await queue.EnqueueTextAsync(chat, TelegramLocalization.Get("chooseLanguage", Language(chat)),
            $"language-menu:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: LanguageKeyboard(), token: token);

    private async Task ChangeLanguageAsync(TelegramChat chat, string language, CancellationToken token)
    {
        if (!SupportedLanguages.IsSupported(language))
        {
            await SendLanguageMenuAsync(chat, token);
            return;
        }
        chat.User.PreferredLanguage = SupportedLanguages.Normalize(language);
        await db.SaveChangesAsync(token);
        await queue.EnqueueTextAsync(chat, TelegramLocalization.Get("languageSaved", chat.User.PreferredLanguage),
            $"language-saved:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: MainKeyboard(chat.User.PreferredLanguage), token: token);
    }

    private static string Language(TelegramChat chat) => SupportedLanguages.Normalize(chat.User.PreferredLanguage);

    private static object MainKeyboard(string language) => new
    {
        inline_keyboard = new object[]
        {
            new[] { new { text = TelegramLocalization.Get("accountButton", language), callback_data = "account" }, new { text = TelegramLocalization.Get("buyButton", language), callback_data = "products" } },
            new[] { new { text = TelegramLocalization.Get("proxyButton", language), callback_data = "proxies" } },
            new[] { new { text = TelegramLocalization.Get("notificationsButton", language), callback_data = "notifications" }, new { text = TelegramLocalization.Get("languageButton", language), callback_data = "language" } }
        }
    };

    private static object BuyKeyboard(string language) => new
    {
        inline_keyboard = new[] { new[] { new { text = TelegramLocalization.Get("choosePlanButton", language), callback_data = "products" } } }
    };

    private static object LanguageKeyboard() => new
    {
        inline_keyboard = new[]
        {
            new[] { new { text = "Русский", callback_data = "language:ru" }, new { text = "English", callback_data = "language:en" } },
            new[] { new { text = "Deutsch", callback_data = "language:de" }, new { text = "Français", callback_data = "language:fr" } },
            new[] { new { text = "简体中文", callback_data = "language:zh" } }
        }
    };

    private static string Cut(string value, int maximum)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? "Подписка ProxyHarbor" : value.Trim();
        return clean.Length <= maximum ? clean : clean[..maximum];
    }
}

/// <summary>Доставляет persistent queue с free-tier лимитами Telegram и retry_after.</summary>
public sealed class TelegramOutboundWorker(
    IServiceScopeFactory scopes,
    ILogger<TelegramOutboundWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, Exception?> IterationFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(1702, nameof(IterationFailed)),
        "Telegram outbound worker завершил итерацию с ошибкой.");
    private readonly Dictionary<long, DateTimeOffset> lastPerChat = [];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOneAsync(stoppingToken);
                await Task.Delay(processed ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                IterationFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
        var settings = await scope.ServiceProvider.GetRequiredService<ITelegramBotConfigurationStore>().GetAsync(token);
        if (!settings.Ready) return false;
        await db.TelegramOutboundMessages.Where(x => x.Status == TelegramOutboundStatuses.Processing && x.LeaseUntil < DateTimeOffset.UtcNow)
            .ExecuteUpdateAsync(x => x.SetProperty(m => m.Status, TelegramOutboundStatuses.Pending)
                .SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), token);
        // PostgreSQL retry is enabled globally. A user transaction must therefore run
        // inside the provider execution strategy; otherwise the worker fails before it
        // can claim even the first queue item on production.
        var strategy = db.Database.CreateExecutionStrategy();
        var message = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            var claimed = await db.TelegramOutboundMessages
                .FromSqlRaw("""
                    SELECT * FROM "TelegramOutboundMessages"
                    WHERE "Status" = 'pending' AND "AvailableAt" <= now()
                    ORDER BY "AvailableAt", "CreatedAt"
                    FOR UPDATE SKIP LOCKED LIMIT 1
                    """)
                .Include(x => x.TelegramChat).SingleOrDefaultAsync(token);
            if (claimed is not null)
            {
                claimed.Status = TelegramOutboundStatuses.Processing;
                claimed.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2);
                claimed.Attempts++;
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return claimed;
        });
        if (message is null) return false;
        if (message.TelegramChat.IsBlocked)
        {
            message.Status = TelegramOutboundStatuses.Canceled;
            message.LastError = "Чат отключён.";
            message.LeaseUntil = null;
            await db.SaveChangesAsync(token);
            return true;
        }

        if (lastPerChat.TryGetValue(message.TelegramChat.ChatId, out var last))
        {
            var remaining = last.AddSeconds(1) - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
        }
        try
        {
            var api = scope.ServiceProvider.GetRequiredService<TelegramBotApiClient>();
            var collector = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value;
            message.TelegramMessageId = message.Kind switch
            {
                TelegramOutboundKinds.Text => await SendTextAsync(api, settings, message, token),
                TelegramOutboundKinds.Invoice => await SendInvoiceAsync(api, settings, message, token),
                TelegramOutboundKinds.ProxyFile => await SendProxyFileAsync(
                    api, settings, db, message,
                    DateTimeOffset.UtcNow.AddMinutes(-collector.PublicFreshnessMinutes), token),
                _ => throw new InvalidOperationException("Неизвестный вид Telegram-задания.")
            };
            lastPerChat[message.TelegramChat.ChatId] = DateTimeOffset.UtcNow;
            message.Status = TelegramOutboundStatuses.Sent;
            message.SentAt = DateTimeOffset.UtcNow;
            message.LeaseUntil = null;
            message.LastError = null;
        }
        catch (TelegramBotApiException exception) when (exception.Forbidden)
        {
            message.TelegramChat.IsBlocked = true;
            message.Status = TelegramOutboundStatuses.Canceled;
            message.LeaseUntil = null;
            message.LastError = TelegramDispatchService.Limit(exception.Message, 1000);
        }
        catch (Exception exception) when (exception is TelegramBotApiException { Transient: true } ||
                                          exception is HttpRequestException or TaskCanceledException)
        {
            var retry = exception is TelegramBotApiException apiError && apiError.RetryAfterSeconds.HasValue
                ? TimeSpan.FromSeconds(Math.Clamp(apiError.RetryAfterSeconds.Value, 1, 3600))
                : TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(message.Attempts, 8))));
            message.Status = message.Attempts >= 10 ? TelegramOutboundStatuses.Failed : TelegramOutboundStatuses.Pending;
            message.AvailableAt = DateTimeOffset.UtcNow.Add(retry);
            message.LeaseUntil = null;
            message.LastError = TelegramDispatchService.Limit(exception.Message, 1000);
        }
        catch (Exception exception)
        {
            message.Status = TelegramOutboundStatuses.Failed;
            message.LeaseUntil = null;
            message.LastError = TelegramDispatchService.Limit(exception.Message, 1000);
        }
        await db.SaveChangesAsync(token);
        return true;
    }

    private static async Task<long> SendTextAsync(
        TelegramBotApiClient api, TelegramBotOptions settings, TelegramOutboundMessage message, CancellationToken token)
    {
        var payload = JsonSerializer.Deserialize<TelegramTextPayload>(message.PayloadJson, Json)
            ?? throw new InvalidOperationException("Пустой text payload.");
        return await api.SendMessageAsync(settings, message.TelegramChat.ChatId,
            payload.Text, payload.ReplyMarkup, token);
    }

    private static async Task<long> SendInvoiceAsync(
        TelegramBotApiClient api, TelegramBotOptions settings, TelegramOutboundMessage message, CancellationToken token)
    {
        var payload = JsonSerializer.Deserialize<TelegramInvoicePayload>(message.PayloadJson, Json)
            ?? throw new InvalidOperationException("Пустой invoice payload.");
        return await api.SendStarsInvoiceAsync(settings, message.TelegramChat.ChatId, payload, token);
    }

    private static async Task<long> SendProxyFileAsync(
        TelegramBotApiClient api, TelegramBotOptions settings, ProxyHarborDbContext db,
        TelegramOutboundMessage message, DateTimeOffset freshAfter, CancellationToken token)
    {
        var payload = JsonSerializer.Deserialize<TelegramProxyFilePayload>(message.PayloadJson, Json)
            ?? throw new InvalidOperationException("Пустой proxy_file payload.");
        var file = await BuildProxyFileAsync(
            db, Math.Min(payload.Count, settings.ProxyFileMaxItems), freshAfter, token);
        return await api.SendDocumentAsync(settings, message.TelegramChat.ChatId,
            $"proxyharbor-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.txt", file.Content,
            $"{file.Count:N0} проверенных прокси · сформировано {DateTimeOffset.UtcNow:dd.MM.yyyy HH:mm} UTC", token);
    }

    /// <summary>
    /// Формирует выгрузку по тому же окну свежести, что публичный API: одного исторического
    /// статуса Alive недостаточно для выдачи оплачивающему клиенту.
    /// </summary>
    internal static async Task<(byte[] Content, int Count)> BuildProxyFileAsync(
        ProxyHarborDbContext db, int maximum, DateTimeOffset freshAfter, CancellationToken token)
    {
        var rows = await db.Proxies.AsNoTracking()
            .Where(x => x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter)
            .OrderBy(x => x.LatencyMs).ThenByDescending(x => x.SuccessfulChecks).ThenBy(x => x.Id)
            .Take(Math.Clamp(maximum, 1, 10_000))
            .Select(x => new { x.Host, x.Port, x.Protocol }).ToArrayAsync(token);
        var builder = new StringBuilder(rows.Length * 32);
        foreach (var row in rows)
        {
            var host = row.Host.Contains(':') ? $"[{row.Host}]" : row.Host;
            builder.Append(row.Protocol.ToString().ToLowerInvariant()).Append("://")
                .Append(host).Append(':').Append(row.Port).Append('\n');
        }
        return (Encoding.UTF8.GetBytes(builder.ToString()), rows.Length);
    }
}

/// <summary>Long polling transport; автоматически бездействует в webhook-режиме.</summary>
public sealed class TelegramPollingWorker(
    IServiceScopeFactory scopes,
    ILogger<TelegramPollingWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> PollingFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1703, nameof(PollingFailed)),
        "Telegram polling временно недоступен.");
    private long offset;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var settings = await scope.ServiceProvider.GetRequiredService<ITelegramBotConfigurationStore>().GetAsync(stoppingToken);
                if (!settings.Ready || settings.UpdateMode != TelegramUpdateModes.Polling)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                var api = scope.ServiceProvider.GetRequiredService<TelegramBotApiClient>();
                var updates = await api.GetUpdatesAsync(settings, offset, stoppingToken);
                foreach (var update in updates)
                {
                    await scope.ServiceProvider.GetRequiredService<TelegramUpdateProcessor>()
                        .ProcessAsync(update, TelegramUpdateModes.Polling, stoppingToken);
                    offset = Math.Max(offset, update.GetProperty("update_id").GetInt64() + 1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                PollingFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

/// <summary>Напоминания о продлении и единая обработка истёкших подписок.</summary>
public sealed class TelegramSubscriptionReminderWorker(
    IServiceScopeFactory scopes,
    ILogger<TelegramSubscriptionReminderWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ReminderFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(1704, nameof(ReminderFailed)),
        "Telegram reminders завершили цикл с ошибкой.");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { ReminderFailed(logger, exception); }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<ITelegramBotConfigurationStore>().GetAsync(token);
        if (!settings.Ready) return;
        var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var queue = scope.ServiceProvider.GetRequiredService<TelegramDispatchService>();
        var now = DateTimeOffset.UtcNow;
        var expired = await db.Subscriptions.Include(x => x.User).ThenInclude(x => x.TelegramChat)
            .Where(x => x.Status == SubscriptionStatuses.Active && x.ExpiresAt != null && x.ExpiresAt <= now)
            .ToArrayAsync(token);
        foreach (var subscription in expired)
        {
            subscription.Status = SubscriptionStatuses.Expired;
            subscription.UpdatedAt = now;
            if (await users.IsInRoleAsync(subscription.User, UserRoles.Subscriber))
                await users.RemoveFromRoleAsync(subscription.User, UserRoles.Subscriber);
            if (subscription.User.TelegramChat is { NotificationsEnabled: true, IsBlocked: false } chat)
                await queue.EnqueueTextAsync(chat,
                    "⏰ Подписка закончилась. Продлить доступ и снова получить файл можно через /buy.",
                    $"subscription-expired:{subscription.Id:N}:{subscription.ExpiresAt:yyyyMMddHHmm}", token: token);
        }
        await db.SaveChangesAsync(token);

        var upcoming = await db.Subscriptions.AsNoTracking().Include(x => x.User).ThenInclude(x => x.TelegramChat)
            .Where(x => x.Status == SubscriptionStatuses.Active && x.ExpiresAt > now && x.ExpiresAt <= now.AddDays(7) &&
                        x.User.TelegramChat != null && x.User.TelegramChat.NotificationsEnabled && !x.User.TelegramChat.IsBlocked)
            .ToArrayAsync(token);
        foreach (var subscription in upcoming)
        {
            var remaining = subscription.ExpiresAt!.Value - now;
            var window = remaining <= TimeSpan.FromDays(1) ? "1d" : remaining <= TimeSpan.FromDays(3) ? "3d" : "7d";
            var chat = subscription.User.TelegramChat!;
            await queue.EnqueueTextAsync(chat,
                $"⏳ Подписка закончится <b>{subscription.ExpiresAt:dd.MM.yyyy HH:mm} UTC</b>. Продлить можно заранее через /buy — новый срок прибавится к текущему.",
                $"subscription-reminder:{subscription.Id:N}:{subscription.ExpiresAt:yyyyMMddHHmm}:{window}",
                replyMarkup: new { inline_keyboard = new[] { new[] { new { text = "⭐ Продлить", callback_data = "products" } } } }, token: token);
        }

        await db.TelegramUpdateReceipts.Where(x => x.ReceivedAt < now.AddDays(-30)).ExecuteDeleteAsync(token);
        await db.TelegramOutboundMessages.Where(x => x.CreatedAt < now.AddDays(-90) &&
            (x.Status == TelegramOutboundStatuses.Sent || x.Status == TelegramOutboundStatuses.Canceled || x.Status == TelegramOutboundStatuses.Failed))
            .ExecuteDeleteAsync(token);
    }
}
