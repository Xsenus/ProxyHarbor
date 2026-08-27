using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет Identity, профиль и entitlement-права на реальном EF Identity store.</summary>
public sealed class IdentityAccountIntegrationTests
{
    private const string AdminPassword = "Bootstrap-admin-42!";

    [Fact]
    public async Task SeederCreatesIdempotentAdministratorWithUnlimitedSubscription()
    {
        await using var fixture = CreateFixture();
        var configuration = AdminConfiguration();

        await IdentitySeeder.InitializeAsync(fixture.Services, configuration);
        await IdentitySeeder.InitializeAsync(fixture.Services, configuration);

        var users = fixture.Get<UserManager<ApplicationUser>>();
        var roles = fixture.Get<RoleManager<IdentityRole<Guid>>>();
        var db = fixture.Get<ProxyHarborDbContext>();
        var administrator = await users.FindByNameAsync("admin");
        Assert.NotNull(administrator);
        Assert.True(administrator.IsActive);
        Assert.Equal("admin@example.com", administrator.Email);
        Assert.All(UserRoles.All, role => Assert.True(roles.RoleExistsAsync(role).GetAwaiter().GetResult()));
        Assert.All(UserRoles.All, role => Assert.Contains(role, users.GetRolesAsync(administrator).GetAwaiter().GetResult()));
        var subscription = await db.Subscriptions.SingleAsync(x => x.UserId == administrator.Id);
        Assert.Equal(SubscriptionPlans.Unlimited, subscription.Plan);
        Assert.Equal(SubscriptionStatuses.Active, subscription.Status);
    }

    [Fact]
    public async Task RegisterEmailLoginRecoveryAndSessionUseOneIdentityAccount()
    {
        await using var fixture = CreateFixture();
        await fixture.CreateRolesAsync();
        var sender = new RecordingEmailSender();
        var controller = fixture.AuthController(sender);

        var registered = await controller.Register(new RegisterAccountRequest
        {
            Username = "proxy.user",
            Email = "user@example.com",
            DisplayName = "Proxy User",
            Password = "Initial-user-42!"
        });

        var created = Assert.IsType<ObjectResult>(registered);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var users = fixture.Get<UserManager<ApplicationUser>>();
        var user = await users.FindByNameAsync("proxy.user");
        Assert.NotNull(user);
        Assert.True(await users.IsInRoleAsync(user, UserRoles.User));
        Assert.Equal(SubscriptionPlans.Free,
            (await fixture.Get<ProxyHarborDbContext>().Subscriptions.SingleAsync()).Plan);

        fixture.HttpContext.Response.Headers.Clear();
        var emailLogin = await controller.Login(new AccountLoginRequest
        {
            Username = "user@example.com",
            Password = "Initial-user-42!",
            RememberMe = true
        });
        Assert.IsType<OkObjectResult>(emailLogin);
        Assert.NotNull(user.LastLoginAt);
        Assert.Contains("expires=", fixture.HttpContext.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);

        fixture.HttpContext.Response.Headers.Clear();
        Assert.IsType<OkObjectResult>(await controller.Login(new AccountLoginRequest
        {
            Username = "proxy.user",
            Password = "Initial-user-42!",
            RememberMe = false
        }));
        Assert.DoesNotContain("expires=", fixture.HttpContext.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);

        var invalidLogin = await controller.Login(new AccountLoginRequest
        {
            Username = "proxy.user",
            Password = "wrong"
        });
        Assert.IsType<UnauthorizedObjectResult>(invalidLogin);
        user.IsActive = false;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
        Assert.IsType<UnauthorizedObjectResult>(await controller.Login(new AccountLoginRequest
        {
            Username = "proxy.user",
            Password = "Initial-user-42!"
        }));
        user.IsActive = true;
        Assert.True((await users.UpdateAsync(user)).Succeeded);

        var recovery = await controller.ForgotPassword(
            new ForgotPasswordRequest { Email = "user@example.com" }, CancellationToken.None);
        Assert.IsType<AcceptedResult>(recovery);
        Assert.NotNull(sender.Token);
        var reset = await controller.ResetPassword(new ResetPasswordRequest
        {
            Email = "user@example.com",
            Token = sender.Token!,
            NewPassword = "Changed-user-42!"
        });
        Assert.IsType<NoContentResult>(reset);
        Assert.True((await users.FindByEmailAsync("user@example.com"))!.EmailConfirmed);
        Assert.True(await users.CheckPasswordAsync(user, "Changed-user-42!"));

        await fixture.SetPrincipalAsync(user);
        Assert.IsType<OkObjectResult>(await controller.Session());
        Assert.IsType<NoContentResult>(await controller.Logout());
    }

