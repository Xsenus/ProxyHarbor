using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
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
            ProductStars = new Dictionary<string, int> { ["pro-30"] = 250 },
            AutomaticProductCodes = new(StringComparer.OrdinalIgnoreCase) { "unlimited-30" },
            StarsPerCurrencyUnit = 1.25m,
            StarsRoundingStep = 10,
            TransportMode = TelegramTransportModes.Proxy,
            Proxies =
            [
                new TelegramProxyOptions
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Host = "proxy.example.test", Port = 1080,
                    Username = "telegram-user", Password = "TEST_ONLY_PROXY_PASSWORD"
                }
            ]
        });

        var persisted = await db.TelegramBotConfigurations.SingleAsync();
        Assert.DoesNotContain("TEST_ONLY", persisted.ProtectedSecrets, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy.example.test", persisted.ProtectedSecrets, StringComparison.Ordinal);
        var restored = await store.GetAsync();
        Assert.Equal("123:TEST_ONLY_NOT_A_REAL_TOKEN", restored.BotToken);
        Assert.Equal("https://proxy.example.test/api/v1/telegram/webhook/proxyharborbot", restored.WebhookUrl);
        Assert.Equal(250, restored.ProductStars["pro-30"]);
        Assert.Contains("UNLIMITED-30", restored.AutomaticProductCodes);
        Assert.Equal(1.25m, restored.StarsPerCurrencyUnit);
        Assert.Equal(10, restored.StarsRoundingStep);
        Assert.Equal(TelegramTransportModes.Proxy, restored.TransportMode);
        var proxy = Assert.Single(restored.Proxies);
        Assert.Equal("proxy.example.test", proxy.Host);
        Assert.Equal("telegram-user", proxy.Username);
        Assert.Equal("TEST_ONLY_PROXY_PASSWORD", proxy.Password);
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

    [Fact]
    public async Task DirectQueueIsIdempotentAndHonorsScheduledAvailability()
    {
        await using var db = Database();
        var chat = Chat(400, true, false);
        db.TelegramChats.Add(chat);
        await db.SaveChangesAsync();
        var dispatch = new TelegramDispatchService(db);
        var available = DateTimeOffset.UtcNow.AddMinutes(5);
        var first = await dispatch.EnqueueTextAsync(chat, "Позже", "same-key", availableAt: available);
        var second = await dispatch.EnqueueTextAsync(chat, "Дубликат", "same-key");
        Assert.Equal(first, second);
        Assert.Equal(available, (await db.TelegramOutboundMessages.SingleAsync()).AvailableAt);
        Assert.Single(db.TelegramConversationMessages);
    }

    [Fact]
    public async Task ProxyFileContainsOnlyFreshAliveEndpointsInPublicOrder()
    {
        await using var db = Database();
        var now = DateTimeOffset.UtcNow;
        db.Proxies.AddRange(
            Endpoint("192.0.2.10", 8080, ProxyStatus.Alive, now.AddMinutes(-2), 40, 3),
            Endpoint("2001:db8::10", 1080, ProxyStatus.Alive, now.AddMinutes(-1), 20, 2, ProxyProtocol.Socks5),
            Endpoint("192.0.2.11", 3128, ProxyStatus.Alive, now.AddMinutes(-40), 10, 20),
            Endpoint("192.0.2.12", 8888, ProxyStatus.Dead, now, 5, 0));
        await db.SaveChangesAsync();

        var file = await TelegramOutboundWorker.BuildProxyFileAsync(
            db, maximum: 10, freshAfter: now.AddMinutes(-15), CancellationToken.None);
        var text = System.Text.Encoding.UTF8.GetString(file.Content);

        Assert.Equal(2, file.Count);
        Assert.Equal("socks5://[2001:db8::10]:1080\nhttp://192.0.2.10:8080\n", text);
        Assert.DoesNotContain("192.0.2.11", text, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.12", text, StringComparison.Ordinal);
    }

    private static ProxyHarborDbContext Database() => new(new DbContextOptionsBuilder<ProxyHarborDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static TelegramChat Chat(long id, bool notifications, bool blocked) => new()
    {
        ChatId = id, TelegramUserId = id, UserId = Guid.NewGuid(), DisplayName = $"User {id}",
        NotificationsEnabled = notifications, IsBlocked = blocked
    };

    private static ProxyEndpoint Endpoint(
        string host, int port, ProxyStatus status, DateTimeOffset checkedAt,
        int latency, int successfulChecks, ProxyProtocol protocol = ProxyProtocol.Http) => new()
    {
        Host = host,
        Port = port,
        Protocol = protocol,
        Status = status,
        LatencyMs = latency,
        LastCheckedAt = checkedAt,
        SuccessfulChecks = successfulChecks,
        FailedChecks = status == ProxyStatus.Dead ? 1 : 0,
        FirstAliveAt = status == ProxyStatus.Alive ? checkedAt : null,
        LastAliveAt = status == ProxyStatus.Alive ? checkedAt : null,
        CurrentAliveSince = status == ProxyStatus.Alive ? checkedAt : null
    };
}
