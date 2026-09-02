using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
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

/// <summary>
/// Фиксирует число SQL round-trip личного кабинета и реферального аудита на PostgreSQL.
/// </summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class AccountReferralQueryIntegrationTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task AccountAndReferralReadsUseBoundedConsistentSnapshots()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_account_referrals_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var adminConnection = new NpgsqlConnection(baseConnectionString);
        await adminConnection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", adminConnection))
            await create.ExecuteNonQueryAsync();

        try
        {
            var commands = new AccountCommandCounter();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddDbContext<ProxyHarborDbContext>(options => options
                .UseNpgsql(builder.ConnectionString, postgres =>
                    postgres.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), null))
                .AddInterceptors(commands));
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<IdentityRole<Guid>>()
                .AddSignInManager()
                .AddEntityFrameworkStores<ProxyHarborDbContext>()
                .AddDefaultTokenProviders();
            services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
            services.AddAuthorization();

            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
            await db.Database.MigrateAsync();
            foreach (var role in UserRoles.All)
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);

            var owner = User("referral-owner", "owner@example.test", "ownerref0001");
            var referred = User("referred-client", "referred@example.test", "referred0001");
            Assert.True((await users.CreateAsync(owner, "Initial-user-42!")).Succeeded);
            Assert.True((await users.CreateAsync(referred)).Succeeded);
            Assert.True((await users.AddToRolesAsync(owner,
                [UserRoles.User, UserRoles.Subscriber])).Succeeded);

            var now = DateTimeOffset.UtcNow;
            var relationship = new ReferralRelationship
            {
                ReferrerUserId = owner.Id,
                ReferredUserId = referred.Id,
                Slot = 1,
                CreatedAt = now.AddDays(-1)
            };
            var apiToken = new UserApiToken
            {
                UserId = owner.Id,
                Name = "Query integration",
                SecretHash = new byte[32],
                DisplaySuffix = "ABC123",
                CreatedAt = now.AddHours(-1)
            };
            db.Subscriptions.Add(new UserSubscription
            {
                UserId = owner.Id,
                Plan = SubscriptionPlans.Unlimited,
                Status = SubscriptionStatuses.Active,
                ExpiresAt = now.AddDays(30)
            });
            db.ReferralRelationships.Add(relationship);
            db.ReferralRewards.AddRange(
                new ReferralReward
                {
                    ReferralRelationshipId = relationship.Id,
                    RewardKey = "query:signup",
                    Kind = ReferralRewardKinds.Signup,
                    DaysGranted = 1,
                    CreatedAt = now.AddDays(-1)
                },
                new ReferralReward
                {
                    ReferralRelationshipId = relationship.Id,
                    RewardKey = "query:purchase",
                    Kind = ReferralRewardKinds.Purchase,
                    DaysGranted = 7,
                    CreatedAt = now.AddHours(-2)
                });
            db.UserApiTokens.Add(apiToken);
            db.UserApiTokenRequests.Add(new UserApiTokenRequest
            {
                UserApiTokenId = apiToken.Id,
                UserId = owner.Id,
                IpAddress = "203.0.113.77",
                Method = "GET",
                Path = "/api/v1/proxies",
                StatusCode = StatusCodes.Status200OK,
                ItemCount = 5,
                DurationMs = 12,
                RequestedAt = now
            });
            db.TelegramBotConfigurations.Add(new TelegramBotConfiguration
            {
                Id = 1,
                SettingsJson = "{}",
                BotUsername = "proxyharbor_query_bot"
            });
            await db.SaveChangesAsync();

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, owner.Id.ToString())],
                IdentityConstants.ApplicationScheme));
            var http = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
                User = principal
            };
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("proxy.example.test");
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var account = new AccountController(users, signIn, db, new UserApiTokenService(db, users))
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };

            commands.Reset();
            var profile = Assert.IsType<OkObjectResult>(await account.Profile(CancellationToken.None));
            AssertSnapshot(commands, expectedReads: 3);
            using (var json = Serialize(profile.Value))
            {
                var root = json.RootElement;
                Assert.Equal(1, root.GetProperty("referral").GetProperty("invited").GetInt32());
                Assert.Equal(8, root.GetProperty("referral").GetProperty("rewardDays").GetInt32());
                Assert.Contains("proxyharbor_query_bot", root.GetProperty("referral")
                    .GetProperty("telegramLink").GetString(), StringComparison.Ordinal);
                Assert.Single(root.GetProperty("apiTokens").EnumerateArray());
            }

            commands.Reset();
            var referrals = Assert.IsType<OkObjectResult>(await account.Referrals(
                token: CancellationToken.None));
            AssertSnapshot(commands, expectedReads: 2);
            using (var json = Serialize(referrals.Value))
            {
                Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
                Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
            }

            commands.Reset();
            var history = Assert.IsType<OkObjectResult>(await account.ApiTokenHistory(
                tokenId: apiToken.Id, token: CancellationToken.None));
            AssertSnapshot(commands, expectedReads: 3);
            using (var json = Serialize(history.Value))
                Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());

            commands.Reset();
            var unfilteredHistory = Assert.IsType<OkObjectResult>(await account.ApiTokenHistory(
                token: CancellationToken.None));
            AssertSnapshot(commands, expectedReads: 3);
            using (var json = Serialize(unfilteredHistory.Value))
                Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());

            commands.Reset();
            var admin = Assert.IsType<OkObjectResult>(await new AdminReferralsController(db).List(
                token: CancellationToken.None));
            AssertSnapshot(commands, expectedReads: 3);
            Assert.Single(commands.SelectSql, sql =>
                sql.Contains("\"ReferralRewards\"", StringComparison.Ordinal) &&
                sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
            using (var json = Serialize(admin.Value))
            {
                var summary = json.RootElement.GetProperty("summary");
                Assert.Equal(1, summary.GetProperty("referrals").GetInt32());
                Assert.Equal(8, summary.GetProperty("rewardDays").GetInt32());
                Assert.Equal(1, summary.GetProperty("purchaseRewards").GetInt32());
            }

            var independentlyRevoked = new UserApiToken
            {
                UserId = owner.Id,
                Name = "Set based revoke",
                SecretHash = new byte[32],
                DisplaySuffix = "XYZ789",
                CreatedAt = now
            };
            db.UserApiTokens.Add(independentlyRevoked);
            await db.SaveChangesAsync();
            commands.Reset();
            Assert.True(await new UserApiTokenService(db, users).RevokeAsync(
                owner.Id, independentlyRevoked.Id, CancellationToken.None));
            Assert.Equal(0, commands.UserApiTokenSelects);
            Assert.Equal(1, commands.UserApiTokenUpdates);

            commands.Reset();
            Assert.IsType<NoContentResult>(await account.ChangePassword(new ChangePasswordRequest
            {
                CurrentPassword = "Initial-user-42!",
                NewPassword = "Changed-user-42!"
            }, CancellationToken.None));
            Assert.Equal(0, commands.UserApiTokenSelects);
            Assert.Equal(1, commands.UserApiTokenUpdates);

            db.ChangeTracker.Clear();
            Assert.All(await db.UserApiTokens.AsNoTracking().ToArrayAsync(),
                storedToken => Assert.NotNull(storedToken.RevokedAt));

            await db.ReferralRelationships.ExecuteDeleteAsync();
            commands.Reset();
            var empty = Assert.IsType<OkObjectResult>(await new AdminReferralsController(db).List(
                token: CancellationToken.None));
            AssertSnapshot(commands, expectedReads: 3);
            using (var json = Serialize(empty.Value))
            {
                var summary = json.RootElement.GetProperty("summary");
                Assert.Equal(0, summary.GetProperty("referrals").GetInt32());
                Assert.Equal(0, summary.GetProperty("rewardDays").GetInt32());
                Assert.Equal(0, summary.GetProperty("purchaseRewards").GetInt32());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", adminConnection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ApplicationUser User(string name, string email, string referralCode) => new()
    {
        UserName = name,
        Email = email,
        EmailConfirmed = true,
        ReferralCode = referralCode
    };

    private static JsonDocument Serialize(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, WebJsonOptions));

    private static void AssertSnapshot(AccountCommandCounter commands, int expectedReads)
    {
        Assert.Equal(expectedReads, commands.SelectSql.Length);
        Assert.All(commands.ReadIsolationLevels,
            isolation => Assert.Equal(IsolationLevel.RepeatableRead, isolation));
    }

    private sealed class AccountCommandCounter : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> selectSql = new();
        private readonly ConcurrentQueue<IsolationLevel> readIsolationLevels = new();
        private int userApiTokenSelects;
        private int userApiTokenUpdates;

        internal string[] SelectSql => selectSql.ToArray();
        internal IsolationLevel[] ReadIsolationLevels => readIsolationLevels.ToArray();
        internal int UserApiTokenSelects => Volatile.Read(ref userApiTokenSelects);
        internal int UserApiTokenUpdates => Volatile.Read(ref userApiTokenUpdates);

        internal void Reset()
        {
            while (selectSql.TryDequeue(out _)) { }
            while (readIsolationLevels.TryDequeue(out _)) { }
            Volatile.Write(ref userApiTokenSelects, 0);
            Volatile.Write(ref userApiTokenUpdates, 0);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                selectSql.Enqueue(command.CommandText);
                readIsolationLevels.Enqueue(
                    command.Transaction?.IsolationLevel ?? IsolationLevel.Unspecified);
                if (command.CommandText.Contains("\"UserApiTokens\"", StringComparison.Ordinal))
                    Interlocked.Increment(ref userApiTokenSelects);
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"UserApiTokens\"", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref userApiTokenUpdates);
            return ValueTask.FromResult(result);
        }
    }
}
