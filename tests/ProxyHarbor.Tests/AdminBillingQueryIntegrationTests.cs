using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

/// <summary>Фиксирует число billing aggregate и совместимость ручного update с Npgsql retry.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class AdminBillingQueryIntegrationTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task BillingReadsAreConsistentAndSubscriptionUpdateIsRetrySafe()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_billing_queries_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var adminConnection = new NpgsqlConnection(baseConnectionString);
        await adminConnection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", adminConnection))
            await create.ExecuteNonQueryAsync();

        try
        {
            var reads = new BillingCommandCounter();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ProxyHarborDbContext>(options => options
                .UseNpgsql(builder.ConnectionString, postgres =>
                    postgres.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), null))
                .AddInterceptors(reads));
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<ProxyHarborDbContext>();
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            await db.Database.MigrateAsync();
            foreach (var role in new[] { UserRoles.User, UserRoles.Subscriber, UserRoles.Administrator })
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);

            var administrator = new ApplicationUser
            {
                UserName = "billing-admin",
                Email = "billing-admin@example.test",
                EmailConfirmed = true,
                ReferralCode = "billadmin"
            };
            var client = new ApplicationUser
            {
                UserName = "billing-client",
                Email = "billing-client@example.test",
                EmailConfirmed = true,
                ReferralCode = "billclient"
            };
            Assert.True((await users.CreateAsync(administrator)).Succeeded);
            Assert.True((await users.CreateAsync(client)).Succeeded);
            Assert.True((await users.AddToRolesAsync(administrator,
                [UserRoles.User, UserRoles.Administrator])).Succeeded);
            Assert.True((await users.AddToRolesAsync(client,
                [UserRoles.User, UserRoles.Subscriber])).Succeeded);

            var now = DateTimeOffset.UtcNow;
            var subscription = new UserSubscription
            {
                UserId = client.Id,
                Plan = SubscriptionPlans.Pro,
                Status = SubscriptionStatuses.Active,
                StartedAt = now.AddDays(-2),
                ExpiresAt = now.AddDays(5)
            };
            db.Subscriptions.AddRange(
                subscription,
                new UserSubscription
                {
                    UserId = administrator.Id,
                    Plan = SubscriptionPlans.Free,
                    Status = SubscriptionStatuses.Trialing,
                    StartedAt = now.AddDays(-1),
                    ExpiresAt = now.AddDays(10)
                });
            db.PaymentOrders.Add(new PaymentOrder
            {
                UserId = client.Id,
                ProductCode = "unlimited-month",
                Plan = SubscriptionPlans.Pro,
                Provider = "yookassa",
                AmountMinor = 69_000,
                Currency = "RUB",
                DurationDays = 30,
                Status = PaymentStatuses.Paid,
                ProviderPaymentId = "billing-query-test",
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now,
                PaidAt = now
            });
            await db.SaveChangesAsync();

            reads.Reset();
            var subscriptionController = new AdminSubscriptionsController(db, users);
            var subscriptionList = Assert.IsType<OkObjectResult>(await subscriptionController.List(
                token: CancellationToken.None));
            Assert.Equal(3, reads.SubscriptionReads);
            Assert.All(reads.ReadIsolationLevels, isolation => Assert.Equal(IsolationLevel.RepeatableRead, isolation));
            using (var json = JsonDocument.Parse(JsonSerializer.Serialize(subscriptionList.Value, WebJsonOptions)))
            {
                var summary = json.RootElement.GetProperty("summary");
                Assert.Equal(1, summary.GetProperty("active").GetInt32());
                Assert.Equal(1, summary.GetProperty("trialing").GetInt32());
                Assert.Equal(0, summary.GetProperty("suspended").GetInt32());
                Assert.Equal(1, summary.GetProperty("expiringSoon").GetInt32());
            }

            reads.Reset();
            var orderList = Assert.IsType<OkObjectResult>(await new AdminPaymentOrdersController(db).List(
                token: CancellationToken.None));
            Assert.Equal(3, reads.PaymentOrderReads);
            Assert.All(reads.ReadIsolationLevels, isolation => Assert.Equal(IsolationLevel.RepeatableRead, isolation));
            Assert.Contains("billing-query-test", JsonSerializer.Serialize(orderList.Value, WebJsonOptions));

            reads.Reset();
            subscriptionController.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier,
                            administrator.Id.ToString())], "integration"))
                }
            };
            var updated = await subscriptionController.Update(subscription.Id, new UpdateSubscriptionRequest
            {
                Plan = SubscriptionPlans.Pro,
                Status = SubscriptionStatuses.Suspended,
                ExtensionDays = 7,
                Reason = "PostgreSQL retry contract"
            }, CancellationToken.None);
            Assert.IsType<NoContentResult>(updated);
            Assert.Equal(IsolationLevel.Serializable, reads.SubscriptionWriteIsolation);

            db.ChangeTracker.Clear();
            Assert.Equal(SubscriptionStatuses.Suspended,
                (await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == subscription.Id)).Status);
            Assert.Single(await db.SubscriptionAdminActions.AsNoTracking().ToArrayAsync());
            var refreshedClient = await users.FindByIdAsync(client.Id.ToString());
            Assert.NotNull(refreshedClient);
            Assert.False(await users.IsInRoleAsync(refreshedClient!, UserRoles.Subscriber));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", adminConnection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class BillingCommandCounter : DbCommandInterceptor
    {
        private int subscriptionReads;
        private int paymentOrderReads;
        private readonly List<IsolationLevel> readIsolationLevels = [];
        private IsolationLevel? subscriptionWriteIsolation;

        internal int SubscriptionReads => Volatile.Read(ref subscriptionReads);
        internal int PaymentOrderReads => Volatile.Read(ref paymentOrderReads);
        internal IReadOnlyList<IsolationLevel> ReadIsolationLevels
        {
            get { lock (readIsolationLevels) return readIsolationLevels.ToArray(); }
        }
        internal IsolationLevel? SubscriptionWriteIsolation => subscriptionWriteIsolation;

        internal void Reset()
        {
            Volatile.Write(ref subscriptionReads, 0);
            Volatile.Write(ref paymentOrderReads, 0);
            lock (readIsolationLevels) readIsolationLevels.Clear();
            subscriptionWriteIsolation = null;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var subscriptionRead = command.CommandText.Contains("\"Subscriptions\"", StringComparison.Ordinal) &&
                command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
            var paymentRead = command.CommandText.Contains("\"PaymentOrders\"", StringComparison.Ordinal) &&
                command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
            if (subscriptionRead) Interlocked.Increment(ref subscriptionReads);
            if (paymentRead) Interlocked.Increment(ref paymentOrderReads);
            if (subscriptionRead || paymentRead)
                lock (readIsolationLevels)
                    readIsolationLevels.Add(command.Transaction?.IsolationLevel ?? IsolationLevel.Unspecified);
            if (command.CommandText.Contains("UPDATE \"Subscriptions\"", StringComparison.Ordinal) ||
                command.CommandText.Contains("INSERT INTO \"SubscriptionAdminActions\"", StringComparison.Ordinal))
                subscriptionWriteIsolation = command.Transaction?.IsolationLevel;
            return ValueTask.FromResult(result);
        }
    }
}
