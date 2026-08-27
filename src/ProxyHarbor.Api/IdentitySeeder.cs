using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Идемпотентно создаёт роли и переносит bootstrap-администратора в Identity.</summary>
public static class IdentitySeeder
{
    /// <summary>
    /// Пароль из secret используется только при первом создании строки. Последующие
    /// смены пароля через профиль не перезаписываются при перезапуске контейнера.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();

        foreach (var roleName in UserRoles.All)
        {
            if (await roles.RoleExistsAsync(roleName)) continue;
            var roleResult = await roles.CreateAsync(new IdentityRole<Guid>(roleName));
            EnsureSucceeded(roleResult, $"Не удалось создать роль {roleName}");
        }

        var username = configuration["Security:AdminUsername"]!;
        var password = configuration["Security:AdminPassword"]!;
        var email = configuration["Security:AdminEmail"] ?? "admin@proxy.blagodaty.ru";
        var administrator = await users.FindByNameAsync(username);
        if (administrator is null)
        {
            administrator = new ApplicationUser
            {
                UserName = username,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Администратор",
                ReferralCode = ReferralCodes.New(),
                IsActive = true
            };
            EnsureSucceeded(await users.CreateAsync(administrator, password),
                "Не удалось создать bootstrap-администратора");
        }

        if (!administrator.IsActive || string.IsNullOrWhiteSpace(administrator.ReferralCode))
        {
            administrator.IsActive = true;
            if (string.IsNullOrWhiteSpace(administrator.ReferralCode))
                administrator.ReferralCode = ReferralCodes.New();
            EnsureSucceeded(await users.UpdateAsync(administrator),
                "Не удалось активировать bootstrap-администратора");
        }
        EnsureSucceeded(await users.AddToRolesAsync(administrator,
            new[] { UserRoles.User, UserRoles.Subscriber, UserRoles.Administrator }
                .Except(await users.GetRolesAsync(administrator), StringComparer.Ordinal)),
            "Не удалось назначить роли bootstrap-администратору");

        if (!await db.Subscriptions.AnyAsync(x => x.UserId == administrator.Id))
        {
            db.Subscriptions.Add(new UserSubscription
            {
                UserId = administrator.Id,
                Plan = SubscriptionPlans.Unlimited,
                Status = SubscriptionStatuses.Active
            });
            await db.SaveChangesAsync();
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded) return;
        var codes = string.Join(", ", result.Errors.Select(x => x.Code).Distinct(StringComparer.Ordinal));
        throw new InvalidOperationException($"{operation}: {codes}");
    }
}
