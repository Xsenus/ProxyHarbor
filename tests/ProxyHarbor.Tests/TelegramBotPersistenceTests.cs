using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class TelegramBotPersistenceTests
{
    [Fact]
    public async Task ConfigurationEncryptsTokenAndUsesTrustedDeployOrigin()
    {
        await using var db = Database();
        var store = new TelegramBotConfigurationStore(db,
            Options.Create(new TelegramBotHostOptions { PublicBaseUrl = "https://proxy.example.test" }),
            new EphemeralDataProtectionProvider());
        await store.SaveAsync(new TelegramBotOptions
        {
            Enabled = true, PublicBaseUrl = "https://attacker.example", BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN",
            WebhookSecret = "safe_webhook_secret", BotId = 42, BotUsername = "ProxyHarborBot",
            ProductStars = new Dictionary<string, int> { ["pro-30"] = 250 }
        });

        var persisted = await db.TelegramBotConfigurations.SingleAsync();
        Assert.DoesNotContain("TEST_ONLY", persisted.ProtectedSecrets, StringComparison.Ordinal);
        var restored = await store.GetAsync();
        Assert.Equal("123:TEST_ONLY_NOT_A_REAL_TOKEN", restored.BotToken);
        Assert.Equal("https://proxy.example.test/api/v1/telegram/webhook/proxyharborbot", restored.WebhookUrl);
        Assert.Equal(250, restored.ProductStars["pro-30"]);
    }

    [Fact]
    public async Task BroadcastQueuesOnlySubscribedUnblockedChatsWithCrmAudit()
    {
        await using var db = Database();
        var allowed = Chat(100, true, false);
        db.TelegramChats.AddRange(allowed, Chat(200, false, false), Chat(300, true, true));
        await db.SaveChangesAsync();

        var count = await new TelegramDispatchService(db).EnqueueBroadcastAsync(
            "Плановые работы", Guid.Parse("11111111-1111-1111-1111-111111111111"), null, CancellationToken.None);

        Assert.Equal(1, count);
        var outbound = await db.TelegramOutboundMessages.SingleAsync();
        Assert.Equal(allowed.Id, outbound.TelegramChatId);
        Assert.Contains(allowed.Id.ToString("N"), outbound.IdempotencyKey, StringComparison.Ordinal);
        var audit = await db.TelegramConversationMessages.SingleAsync();
        Assert.Equal("admin", audit.Direction);
        Assert.Equal("Плановые работы", audit.Text);
    }

    private static ProxyHarborDbContext Database() => new(new DbContextOptionsBuilder<ProxyHarborDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static TelegramChat Chat(long id, bool notifications, bool blocked) => new()
    {
        ChatId = id, TelegramUserId = id, UserId = Guid.NewGuid(), DisplayName = $"User {id}",
        NotificationsEnabled = notifications, IsBlocked = blocked
    };
}
