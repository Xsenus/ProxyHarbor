using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>
/// Проверяет bounded-обработку напоминаний на настоящей PostgreSQL: модель
/// блокировок, частичный индекс и set-based операции невозможно надёжно
/// подтвердить InMemory-провайдером.
/// </summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class SubscriptionReminderProcessorIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task BatchesRemainBoundedIdempotentAndRepeatAfterExtension()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_subscription_reminders_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var now = DateTimeOffset.UtcNow;
            await using (var seed = new ProxyHarborDbContext(options))
            {
                await seed.Database.MigrateAsync();
                var subscriberRole = new IdentityRole<Guid>
                {
                    Id = Guid.NewGuid(),
                    Name = UserRoles.Subscriber,
                    NormalizedName = UserRoles.Subscriber.ToUpperInvariant(),
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                };
                seed.Roles.Add(subscriberRole);
                for (var index = 0; index < 3; index++)
                {
                    var expired = User($"expired-{index}", 10_000 + index);
                    var upcoming = User($"upcoming-{index}", 20_000 + index);
                    seed.Users.AddRange(expired.User, upcoming.User);
                    seed.TelegramChats.AddRange(expired.Chat, upcoming.Chat);
                    seed.Subscriptions.AddRange(
                        Subscription(expired.User, now.AddMinutes(-30 - index)),
                        Subscription(upcoming.User, now.AddHours(6).AddMinutes(index)));
                    seed.UserRoles.Add(new IdentityUserRole<Guid>
                    {
                        UserId = expired.User.Id,
                        RoleId = subscriberRole.Id
                    });
                }
                await seed.SaveChangesAsync();
            }

            // Один цикл намеренно ограничен одной партией по две записи.
            await using (var firstDb = new ProxyHarborDbContext(options))
            {
                var first = await Processor(firstDb).RunAsync(now, CancellationToken.None);
                Assert.Equal(2, first.Expired);
                Assert.Equal(2, first.Upcoming);
                Assert.Equal(4, first.Notifications);
                Assert.Equal(4, first.TelegramMessages);
            }
            await using (var afterFirst = new ProxyHarborDbContext(options))
            {
                Assert.Equal(2, await afterFirst.Subscriptions.CountAsync(x => x.Status == SubscriptionStatuses.Expired));
                Assert.Single(await afterFirst.UserRoles.ToArrayAsync());
                Assert.Equal(4, await afterFirst.UserNotifications.CountAsync());
                Assert.Equal(4, await afterFirst.TelegramOutboundMessages.CountAsync());
            }

            await using (var secondDb = new ProxyHarborDbContext(options))
            {
                var second = await Processor(secondDb).RunAsync(now, CancellationToken.None);
                Assert.Equal(1, second.Expired);
                Assert.Equal(1, second.Upcoming);
                Assert.Equal(2, second.Notifications);
                Assert.Equal(2, second.TelegramMessages);
            }
            await using (var idempotentDb = new ProxyHarborDbContext(options))
            {
                var idempotent = await Processor(idempotentDb).RunAsync(now, CancellationToken.None);
                Assert.Equal(0, idempotent.Expired);
                Assert.Equal(0, idempotent.Upcoming);
                Assert.Equal(0, idempotent.Notifications);
                Assert.Equal(0, idempotent.TelegramMessages);
                Assert.Equal(6, await idempotentDb.UserNotifications.CountAsync());
                Assert.Equal(6, await idempotentDb.TelegramOutboundMessages.CountAsync());
            }

            // После перехода из 12-часового окна в 1-часовое каждый активный
            // пользователь получает ровно одно новое напоминание.
            var oneHourNow = now.AddHours(5).AddMinutes(15);
            for (var iteration = 0; iteration < 2; iteration++)
            {
                await using var oneHourDb = new ProxyHarborDbContext(options);
                _ = await Processor(oneHourDb).RunAsync(oneHourNow, CancellationToken.None);
            }
            await using (var afterOneHour = new ProxyHarborDbContext(options))
            {
                Assert.Equal(9, await afterOneHour.UserNotifications.CountAsync());
                Assert.Equal(9, await afterOneHour.TelegramOutboundMessages.CountAsync());
                Assert.Equal(3, await afterOneHour.Subscriptions.CountAsync(x =>
                    x.Status == SubscriptionStatuses.Active && x.Reminder1HourForExpiresAt == x.ExpiresAt));

                // Продление меняет ExpiresAt. Маркеры сохраняют аудит старого срока,
                // а несоответствие новому сроку автоматически разрешает новое событие.
                var extended = await afterOneHour.Subscriptions
                    .Where(x => x.Status == SubscriptionStatuses.Active)
                    .OrderBy(x => x.Id).FirstAsync();
                extended.ExpiresAt = oneHourNow.AddHours(8);
                await afterOneHour.SaveChangesAsync();
            }
            await using (var extensionDb = new ProxyHarborDbContext(options))
            {
                var extension = await Processor(extensionDb).RunAsync(oneHourNow, CancellationToken.None);
                Assert.Equal(1, extension.Upcoming);
                Assert.Equal(1, extension.Notifications);
                Assert.Equal(1, extension.TelegramMessages);
                Assert.Equal(10, await extensionDb.UserNotifications.CountAsync());
                Assert.Equal(10, await extensionDb.TelegramOutboundMessages.CountAsync());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static SubscriptionReminderProcessor Processor(ProxyHarborDbContext db) =>
        new(db, new TelegramDispatchService(db), batchSize: 2, maximumBatches: 1, cleanupBatchSize: 2);

    private static (ApplicationUser User, TelegramChat Chat) User(string suffix, long telegramId)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = suffix,
            NormalizedUserName = suffix.ToUpperInvariant(),
            Email = $"{suffix}@example.test",
            NormalizedEmail = $"{suffix}@example.test".ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            ReferralCode = Guid.NewGuid().ToString("N")[..12],
            PreferredLanguage = SupportedLanguages.Default
        };
        return (user, new TelegramChat
        {
            UserId = user.Id,
            User = user,
            ChatId = telegramId,
            TelegramUserId = telegramId,
            DisplayName = suffix,
            NotificationsEnabled = true
        });
    }

    private static UserSubscription Subscription(ApplicationUser user, DateTimeOffset expiresAt) => new()
    {
        UserId = user.Id,
        User = user,
        Plan = SubscriptionPlans.Pro,
        Status = SubscriptionStatuses.Active,
        StartedAt = expiresAt.AddDays(-30),
        ExpiresAt = expiresAt,
        UpdatedAt = expiresAt.AddDays(-30)
    };
}
