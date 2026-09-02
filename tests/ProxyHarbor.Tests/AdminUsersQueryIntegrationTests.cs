using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует bounded SQL-бюджет и snapshot административного реестра пользователей.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class AdminUsersQueryIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task UserRegistryUsesCountAndOneSecretFreePageReader()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_admin_users_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var commands = new RegistryCommandCounter();
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
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            await db.Database.MigrateAsync();
            foreach (var role in UserRoles.All)
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);

            var now = DateTimeOffset.UtcNow;
            var paid = await CreateUserAsync(users, "paid-user", true, now.AddMinutes(-1));
            Assert.True((await users.AddToRolesAsync(paid, [UserRoles.User, UserRoles.Subscriber])).Succeeded);
            var free = await CreateUserAsync(users, "free-user", true, now.AddMinutes(-2));
            Assert.True((await users.AddToRoleAsync(free, UserRoles.User)).Succeeded);
            var disabled = await CreateUserAsync(users, "disabled-user", false, now.AddMinutes(-3));
            Assert.True((await users.AddToRoleAsync(disabled, UserRoles.User)).Succeeded);
            db.Subscriptions.Add(new UserSubscription
            {
                UserId = paid.Id,
                User = paid,
                Plan = SubscriptionPlans.Pro,
                Status = SubscriptionStatuses.Active,
                StartedAt = now.AddDays(-1),
                ExpiresAt = now.AddDays(29)
            });
            await db.SaveChangesAsync();

            var controller = new AdminUsersController(users, db);
            commands.Reset();
            var filtered = Page(await controller.List(
                1, 10, "paid@example", "active", SubscriptionPlans.Pro, CancellationToken.None));
            AssertRegistryBudget(commands);
            var paidRow = Assert.Single(filtered.Items);
            Assert.Equal(1, filtered.Total);
            Assert.Equal("paid-user", paidRow.UserName);
            Assert.Equal([UserRoles.Subscriber, UserRoles.User], paidRow.Roles);
            Assert.Equal(SubscriptionPlans.Pro, paidRow.Subscription?.Plan);
            Assert.Equal(SubscriptionStatuses.Active, paidRow.Subscription?.Status);

            commands.Reset();
            var activeFree = Page(await controller.List(
                1, 10, null, "active", SubscriptionPlans.Free, CancellationToken.None));
            AssertRegistryBudget(commands);
            Assert.Equal(1, activeFree.Total);
            Assert.Equal(free.Id, Assert.Single(activeFree.Items).Id);

            commands.Reset();
            var all = Page(await controller.List(1, 10, cancellationToken: CancellationToken.None));
            AssertRegistryBudget(commands);
            Assert.Equal(3, all.Total);
            Assert.Equal(3, all.Items.Count);
            Assert.Null(all.Items.Single(item => item.Id == free.Id).Subscription);
            Assert.False(all.Items.Single(item => item.Id == disabled.Id).IsActive);

            commands.Reset();
            var emptyPage = Page(await controller.List(100, 10, cancellationToken: CancellationToken.None));
            AssertRegistryBudget(commands);
            Assert.Equal(3, emptyPage.Total);
            Assert.Empty(emptyPage.Items);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        UserManager<ApplicationUser> users,
        string name,
        bool active,
        DateTimeOffset createdAt)
    {
        var user = new ApplicationUser
        {
            UserName = name,
            Email = $"{name.Replace("-user", string.Empty, StringComparison.Ordinal)}@example.test",
            EmailConfirmed = true,
            DisplayName = name,
            IsActive = active,
            CreatedAt = createdAt,
            ReferralCode = Guid.NewGuid().ToString("N")[..12]
        };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        return user;
    }

    private static PagedResult<AdminUserResponse> Page(ActionResult<PagedResult<AdminUserResponse>> action) =>
        Assert.IsType<PagedResult<AdminUserResponse>>(Assert.IsType<OkObjectResult>(action.Result).Value);

    private static void AssertRegistryBudget(RegistryCommandCounter commands)
    {
        Assert.Equal(2, commands.Reads.Length);
        Assert.All(commands.Reads, read => Assert.Equal(IsolationLevel.RepeatableRead, read.IsolationLevel));
        var pageSql = Assert.Single(commands.Reads, read =>
            read.Sql.Contains("AspNetUserRoles", StringComparison.Ordinal));
        Assert.Contains("Subscriptions", pageSql.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordHash", pageSql.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SecurityStamp", pageSql.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrencyStamp", pageSql.Sql, StringComparison.Ordinal);
    }

    private sealed class RegistryCommandCounter : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<RegistryRead> reads = new();
        internal RegistryRead[] Reads => reads.ToArray();

        internal void Reset()
        {
            while (reads.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                reads.Enqueue(new RegistryRead(command.CommandText, command.Transaction?.IsolationLevel));
            return ValueTask.FromResult(result);
        }
    }

    private sealed record RegistryRead(string Sql, IsolationLevel? IsolationLevel);
}
