using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>
/// Связывает резервное копирование с уже настроенным commerce-ботом и его CRM.
/// Singleton создаёт короткий scope на каждую отправку, поэтому всегда видит
/// актуальный token после его замены в админке.
/// </summary>
public sealed class TelegramBackupDeliveryResolver(IServiceScopeFactory scopeFactory)
    : ITelegramBackupDeliveryResolver
{
    /// <inheritdoc />
    public async Task<TelegramBackupDelivery> ResolveAsync(
        Guid recipientId,
        CancellationToken token = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var configurations = scope.ServiceProvider
            .GetRequiredService<ITelegramBotConfigurationStore>();
        var bot = await configurations.GetAsync(token);
        if (!bot.Enabled || !bot.BotId.HasValue || !TelegramTokenPolicy.IsValid(bot.BotToken))
            throw new InvalidOperationException(
                "Основной Telegram-бот не включён или его token ещё не настроен.");

        var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
        var recipient = await db.TelegramChats.AsNoTracking()
            .Where(chat => chat.Id == recipientId)
            .Select(chat => new
            {
                chat.Id,
                chat.ChatId,
                chat.DisplayName,
                chat.Username,
                chat.IsBlocked
            })
            .SingleOrDefaultAsync(token);
        if (recipient is null)
            throw new InvalidOperationException("Выбранный Telegram-диалог больше не существует.");
        if (recipient.IsBlocked)
            throw new InvalidOperationException("Выбранный Telegram-диалог заблокирован.");

        return new TelegramBackupDelivery(
            recipient.Id,
            bot.BotToken,
            recipient.ChatId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            recipient.DisplayName,
            recipient.Username);
    }
}
