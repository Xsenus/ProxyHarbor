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
        var chat = await EnsureChatAsync(chatElement, from, token);
        if (message.TryGetProperty("successful_payment", out var successful))
        {
            await HandleSuccessfulPaymentAsync(chat, successful, token);
            return;
        }
        if (!message.TryGetProperty("text", out var textElement)) return;
        var text = TelegramDispatchService.Limit(textElement.GetString()?.Trim() ?? string.Empty, 4096);
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
            case "/support": await ReplyAsync(chat, options.SupportText, $"support:{chat.Id:N}:{Guid.NewGuid():N}", token); break;
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
            await api.AnswerCallbackAsync(options.BotToken, queryId, "Команда устарела.", token);
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
        else await SendMainMenuAsync(chat, token);
        await api.AnswerCallbackAsync(options.BotToken, queryId, null, token);
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
        await api.AnswerPreCheckoutAsync(options.BotToken, queryId, valid, error, token);
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
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        await ReplyAsync(chat,
            $"✅ Оплата подтверждена. Тариф <b>{WebUtility.HtmlEncode(order.Plan)}</b> действует до " +
            $"<b>{subscription.ExpiresAt:dd.MM.yyyy HH:mm} UTC</b>. Файл доступен через /proxies.",
            $"paid:{order.Id:N}", token);
    }

    private async Task CreateStarsInvoiceAsync(
        TelegramChat chat, string productCode, TelegramBotOptions bot, CancellationToken token)
    {
        var catalog = await payments.GetAsync(token);
        productCode = productCode.Trim().ToLowerInvariant();
        if (!catalog.Enabled || !catalog.Products.TryGetValue(productCode, out var product) || !product.Enabled ||
            !bot.ProductStars.TryGetValue(productCode, out var stars) || stars is < 1 or > 1_000_000)
        {
            await ReplyAsync(chat, "Этот тариф сейчас недоступен для оплаты в Telegram.", $"unavailable:{Guid.NewGuid():N}", token);
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
        var products = catalog.Products.Where(x => x.Value.Enabled && bot.ProductStars.TryGetValue(x.Key, out var stars) && stars > 0)
            .OrderBy(x => x.Value.DurationDays).ToArray();
        if (!catalog.Enabled || products.Length == 0)
        {
            await ReplyAsync(chat, "Продажи через Telegram временно приостановлены.", $"products-empty:{Guid.NewGuid():N}", token);
            return;
        }
        var rows = products.Select(x => new[]
        {
            new { text = $"{x.Value.Name} · {bot.ProductStars[x.Key]} ⭐", callback_data = $"buy:{x.Key}" }
        }).ToArray();
        await queue.EnqueueTextAsync(chat,
            "<b>Выберите подписку</b>\nЦена окончательная и списывается в Telegram Stars только после подтверждения.",
            $"products:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: new { inline_keyboard = rows }, token: token);
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
        var expires = subscription.ExpiresAt is null ? "без срока" :
            subscription.ExpiresAt.Value.ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var paidStars = paidOrders.Sum(x => x.AmountMinor);
        var lastPayment = paidOrders.MaxBy(x => x.PaidAt)?.PaidAt?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "—";
        await queue.EnqueueTextAsync(chat,
            $"<b>Личный кабинет</b>\nСтатус: {(active ? "✅ активна" : "⛔ нет активной подписки")}\n" +
            $"Тариф: <b>{WebUtility.HtmlEncode(subscription.Plan)}</b>\nДействует до: <b>{expires}</b>\n" +
            $"\n<b>Статистика</b>\nОплат через Stars: <b>{paidOrders.Length}</b> · всего <b>{paidStars} ⭐</b>\n" +
            $"Последняя оплата: <b>{lastPayment}</b>\nПолучено файлов: <b>{deliveredFiles}</b>\n" +
            $"Уведомления: {(chat.NotificationsEnabled ? "включены" : "выключены")}",
            $"account:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: MainKeyboard(), token: token);
    }

    private async Task RequestProxyFileAsync(TelegramChat chat, TelegramBotOptions options, CancellationToken token)
    {
        var subscription = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.UserId == chat.UserId, token);
        if (subscription.Status != SubscriptionStatuses.Active ||
            subscription.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            await queue.EnqueueTextAsync(chat, "Для выгрузки нужна активная подписка. Выберите тариф через /buy.",
                $"proxy-denied:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: BuyKeyboard(), token: token);
            return;
        }
        await queue.EnqueueProxyFileAsync(chat, options.ProxyFileMaxItems,
            $"proxy-file:{chat.Id:N}:{DateTimeOffset.UtcNow:yyyyMMddHHmmss}:{Guid.NewGuid():N}", token);
        await ReplyAsync(chat, "Файл поставлен в очередь и придёт отдельным сообщением.",
            $"proxy-queued:{Guid.NewGuid():N}", token);
    }

    private async Task ToggleNotificationsAsync(TelegramChat chat, CancellationToken token)
    {
        chat.NotificationsEnabled = !chat.NotificationsEnabled;
        await db.SaveChangesAsync(token);
        await ReplyAsync(chat, chat.NotificationsEnabled
            ? "🔔 Уведомления о подписке и важных изменениях включены."
            : "🔕 Информационные уведомления отключены. Чеки и ответы поддержки продолжат приходить.",
            $"notifications:{chat.Id:N}:{Guid.NewGuid():N}", token);
    }

    private async Task SendMainMenuAsync(TelegramChat chat, CancellationToken token) =>
        await queue.EnqueueTextAsync(chat,
            "<b>ProxyHarbor</b>\nПроверенные публичные прокси, покупка подписки и выгрузка файлов прямо в Telegram.",
            $"start:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: MainKeyboard(), token: token);

    private async Task SendHelpAsync(TelegramChat chat, CancellationToken token) =>
        await queue.EnqueueTextAsync(chat,
            "<b>Помощь</b>\n/account — подписка\n/buy — оплата Stars\n/proxies — TXT-файл\n" +
            "/notifications — включить или отключить напоминания\n/support — связь с оператором\n\n" +
            "Прокси регулярно перепроверяются; файл формируется только из адресов, которые сейчас имеют статус Alive.",
            $"help:{chat.Id:N}:{Guid.NewGuid():N}", replyMarkup: MainKeyboard(), token: token);

    private async Task AnswerFaqAsync(
        TelegramChat chat, string text, TelegramBotOptions options, CancellationToken token)
    {
        var lower = text.ToLowerInvariant();
        var answer = lower.Contains("оплат", StringComparison.Ordinal) || lower.Contains("звезд", StringComparison.Ordinal) || lower.Contains("stars", StringComparison.Ordinal)
            ? "Оплата выполняется встроенным счётом Telegram Stars. Нажмите /buy, выберите тариф и подтвердите списание."
            : lower.Contains("прокс", StringComparison.Ordinal) || lower.Contains("файл", StringComparison.Ordinal)
                ? "При активной подписке команда /proxies создаёт свежий TXT-файл с проверенными HTTP, HTTPS, SOCKS4 и SOCKS5 адресами."
                : lower.Contains("скорост", StringComparison.Ordinal) || lower.Contains("провер", StringComparison.Ordinal)
                    ? "ProxyHarbor регулярно перепроверяет доступность и задержку. В файл попадают только прокси с подтверждённым статусом Alive."
                    : lower.Contains("подпис", StringComparison.Ordinal) || lower.Contains("срок", StringComparison.Ordinal)
                        ? "Срок и тариф показаны в /account. Перед окончанием бот напомнит о продлении, если уведомления включены."
                        : $"Я передал сообщение в CRM. {options.SupportText}";
        await ReplyAsync(chat, answer, $"faq:{chat.Id:N}:{Guid.NewGuid():N}", token);
    }

    private async Task<TelegramChat> EnsureChatAsync(
        JsonElement chatElement, JsonElement from, CancellationToken token)
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
                DisplayName = displayName.Length == 0 ? $"Telegram {telegramUserId}" : TelegramDispatchService.Limit(displayName, 120),
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

    private static object MainKeyboard() => new
    {
        inline_keyboard = new object[]
        {
            new[] { new { text = "👤 Личный кабинет", callback_data = "account" }, new { text = "⭐ Купить", callback_data = "products" } },
            new[] { new { text = "📄 Получить прокси", callback_data = "proxies" } },
            new[] { new { text = "🔔 Уведомления", callback_data = "notifications" } }
        }
    };

    private static object BuyKeyboard() => new
    {
        inline_keyboard = new[] { new[] { new { text = "⭐ Выбрать тариф", callback_data = "products" } } }
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
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var message = await db.TelegramOutboundMessages
            .FromSqlRaw("""
                SELECT * FROM "TelegramOutboundMessages"
                WHERE "Status" = 'pending' AND "AvailableAt" <= now()
                ORDER BY "AvailableAt", "CreatedAt"
                FOR UPDATE SKIP LOCKED LIMIT 1
                """)
            .Include(x => x.TelegramChat).SingleOrDefaultAsync(token);
        if (message is null)
        {
            await transaction.CommitAsync(token);
            return false;
        }
        message.Status = TelegramOutboundStatuses.Processing;
        message.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2);
        message.Attempts++;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
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
            message.TelegramMessageId = message.Kind switch
            {
                TelegramOutboundKinds.Text => await SendTextAsync(api, settings, message, token),
                TelegramOutboundKinds.Invoice => await SendInvoiceAsync(api, settings, message, token),
                TelegramOutboundKinds.ProxyFile => await SendProxyFileAsync(api, settings, db, message, token),
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
        return await api.SendMessageAsync(settings.BotToken, message.TelegramChat.ChatId,
            payload.Text, payload.ReplyMarkup, token);
    }

    private static async Task<long> SendInvoiceAsync(
        TelegramBotApiClient api, TelegramBotOptions settings, TelegramOutboundMessage message, CancellationToken token)
    {
        var payload = JsonSerializer.Deserialize<TelegramInvoicePayload>(message.PayloadJson, Json)
            ?? throw new InvalidOperationException("Пустой invoice payload.");
        return await api.SendStarsInvoiceAsync(settings.BotToken, message.TelegramChat.ChatId, payload, token);
    }

    private static async Task<long> SendProxyFileAsync(
        TelegramBotApiClient api, TelegramBotOptions settings, ProxyHarborDbContext db,
        TelegramOutboundMessage message, CancellationToken token)
    {
        var payload = JsonSerializer.Deserialize<TelegramProxyFilePayload>(message.PayloadJson, Json)
            ?? throw new InvalidOperationException("Пустой proxy_file payload.");
        var rows = await db.Proxies.AsNoTracking().Where(x => x.Status == ProxyStatus.Alive)
            .OrderBy(x => x.LatencyMs).ThenByDescending(x => x.SuccessfulChecks).ThenBy(x => x.Id)
            .Take(Math.Min(payload.Count, settings.ProxyFileMaxItems))
            .Select(x => new { x.Host, x.Port, x.Protocol }).ToArrayAsync(token);
        var builder = new StringBuilder(rows.Length * 32);
        foreach (var row in rows)
        {
            var host = row.Host.Contains(':') ? $"[{row.Host}]" : row.Host;
            builder.Append(row.Protocol.ToString().ToLowerInvariant()).Append("://")
                .Append(host).Append(':').Append(row.Port).Append('\n');
        }
        var content = Encoding.UTF8.GetBytes(builder.ToString());
        return await api.SendDocumentAsync(settings.BotToken, message.TelegramChat.ChatId,
            $"proxyharbor-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.txt", content,
            $"{rows.Length:N0} проверенных прокси · сформировано {DateTimeOffset.UtcNow:dd.MM.yyyy HH:mm} UTC", token);
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
