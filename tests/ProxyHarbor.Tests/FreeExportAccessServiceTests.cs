using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет постоянный cooldown и серверное право платной подписки.</summary>
public sealed class FreeExportAccessServiceTests
{
    [Fact]
    public async Task AnonymousClientGetsOneGrantPerCooldownWindow()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"free-export-access-{Guid.NewGuid():N}").Options;
        var service = new FreeExportAccessService(new TestFactory(options));

        var first = await service.AcquireAsync(new ClaimsPrincipal(), "203.0.113.10", CancellationToken.None);
        var second = await service.AcquireAsync(new ClaimsPrincipal(), "203.0.113.10", CancellationToken.None);

        Assert.True(first.Allowed);
        Assert.False(first.IsPaid);
        Assert.Equal(10, first.Limit);
        Assert.False(second.Allowed);
        Assert.Equal(first.NextAllowedAt, second.NextAllowedAt);
    }

    [Fact]
    public async Task ActivePaidSubscriptionBypassesFreeGrantStorage()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"paid-export-access-{Guid.NewGuid():N}").Options;
        var userId = Guid.NewGuid();
        await using (var db = new ProxyHarborDbContext(options))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = "paid", Email = "paid@example.test" });
            db.Subscriptions.Add(new UserSubscription
            {
                UserId = userId,
                Plan = SubscriptionPlans.Pro,
                Status = SubscriptionStatuses.Active,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();
        }
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString("D"))], "test");
        var service = new FreeExportAccessService(new TestFactory(options));

        var access = await service.AcquireAsync(new ClaimsPrincipal(identity), "203.0.113.11", CancellationToken.None);

        Assert.True(access.Allowed);
        Assert.True(access.IsPaid);
        Assert.Equal("paid", access.Tier);
        await using var verify = new ProxyHarborDbContext(options);
        Assert.Empty(await verify.FreeProxyExportGrants.ToListAsync());
    }

    private sealed class TestFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
