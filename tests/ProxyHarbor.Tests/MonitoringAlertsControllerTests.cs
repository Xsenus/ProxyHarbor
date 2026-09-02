using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class MonitoringAlertsControllerTests
{
    private const string Secret = "ci-alertmanager-webhook-token-at-least-32-chars";

    [Fact]
    public async Task InternalAuthenticatedWebhookQueuesOneIdempotentEscapedMessage()
    {
        await using var db = Database();
        var user = new ApplicationUser { UserName = "operator", Email = "operator@example.test" };
        var chat = new TelegramChat
        {
            ChatId = 123456,
            TelegramUserId = 123456,
            UserId = user.Id,
            User = user,
            DisplayName = "Operator"
        };
        db.Users.Add(user);
        db.TelegramChats.Add(chat);
        await db.SaveChangesAsync();

        var controller = Controller(db, new StaticBackupStore(new BackupOptions
        {
            TelegramRecipientId = chat.Id
        }));
        var notification = Notification();

        Assert.IsType<AcceptedResult>(await controller.Receive(notification, default));
        Assert.IsType<AcceptedResult>(await controller.Receive(notification, default));

        var outbound = Assert.Single(await db.TelegramOutboundMessages.AsNoTracking().ToArrayAsync());
        Assert.StartsWith("alertmanager:", outbound.IdempotencyKey, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", outbound.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", JsonSerializer.Deserialize<JsonElement>(outbound.PayloadJson)
            .GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Single(await db.TelegramConversationMessages.ToArrayAsync());
    }

    [Fact]
    public async Task PublicHostAndInvalidBearerAreRejectedWithoutQueueing()
    {
        await using var db = Database();
        var controller = Controller(db, new StaticBackupStore(new BackupOptions()));
        controller.HttpContext.Request.Host = new HostString("proxy.example.test");
        Assert.IsType<NotFoundResult>(await controller.Receive(Notification(), default));

        controller.HttpContext.Request.Host = new HostString("api", 8080);
        controller.HttpContext.Request.Headers.Authorization = "Bearer wrong";
        Assert.IsType<UnauthorizedResult>(await controller.Receive(Notification(), default));
        Assert.Empty(await db.TelegramOutboundMessages.ToArrayAsync());
    }

    [Fact]
    public async Task MissingRecipientReturnsRetryableFailure()
    {
        await using var db = Database();
        var result = Assert.IsType<StatusCodeResult>(await Controller(db,
            new StaticBackupStore(new BackupOptions { TelegramChatId = "987654" }))
            .Receive(Notification(), default));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    [Theory]
    [InlineData("unexpected-receiver", 0)]
    [InlineData("proxyharbor-api", -1)]
    public async Task InvalidReceiverOrTruncationCountIsRejected(string receiver, int truncatedAlerts)
    {
        await using var db = Database();
        var notification = Notification();
        notification.Receiver = receiver;
        notification.TruncatedAlerts = truncatedAlerts;

        Assert.IsType<BadRequestResult>(await Controller(db,
            new StaticBackupStore(new BackupOptions())).Receive(notification, default));
        Assert.Empty(await db.TelegramOutboundMessages.ToArrayAsync());
    }

    [Fact]
    public void IdempotencyChangesByWindowOrAlertSet()
    {
        var first = Notification();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var key = MonitoringAlertsController.CreateIdempotencyKey(first, now);
        Assert.Equal(key, MonitoringAlertsController.CreateIdempotencyKey(first, now.AddMinutes(9)));
        Assert.NotEqual(key, MonitoringAlertsController.CreateIdempotencyKey(first, now.AddMinutes(10)));
        first.Alerts[0].Fingerprint = "different";
        Assert.NotEqual(key, MonitoringAlertsController.CreateIdempotencyKey(first, now));
    }

    private static MonitoringAlertsController Controller(
        ProxyHarborDbContext db, IBackupConfigurationStore backupStore)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Monitoring:AlertmanagerWebhookToken"] = Secret }).Build();
        var controller = new MonitoringAlertsController(configuration, db, backupStore,
            new TelegramDispatchService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Request.Host = new HostString("api", 8080);
        controller.HttpContext.Request.Headers.Authorization = $"Bearer {Secret}";
        return controller;
    }

    private static AlertmanagerNotification Notification() => new()
    {
        Version = "4",
        GroupKey = "{}:{alertname=\"Example\"}",
        Status = "firing",
        Receiver = "proxyharbor-api",
        Alerts =
        [
            new AlertmanagerAlert
            {
                Status = "firing",
                Fingerprint = "abc123",
                StartsAt = "2026-09-02T18:00:00Z",
                Labels = new Dictionary<string, string>
                {
                    ["alertname"] = "Proxy<script>Down",
                    ["severity"] = "critical"
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = "Unsafe <b>tag</b> & text"
                }
            }
        ]
    };

    private static ProxyHarborDbContext Database() => new(
        new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"monitoring-alerts-{Guid.NewGuid():N}").Options);

    private sealed class StaticBackupStore(BackupOptions options) : IBackupConfigurationStore
    {
        public Task<BackupOptions> GetAsync(CancellationToken token = default) => Task.FromResult(options);
        public Task SaveAsync(BackupOptions next, CancellationToken token = default) => Task.CompletedTask;
    }
}
