using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует единый PostgreSQL snapshot операционной сводки Telegram.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class AdminTelegramQueryIntegrationTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task TelegramOverviewUsesOneConditionalAggregateStatement()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_telegram_overview_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var adminConnection = new NpgsqlConnection(baseConnectionString);
        await adminConnection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", adminConnection))
            await create.ExecuteNonQueryAsync();

        try
        {
            var commands = new TelegramOverviewCommandCounter();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ProxyHarborDbContext>(options => options
                .UseNpgsql(builder.ConnectionString, postgres =>
                    postgres.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), null))
                .AddInterceptors(commands));
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<ProxyHarborDbContext>();

            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await db.Database.MigrateAsync();

            var first = await CreateUserAsync(users, "telegram-overview-1");
            var second = await CreateUserAsync(users, "telegram-overview-2");
            var third = await CreateUserAsync(users, "telegram-overview-3");
            var now = DateTimeOffset.UtcNow;
            var active = Chat(first, 1001, now.AddMinutes(-5));
            active.MarketingNotificationsEnabled = true;
            active.MarketingConsentGrantedAt = now.AddDays(-1);
            active.MarketingConsentVersion = LegalDocumentVersions.MarketingConsent;
            var blocked = Chat(second, 1002, now.AddDays(-40));
            blocked.IsBlocked = true;
            blocked.MarketingNotificationsEnabled = true;
            blocked.MarketingConsentVersion = LegalDocumentVersions.MarketingConsent;
            var staleConsent = Chat(third, 1003, now.AddHours(-2));
            staleConsent.NotificationsEnabled = false;
            staleConsent.MarketingNotificationsEnabled = true;
            staleConsent.MarketingConsentVersion = "outdated";
            db.TelegramChats.AddRange(active, blocked, staleConsent);
            db.PaymentOrders.AddRange(
                Order(first, "telegram_stars", PaymentStatuses.Paid, 100, now),
                Order(first, "telegram_stars", PaymentStatuses.Paid, 250, now),
                Order(first, "telegram_stars", PaymentStatuses.Pending, 500, now),
                Order(first, "yoomoney", PaymentStatuses.Paid, 99_00, now));
            db.TelegramOutboundMessages.AddRange(
                Outbound(active, TelegramOutboundStatuses.Pending, "overview-pending"),
                Outbound(active, TelegramOutboundStatuses.Processing, "overview-processing"),
                Outbound(active, TelegramOutboundStatuses.Failed, "overview-failed"),
                Outbound(active, TelegramOutboundStatuses.Sent, "overview-sent"));
            await db.SaveChangesAsync();

            commands.Reset();
            var enabled = Controller(db, marketingEnabled: true);
            var result = Assert.IsType<OkObjectResult>(await enabled.Get(CancellationToken.None));
            AssertSingleStatement(commands);
            AssertStats(result.Value, users: 3, activeUsers: 2, notifications: 1, marketing: 1,
                blocked: 1, paidOrders: 2, starsRevenue: 350, queued: 2, failed: 1);

            commands.Reset();
            var disabled = Controller(db, marketingEnabled: false);
            result = Assert.IsType<OkObjectResult>(await disabled.Get(CancellationToken.None));
            AssertSingleStatement(commands);
            AssertStats(result.Value, users: 3, activeUsers: 2, notifications: 1, marketing: 0,
                blocked: 1, paidOrders: 2, starsRevenue: 350, queued: 2, failed: 1);

            await db.TelegramOutboundMessages.ExecuteDeleteAsync();
            await db.TelegramChats.ExecuteDeleteAsync();
            await db.PaymentOrders.ExecuteDeleteAsync();
            commands.Reset();
            result = Assert.IsType<OkObjectResult>(await enabled.Get(CancellationToken.None));
            AssertSingleStatement(commands);
            AssertStats(result.Value, users: 0, activeUsers: 0, notifications: 0, marketing: 0,
                blocked: 0, paidOrders: 0, starsRevenue: 0, queued: 0, failed: 0);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", adminConnection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<ApplicationUser> CreateUserAsync(UserManager<ApplicationUser> users, string name)
    {
        var user = new ApplicationUser
        {
            UserName = name,
            Email = $"{name}@example.test",
            EmailConfirmed = true,
            ReferralCode = Guid.NewGuid().ToString("N")[..12]
        };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        return user;
    }

    private static TelegramChat Chat(ApplicationUser user, long id, DateTimeOffset lastInteraction) => new()
    {
        ChatId = id,
        TelegramUserId = id,
        UserId = user.Id,
        User = user,
        DisplayName = user.UserName!,
        NotificationsEnabled = true,
        LastInteractionAt = lastInteraction
    };

    private static PaymentOrder Order(
        ApplicationUser user,
        string provider,
        string status,
        long amount,
        DateTimeOffset now) => new()
        {
            UserId = user.Id,
            User = user,
            ProductCode = "telegram-overview",
            Plan = SubscriptionPlans.Unlimited,
            Provider = provider,
            PaymentMethod = provider,
            AmountMinor = amount,
            Currency = provider == "telegram_stars" ? "XTR" : "RUB",
            DurationDays = 1,
            Status = status,
            PaidAt = status == PaymentStatuses.Paid ? now : null,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static TelegramOutboundMessage Outbound(TelegramChat chat, string status, string key) => new()
    {
        TelegramChatId = chat.Id,
        TelegramChat = chat,
        PayloadJson = "{}",
        IdempotencyKey = key,
        Status = status
    };

    private static AdminTelegramController Controller(ProxyHarborDbContext db, bool marketingEnabled) => new(
        db,
        new StaticBotStore(new TelegramBotOptions { MarketingBroadcastsEnabled = marketingEnabled }),
        new StaticPaymentStore(new PaymentOptions()),
        new TelegramBotApiClient(new NoopHttpClientFactory()),
        new TelegramDispatchService(db));

    private static void AssertSingleStatement(TelegramOverviewCommandCounter commands)
    {
        var sql = Assert.Single(commands.SelectSql);
        Assert.Contains("WITH chat_stats AS", sql, StringComparison.Ordinal);
        Assert.Equal(1, Count(sql, "\"TelegramChats\""));
        Assert.Equal(1, Count(sql, "\"PaymentOrders\""));
        Assert.Equal(1, Count(sql, "\"TelegramOutboundMessages\""));
    }

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;

    private static void AssertStats(
        object? value,
        int users,
        int activeUsers,
        int notifications,
        int marketing,
        int blocked,
        int paidOrders,
        long starsRevenue,
        int queued,
        int failed)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, WebJson));
        var stats = json.RootElement.GetProperty("stats");
        Assert.Equal(users, stats.GetProperty("users").GetInt32());
        Assert.Equal(activeUsers, stats.GetProperty("activeUsers30d").GetInt32());
        Assert.Equal(notifications, stats.GetProperty("notificationsEnabled").GetInt32());
        Assert.Equal(marketing, stats.GetProperty("marketingConsents").GetInt32());
        Assert.Equal(blocked, stats.GetProperty("blocked").GetInt32());
        Assert.Equal(paidOrders, stats.GetProperty("paidOrders").GetInt32());
        Assert.Equal(starsRevenue, stats.GetProperty("starsRevenue").GetInt64());
        Assert.Equal(queued, stats.GetProperty("queued").GetInt32());
        Assert.Equal(failed, stats.GetProperty("failed").GetInt32());
    }

    private sealed class TelegramOverviewCommandCounter : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> selectSql = new();
        internal string[] SelectSql => selectSql.ToArray();

        internal void Reset()
        {
            while (selectSql.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                selectSql.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StaticBotStore(TelegramBotOptions options) : ITelegramBotConfigurationStore
    {
        public Task<TelegramBotOptions> GetAsync(CancellationToken token = default) => Task.FromResult(options);
        public Task SaveAsync(TelegramBotOptions value, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class StaticPaymentStore(PaymentOptions options) : IPaymentConfigurationStore
    {
        public Task<PaymentOptions> GetAsync(CancellationToken token = default) => Task.FromResult(options);
        public Task SaveAsync(PaymentOptions value, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
