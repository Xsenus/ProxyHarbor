using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Создаёт непрозрачные URL-safe коды без пользовательских данных.</summary>
public static class ReferralCodes
{
    /// <summary>Возвращает 12-символьный код с 48 битами энтропии.</summary>
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}

/// <summary>Единая серверная политика реферальных начислений.</summary>
public static class ReferralRewards
{
    /// <summary>Начальный лимит приглашённых клиентов на одного пользователя.</summary>
    public const int MaximumReferralsPerUser = 10;

    /// <summary>Начисляет бонус за подтверждённую оплату ровно один раз.</summary>
    public static async Task<int> GrantForPurchaseAsync(
        ProxyHarborDbContext db,
        UserManager<ApplicationUser> users,
        PaymentOrder order,
        DateTimeOffset now,
        CancellationToken token)
    {
        var days = order.DurationDays switch
        {
            30 => 1,
            90 => 7,
            180 => 30,
            365 => 90,
            _ => 0
        };
        if (days == 0) return 0;

        var referral = await db.ReferralRelationships
            .SingleOrDefaultAsync(x => x.ReferredUserId == order.UserId, token);
        if (referral is null) return 0;
        var rewardKey = $"payment:{order.Id:N}";
        if (db.ReferralRewards.Local.Any(x => x.RewardKey == rewardKey) ||
            await db.ReferralRewards.AnyAsync(x => x.RewardKey == rewardKey, token)) return 0;

        db.ReferralRewards.Add(new ReferralReward
        {
            ReferralRelationshipId = referral.Id,
            PaymentOrderId = order.Id,
            RewardKey = rewardKey,
            Kind = ReferralRewardKinds.Purchase,
            DaysGranted = days,
            CreatedAt = now
        });
        await ExtendSubscriptionAsync(db, users, referral.ReferrerUserId, days, now, token);
        return days;
    }

    /// <summary>Продлевает оплачиваемое право, начиная от текущего срока или от настоящего момента.</summary>
    public static async Task ExtendSubscriptionAsync(
        ProxyHarborDbContext db,
        UserManager<ApplicationUser> users,
        Guid userId,
        int days,
        DateTimeOffset now,
        CancellationToken token)
    {
        var subscription = await db.Subscriptions.SingleAsync(x => x.UserId == userId, token);
        var begins = subscription.ExpiresAt is { } expiry && expiry > now ? expiry : now;
        subscription.Plan = SubscriptionPlans.Unlimited;
        subscription.Status = SubscriptionStatuses.Active;
        subscription.StartedAt = now;
        subscription.ExpiresAt = begins.AddDays(days);
        subscription.UpdatedAt = now;
        var account = await users.FindByIdAsync(userId.ToString());
        if (account is not null && !await users.IsInRoleAsync(account, UserRoles.Subscriber))
            await users.AddToRoleAsync(account, UserRoles.Subscriber);
    }
}