    [Fact]
    public async Task ReferralRegistrationCreatesAuditedRewardAndExtendsReferrer()
    {
        await using var fixture = CreateFixture();
        await fixture.CreateRolesAsync();
        var users = fixture.Get<UserManager<ApplicationUser>>();
        var db = fixture.Get<ProxyHarborDbContext>();
        var referrer = new ApplicationUser
        {
            UserName = "referrer",
            Email = "referrer@example.com",
            ReferralCode = "abc123def456"
        };
        Assert.True((await users.CreateAsync(referrer, "Initial-user-42!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(referrer, UserRoles.User)).Succeeded);
        db.Subscriptions.Add(new UserSubscription { UserId = referrer.Id });
        await db.SaveChangesAsync();

        var response = await fixture.AuthController(new RecordingEmailSender()).Register(new RegisterAccountRequest
        {
            Username = "invited.user",
            Email = "invited@example.com",
            Password = "Initial-user-42!",
            ReferralCode = referrer.ReferralCode
        });

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(response).StatusCode);
        var relationship = await db.ReferralRelationships.Include(x => x.Rewards).SingleAsync();
        Assert.Equal(referrer.Id, relationship.ReferrerUserId);
        Assert.Equal(1, relationship.Slot);
        var reward = Assert.Single(relationship.Rewards);
        Assert.Equal(ReferralRewardKinds.Signup, reward.Kind);
        Assert.Equal(1, reward.DaysGranted);
        var subscription = await db.Subscriptions.SingleAsync(x => x.UserId == referrer.Id);
        Assert.Equal(SubscriptionPlans.Unlimited, subscription.Plan);
        Assert.InRange(subscription.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));
        Assert.True(await users.IsInRoleAsync(referrer, UserRoles.Subscriber));
    }

    [Fact]
    public async Task ReferralPurchaseRewardsFollowPolicyAndAreIdempotent()
    {
        await using var fixture = CreateFixture();
        await fixture.CreateRolesAsync();
        var users = fixture.Get<UserManager<ApplicationUser>>();
        var db = fixture.Get<ProxyHarborDbContext>();
        var referrer = new ApplicationUser { UserName = "owner", Email = "owner@example.com", ReferralCode = "111111111111" };
        var referred = new ApplicationUser { UserName = "buyer", Email = "buyer@example.com", ReferralCode = "222222222222" };
        Assert.True((await users.CreateAsync(referrer, "Initial-user-42!")).Succeeded);
        Assert.True((await users.CreateAsync(referred, "Initial-user-42!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(referrer, UserRoles.User)).Succeeded);
        Assert.True((await users.AddToRoleAsync(referred, UserRoles.User)).Succeeded);
        db.Subscriptions.AddRange(new UserSubscription { UserId = referrer.Id }, new UserSubscription { UserId = referred.Id });
        db.ReferralRelationships.Add(new ReferralRelationship { ReferrerUserId = referrer.Id, ReferredUserId = referred.Id, Slot = 1 });
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var expected = new Dictionary<int, int> { [30] = 1, [90] = 7, [180] = 30, [365] = 90 };

        foreach (var pair in expected)
        {
            var order = new PaymentOrder { UserId = referred.Id, DurationDays = pair.Key, ProductCode = $"p-{pair.Key}", Provider = "test", Status = PaymentStatuses.Paid };
            db.PaymentOrders.Add(order);
            await db.SaveChangesAsync();
            Assert.Equal(pair.Value, await ReferralRewards.GrantForPurchaseAsync(db, users, order, now, CancellationToken.None));
            Assert.Equal(0, await ReferralRewards.GrantForPurchaseAsync(db, users, order, now, CancellationToken.None));
            await db.SaveChangesAsync();
        }

        Assert.Equal(128, await db.ReferralRewards.Where(x => x.Kind == ReferralRewardKinds.Purchase).SumAsync(x => x.DaysGranted));
        var subscription = await db.Subscriptions.SingleAsync(x => x.UserId == referrer.Id);
        Assert.InRange(subscription.ExpiresAt!.Value, now.AddDays(127).AddHours(23), now.AddDays(128).AddHours(1));
    }

    [Fact]
    public async Task RecoveryIsNeutralAndFailsClosedWithoutSmtp()
    {
        await using var fixture = CreateFixture();
        await fixture.CreateRolesAsync();
        var configured = fixture.AuthController(new RecordingEmailSender());
        var unknown = await configured.ForgotPassword(
            new ForgotPasswordRequest { Email = "missing@example.com" }, CancellationToken.None);
        Assert.IsType<AcceptedResult>(unknown);

        var unavailable = fixture.AuthController(new RecordingEmailSender { IsConfigured = false });
        var response = await unavailable.ForgotPassword(
            new ForgotPasswordRequest { Email = "missing@example.com" }, CancellationToken.None);
        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.IsType<BadRequestObjectResult>(await configured.ResetPassword(new ResetPasswordRequest
        {
            Email = "missing@example.com",
            Token = "missing-token",
            NewPassword = "Changed-user-42!"
        }));
        Assert.IsType<UnauthorizedResult>(await configured.Session());
    }

    [Fact]
    public async Task ProfileCanBeReadUpdatedAndSecuredWithCurrentPassword()
    {
        await using var fixture = CreateFixture();
        await fixture.CreateRolesAsync();
        var users = fixture.Get<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "member", Email = "member@example.com" };
        Assert.True((await users.CreateAsync(user, "Initial-user-42!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, UserRoles.User)).Succeeded);
        fixture.Get<ProxyHarborDbContext>().Subscriptions.Add(new UserSubscription { UserId = user.Id });
        await fixture.Get<ProxyHarborDbContext>().SaveChangesAsync();
        await fixture.SetPrincipalAsync(user);
        var controller = fixture.AccountController();

        Assert.IsType<OkObjectResult>(await controller.Profile());
        Assert.IsType<NoContentResult>(await controller.UpdateProfile(
            new UpdateProfileRequest { DisplayName = "  Member Name  " }));
        Assert.Equal("Member Name", (await users.FindByIdAsync(user.Id.ToString()))!.DisplayName);
        Assert.IsType<BadRequestObjectResult>(await controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "wrong",
            NewPassword = "Changed-user-42!"
        }));
        Assert.IsType<NoContentResult>(await controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "Initial-user-42!",
            NewPassword = "Changed-user-42!"
        }));
        Assert.True(await users.CheckPasswordAsync(user, "Changed-user-42!"));
        user.IsActive = false;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
        Assert.IsType<UnauthorizedResult>(await controller.Profile());
        Assert.IsType<UnauthorizedResult>(await controller.UpdateProfile(new UpdateProfileRequest()));
        Assert.IsType<UnauthorizedResult>(await controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "Changed-user-42!",
            NewPassword = "Another-user-42!"
        }));
    }

