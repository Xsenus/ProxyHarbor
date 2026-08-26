using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет единую матрицу платного доступа для cookie и API-токенов.</summary>
public sealed class UserApiTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AdministratorAlwaysHasPaidAccess()
    {
        Assert.True(UserApiTokenService.HasPaidAccess(null, [UserRoles.Administrator], Now));
    }

    [Theory]
    [InlineData(SubscriptionPlans.Pro, SubscriptionStatuses.Active, true)]
    [InlineData(SubscriptionPlans.Unlimited, SubscriptionStatuses.Trialing, true)]
    [InlineData(SubscriptionPlans.Free, SubscriptionStatuses.Active, false)]
    [InlineData(SubscriptionPlans.Pro, SubscriptionStatuses.Canceled, false)]
    [InlineData(SubscriptionPlans.Unlimited, SubscriptionStatuses.Expired, false)]
    public void SubscriptionPlanAndStatusDeterminePaidAccess(string plan, string status, bool expected)
    {
        var subscription = new UserSubscription { Plan = plan, Status = status };

        Assert.Equal(expected, UserApiTokenService.HasPaidAccess(subscription, [UserRoles.User], Now));
    }

    [Fact]
    public void ExpiredSubscriptionDoesNotHavePaidAccess()
    {
        var subscription = new UserSubscription
        {
            Plan = SubscriptionPlans.Unlimited,
            Status = SubscriptionStatuses.Active,
            ExpiresAt = Now
        };

        Assert.False(UserApiTokenService.HasPaidAccess(subscription, [UserRoles.Subscriber], Now));
        subscription.ExpiresAt = Now.AddSeconds(1);
        Assert.True(UserApiTokenService.HasPaidAccess(subscription, [UserRoles.Subscriber], Now));
    }
}