    [Fact]
    public async Task PaidUserCanIssueAuthenticateAndRevokeOneTimeApiToken()
    {
        await using var fixture = CreateFixture();
        await fixture.CreateRolesAsync();
        var users = fixture.Get<UserManager<ApplicationUser>>();
        var db = fixture.Get<ProxyHarborDbContext>();
        var user = new ApplicationUser { UserName = "api-client", Email = "api-client@example.test", IsActive = true };
        Assert.True((await users.CreateAsync(user, "Initial-user-42!")).Succeeded);
        Assert.True((await users.AddToRolesAsync(user, [UserRoles.User, UserRoles.Subscriber])).Succeeded);
        db.Subscriptions.Add(new UserSubscription
        {
            UserId = user.Id,
            Plan = SubscriptionPlans.Unlimited,
            Status = SubscriptionStatuses.Active,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();
        var service = new UserApiTokenService(db, users);

        var issued = await service.IssueAsync(user.Id, "CI", CancellationToken.None);
        Assert.StartsWith($"ph_live_{issued.Id:N}.", issued.Token, StringComparison.Ordinal);
        var stored = await db.UserApiTokens.AsNoTracking().SingleAsync();
        Assert.Equal(32, stored.SecretHash.Length);
        Assert.DoesNotContain(issued.Token, Convert.ToHexString(stored.SecretHash), StringComparison.Ordinal);
        Assert.NotNull(await service.AuthenticateAsync(issued.Token, CancellationToken.None));
        Assert.Null(await service.AuthenticateAsync(issued.Token + "x", CancellationToken.None));

        var requestContext = new DefaultHttpContext();
        requestContext.Request.Method = HttpMethods.Get;
        requestContext.Request.Path = "/api/v1/proxies";
        requestContext.Request.QueryString = new QueryString("?protocol=Http&apiKey=must-not-be-stored");
        requestContext.Request.Headers.Authorization = $"Bearer {issued.Token}";
        requestContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        var middleware = new UserApiTokenMiddleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Items["ProxyHarbor.ProxyItems"] = 17;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(requestContext, service, db, fixture.Get<ILogger<UserApiTokenMiddleware>>());
        db.ChangeTracker.Clear();
        var audit = await db.UserApiTokenRequests.AsNoTracking().SingleAsync();
        Assert.Equal(issued.Id, audit.UserApiTokenId);
        Assert.Equal(user.Id, audit.UserId);
        Assert.Equal("203.0.113.42", audit.IpAddress);
        Assert.Equal("GET", audit.Method);
        Assert.Equal("/api/v1/proxies", audit.Path);
        Assert.Contains("protocol=Http", audit.Query, StringComparison.Ordinal);
        Assert.Contains("apiKey=%5Bhidden%5D", audit.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-stored", audit.Query, StringComparison.Ordinal);
        Assert.Equal(17, audit.ItemCount);
        Assert.Equal(StatusCodes.Status200OK, audit.StatusCode);

        Assert.True(await service.RevokeAsync(user.Id, issued.Id, CancellationToken.None));
        Assert.Empty(await service.ListAsync(user.Id, CancellationToken.None));
        Assert.Null(await service.AuthenticateAsync(issued.Token, CancellationToken.None));
    }

    [Fact]
    public async Task AdministratorManagesUsersButCannotDisableLastAdministrator()
    {
        await using var fixture = CreateFixture();
        await IdentitySeeder.InitializeAsync(fixture.Services, AdminConfiguration());
        var users = fixture.Get<UserManager<ApplicationUser>>();
        var member = new ApplicationUser { UserName = "member", Email = "member@example.com" };
        Assert.True((await users.CreateAsync(member, "Initial-user-42!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(member, UserRoles.User)).Succeeded);
        var controller = fixture.AdminUsersController();

        var list = await controller.List(0, 500);
        var page = Assert.IsType<OkObjectResult>(list.Result);
        var firstPage = Assert.IsType<PagedResult<AdminUserResponse>>(page.Value);
        Assert.Equal(2, firstPage.Total);
        Assert.Equal(100, firstPage.PageSize);
        var searched = await controller.List(1, 10, "member@example", "active", SubscriptionPlans.Free);
        var searchPage = Assert.IsType<PagedResult<AdminUserResponse>>(
            Assert.IsType<OkObjectResult>(searched.Result).Value);
        Assert.Single(searchPage.Items);
        Assert.Equal("member", searchPage.Items[0].UserName);
        Assert.IsType<BadRequestObjectResult>(await controller.Update(member.Id, new UpdateUserAccessRequest
        {
            IsActive = true,
            Roles = ["Unknown"],
            Plan = SubscriptionPlans.Free,
            Status = SubscriptionStatuses.Active
        }));
        Assert.IsType<BadRequestObjectResult>(await controller.Update(member.Id, new UpdateUserAccessRequest
        {
            IsActive = true,
            Roles = [UserRoles.User],
            Plan = "unknown",
            Status = "unknown"
        }));
        Assert.IsType<NotFoundResult>(await controller.Update(Guid.NewGuid(), ValidAccess()));

        var administrator = await users.FindByNameAsync("admin");
        Assert.IsType<ConflictObjectResult>(await controller.Update(administrator!.Id, new UpdateUserAccessRequest
        {
            IsActive = false,
            Roles = [UserRoles.User, UserRoles.Administrator],
            Plan = SubscriptionPlans.Unlimited,
            Status = SubscriptionStatuses.Active
        }));

        var update = ValidAccess();
        update.Roles = [UserRoles.User, UserRoles.Subscriber];
        update.Plan = SubscriptionPlans.Pro;
        update.ExpiresAt = DateTimeOffset.UtcNow.AddMonths(1);
        Assert.IsType<NoContentResult>(await controller.Update(member.Id, update));
        Assert.True(await users.IsInRoleAsync(member, UserRoles.Subscriber));
        Assert.Equal(SubscriptionPlans.Pro,
            (await fixture.Get<ProxyHarborDbContext>().Subscriptions.SingleAsync(x => x.UserId == member.Id)).Plan);
        var paid = await controller.List(1, 10, null, "active", SubscriptionPlans.Pro);
        Assert.Single(Assert.IsType<PagedResult<AdminUserResponse>>(
            Assert.IsType<OkObjectResult>(paid.Result).Value).Items);

        var secondAdministrator = new ApplicationUser { UserName = "admin.two", Email = "admin.two@example.com" };
        Assert.True((await users.CreateAsync(secondAdministrator, "Initial-user-42!")).Succeeded);
        Assert.True((await users.AddToRolesAsync(secondAdministrator,
            [UserRoles.User, UserRoles.Administrator])).Succeeded);
        Assert.IsType<NoContentResult>(await controller.Update(administrator.Id, new UpdateUserAccessRequest
        {
            IsActive = false,
            Roles = [UserRoles.User],
            Plan = SubscriptionPlans.Unlimited,
            Status = SubscriptionStatuses.Canceled
        }));
    }

    private static UpdateUserAccessRequest ValidAccess() => new()
    {
        IsActive = true,
        Roles = [UserRoles.User],
        Plan = SubscriptionPlans.Free,
        Status = SubscriptionStatuses.Active
    };

    private static IConfiguration AdminConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:AdminUsername"] = "admin",
            ["Security:AdminEmail"] = "admin@example.com",
            ["Security:AdminPassword"] = AdminPassword
        }).Build();

    private static IdentityFixture CreateFixture() => new();

    private sealed class RecordingEmailSender : IAccountEmailSender
    {
        public bool IsConfigured { get; set; } = true;
        public string? Token { get; private set; }
        public Task SendPasswordResetAsync(string email, string token, string language, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("user@example.com", email);
            Token = token;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SubscriptionAdminExtendsSuspendsAuditsAndSynchronizesSubscriberRole()
    {
        await using var fixture = new IdentityFixture();
        await fixture.CreateRolesAsync();
        var users = fixture.Get<UserManager<ApplicationUser>>();
        var admin = new ApplicationUser { UserName = "billing-admin", Email = "billing-admin@example.test", EmailConfirmed = true };
        var client = new ApplicationUser { UserName = "paid-client", Email = "paid-client@example.test", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(admin, "Admin-password-123!")).Succeeded);
        Assert.True((await users.CreateAsync(client, "Client-password-123!")).Succeeded);
        Assert.True((await users.AddToRolesAsync(admin, [UserRoles.User, UserRoles.Administrator])).Succeeded);
        Assert.True((await users.AddToRolesAsync(client, [UserRoles.User, UserRoles.Subscriber])).Succeeded);
        var db = fixture.Get<ProxyHarborDbContext>();
        var subscription = new UserSubscription
        {
            UserId = client.Id,
            Plan = SubscriptionPlans.Pro,
            Status = SubscriptionStatuses.Active,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(10)
        };
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        await fixture.SetPrincipalAsync(admin);

        var result = await fixture.AdminSubscriptionsController().Update(subscription.Id, new UpdateSubscriptionRequest
        {
            Plan = SubscriptionPlans.Pro,
            Status = SubscriptionStatuses.Suspended,
            ExtensionDays = 30,
            Reason = "Ручная проверка злоупотребления"
        }, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var saved = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == subscription.Id);
        Assert.Equal(SubscriptionStatuses.Suspended, saved.Status);
        Assert.True(saved.ExpiresAt > DateTimeOffset.UtcNow.AddDays(39));
        var audit = await db.SubscriptionAdminActions.AsNoTracking().SingleAsync();
        Assert.Equal("extend", audit.Action);
        Assert.Equal(admin.Id, audit.AdministratorId);
        Assert.False(await users.IsInRoleAsync(client, UserRoles.Subscriber));

        var listResult = Assert.IsType<OkObjectResult>(await fixture.AdminSubscriptionsController().List(
            page: 1, pageSize: 10, status: SubscriptionStatuses.Suspended,
            plan: SubscriptionPlans.Pro, query: "paid-client", token: CancellationToken.None));
        Assert.Contains("paid-client", System.Text.Json.JsonSerializer.Serialize(listResult.Value));
        Assert.IsType<BadRequestObjectResult>(await fixture.AdminSubscriptionsController().Update(
            subscription.Id, new UpdateSubscriptionRequest { Plan = "unknown", Status = "unknown" },
            CancellationToken.None));
        Assert.IsType<NotFoundResult>(await fixture.AdminSubscriptionsController().Update(
            Guid.NewGuid(), new UpdateSubscriptionRequest
            { Plan = SubscriptionPlans.Free, Status = SubscriptionStatuses.Active }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await fixture.AdminSubscriptionsController().Update(
            subscription.Id, new UpdateSubscriptionRequest
            {
                Plan = SubscriptionPlans.Pro,
                Status = SubscriptionStatuses.Active,
                ExpiresAt = subscription.StartedAt.AddDays(-1)
            }, CancellationToken.None));
    }

    private sealed class IdentityFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        public IServiceProvider Services => _provider;
        public DefaultHttpContext HttpContext { get; }

        public IdentityFixture()
        {
            var services = new ServiceCollection();
            var databaseRoot = new InMemoryDatabaseRoot();
            var databaseName = $"identity-{Guid.NewGuid():N}";
            services.AddLogging();
            services.AddControllers();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddHttpContextAccessor();
            services.AddDbContext<ProxyHarborDbContext>(options => options
                .UseInMemoryDatabase(databaseName, databaseRoot)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.User.RequireUniqueEmail = true;
                    options.Password.RequiredLength = 12;
                    options.Password.RequiredUniqueChars = 4;
                    options.Password.RequireDigit = true;
                    options.Password.RequireLowercase = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = true;
                })
                .AddRoles<IdentityRole<Guid>>()
                .AddSignInManager()
                .AddEntityFrameworkStores<ProxyHarborDbContext>()
                .AddDefaultTokenProviders();
            services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
            services.AddAuthorization();
            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateAsyncScope();
            HttpContext = new DefaultHttpContext { RequestServices = _scope.ServiceProvider };
            _scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = HttpContext;
        }

        public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

        public async Task CreateRolesAsync()
        {
            var roles = Get<RoleManager<IdentityRole<Guid>>>();
            foreach (var role in UserRoles.All)
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);
        }

        public async Task SetPrincipalAsync(ApplicationUser user)
        {
            var factory = Get<IUserClaimsPrincipalFactory<ApplicationUser>>();
            HttpContext.User = await factory.CreateAsync(user);
        }

        public AuthController AuthController(IAccountEmailSender sender) => WithContext(new AuthController(
            Get<UserManager<ApplicationUser>>(), Get<SignInManager<ApplicationUser>>(),
            Get<ProxyHarborDbContext>(), sender,
            new UserApiTokenService(Get<ProxyHarborDbContext>(), Get<UserManager<ApplicationUser>>()),
            Get<ILogger<AuthController>>()));

        public AccountController AccountController() => WithContext(new AccountController(
            Get<UserManager<ApplicationUser>>(), Get<SignInManager<ApplicationUser>>(), Get<ProxyHarborDbContext>(),
            new UserApiTokenService(Get<ProxyHarborDbContext>(), Get<UserManager<ApplicationUser>>())));

        public AdminUsersController AdminUsersController() => WithContext(new AdminUsersController(
            Get<UserManager<ApplicationUser>>(), Get<ProxyHarborDbContext>()));

        public AdminSubscriptionsController AdminSubscriptionsController() => WithContext(new AdminSubscriptionsController(
            Get<ProxyHarborDbContext>(), Get<UserManager<ApplicationUser>>()));

        private T WithContext<T>(T controller) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = HttpContext };
            return controller;
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
