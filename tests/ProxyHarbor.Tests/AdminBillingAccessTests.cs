using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class AdminBillingAccessTests
{
    [Fact]
    public async Task PaymentOrderRegistryFiltersAndReturnsGlobalSummary()
    {
        await using var fixture = new Fixture();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "buyer", Email = "buyer@example.test" };
        fixture.Db.Users.Add(user);
        fixture.Db.PaymentOrders.AddRange(
            Order(user, "yookassa", PaymentStatuses.Paid, 49_900),
            Order(user, "stripe", PaymentStatuses.Pending, 99_900));
        await fixture.Db.SaveChangesAsync();
        var controller = new AdminPaymentOrdersController(fixture.Db);

        var ok = Assert.IsType<OkObjectResult>(await controller.List(
            status: PaymentStatuses.Paid, provider: "yookassa", query: "buyer@",
            token: CancellationToken.None));
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("yookassa", json);
        Assert.Contains("summary", json, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<OkObjectResult>(await controller.List(page: -1, pageSize: 999, token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.List(status: "unknown", token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.List(provider: "unknown", token: CancellationToken.None));
    }

    [Fact]
    public async Task AccessRulesValidateTargetsReloadImmediatelyAndCanBeDisabled()
    {
        await using var fixture = new Fixture();
        var admin = new ApplicationUser { Id = Guid.NewGuid(), UserName = "admin", Email = "admin@example.test" };
        fixture.Db.Users.Add(admin);
        await fixture.Db.SaveChangesAsync();
        var monitor = new ProxyAccessMonitor(fixture.Factory, NullLogger<ProxyAccessMonitor>.Instance);
        var controller = WithPrincipal(new AdminAccessController(fixture.Db, monitor), admin.Id);

        Assert.IsType<BadRequestResult>(await controller.CreateRule(new AccessRuleRequest
        { Kind = "wrong", Value = "203.0.113.1", Reason = "invalid" }, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.CreateRule(new AccessRuleRequest
        { Kind = AccessBlockKinds.Ip, Value = "not-an-ip", Reason = "invalid" }, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.CreateRule(new AccessRuleRequest
        { Kind = AccessBlockKinds.Cidr, Value = "not-a-cidr", Reason = "invalid" }, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.CreateRule(new AccessRuleRequest
        { Kind = AccessBlockKinds.User, Value = Guid.NewGuid().ToString(), Reason = "invalid" }, CancellationToken.None));
        var created = Assert.IsType<CreatedAtActionResult>(await controller.CreateRule(new AccessRuleRequest
        { Kind = AccessBlockKinds.Ip, Value = "203.0.113.10", Reason = "Слишком много запросов" }, CancellationToken.None));
        Assert.NotNull(created.Value);
        Assert.True(monitor.IsBlocked(System.Net.IPAddress.Parse("203.0.113.10"), null));
        Assert.IsType<CreatedAtActionResult>(await controller.CreateRule(new AccessRuleRequest
        { Kind = AccessBlockKinds.Cidr, Value = "198.51.100.0/24", Reason = "Подсеть перегружает экспорт" }, CancellationToken.None));
        Assert.IsType<CreatedAtActionResult>(await controller.CreateRule(new AccessRuleRequest
        { Kind = AccessBlockKinds.User, Value = admin.Id.ToString(), Reason = "Аккаунт временно ограничен" }, CancellationToken.None));
        Assert.True(monitor.IsBlocked(System.Net.IPAddress.Parse("198.51.100.44"), null));
        Assert.True(monitor.IsBlocked(null, admin.Id));
        Assert.False(monitor.IsBlocked(System.Net.IPAddress.Parse("192.0.2.1"), null));

        var rule = await fixture.Db.AccessBlockRules.SingleAsync(x => x.Kind == AccessBlockKinds.Ip);
        Assert.IsType<NoContentResult>(await controller.ToggleRule(rule.Id,
            new ToggleAccessRuleRequest { Enabled = false }, CancellationToken.None));
        Assert.False(monitor.IsBlocked(System.Net.IPAddress.Parse("203.0.113.10"), null));
        Assert.IsType<NotFoundResult>(await controller.ToggleRule(Guid.NewGuid(),
            new ToggleAccessRuleRequest { Enabled = false }, CancellationToken.None));
    }

    [Fact]
    public async Task AccessRegistryAggregatesTrafficAndReturnsRules()
    {
        await using var fixture = new Fixture();
        var admin = new ApplicationUser { Id = Guid.NewGuid(), UserName = "admin", Email = "admin@example.test" };
        fixture.Db.Users.Add(admin);
        fixture.Db.ProxyAccessBuckets.AddRange(
            Bucket("198.51.100.5", 12, 120), Bucket("198.51.100.5", 8, 80), Bucket("198.51.100.7", 2, 10));
        await fixture.Db.SaveChangesAsync();
        var monitor = new ProxyAccessMonitor(fixture.Factory, NullLogger<ProxyAccessMonitor>.Instance);
        var controller = WithPrincipal(new AdminAccessController(fixture.Db, monitor), admin.Id);

        var result = Assert.IsType<OkObjectResult>(await controller.List(query: "198.51.100.5", token: CancellationToken.None));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"requests\":20", json);
        Assert.Contains("\"proxyItems\":200", json);
    }

    [Fact]
    public async Task AccessMiddlewareSkipsOtherPathsRecordsAllowedAndRejectsBlockedClients()
    {
        await using var fixture = new Fixture();
        var admin = new ApplicationUser { Id = Guid.NewGuid(), UserName = "admin", Email = "admin@example.test" };
        fixture.Db.Users.Add(admin);
        fixture.Db.AccessBlockRules.Add(new AccessBlockRule
        {
            Kind = AccessBlockKinds.Ip, Value = "203.0.113.5", Reason = "test",
            AdministratorId = admin.Id
        });
        await fixture.Db.SaveChangesAsync();
        var monitor = new ProxyAccessMonitor(fixture.Factory, NullLogger<ProxyAccessMonitor>.Instance);
        await monitor.ReloadRulesAsync();
        var nextCalls = 0;
        var middleware = new ProxyAccessMiddleware(context => { nextCalls++; context.Response.ContentLength = 25; context.Items["ProxyHarbor.ProxyItems"] = 3; return Task.CompletedTask; });

        var other = Context("/health/live", "192.0.2.1");
        await middleware.InvokeAsync(other, monitor);
        var allowed = Context("/api/v1/proxies", "192.0.2.2");
        await middleware.InvokeAsync(allowed, monitor);
        var export = Context("/api/v1/export/txt", "192.0.2.3");
        await middleware.InvokeAsync(export, monitor);
        var blocked = Context("/api/v1/proxies/seek", "203.0.113.5");
        await middleware.InvokeAsync(blocked, monitor);

        Assert.Equal(3, nextCalls);
        Assert.Equal(StatusCodes.Status403Forbidden, blocked.Response.StatusCode);
        Assert.True(blocked.Response.Body.Length > 0);
    }

    private static PaymentOrder Order(ApplicationUser user, string provider, string status, long amount) => new()
    {
        User = user, UserId = user.Id, ProductCode = "pro-30", Plan = SubscriptionPlans.Pro,
        Provider = provider, AmountMinor = amount, Currency = "RUB", DurationDays = 30,
        Status = status, ProviderPaymentId = Guid.NewGuid().ToString("N")
    };

    private static ProxyAccessBucket Bucket(string ip, int requests, long items) => new()
    {
        BucketStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), IpAddress = ip,
        Endpoint = "catalog", Requests = requests, ProxyItems = items, LastSeenAt = DateTimeOffset.UtcNow
    };

    private static DefaultHttpContext Context(string path, string ip)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static T WithPrincipal<T>(T controller, Guid administratorId) where T : ControllerBase
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, administratorId.ToString())], "test");
        controller.ControllerContext = new ControllerContext
        { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return controller;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DbContextOptions<ProxyHarborDbContext> options =
            new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase($"admin-access-{Guid.NewGuid():N}").Options;
        public Fixture() { Db = new ProxyHarborDbContext(options); Factory = new ContextFactory(options); }
        public ProxyHarborDbContext Db { get; }
        public IDbContextFactory<ProxyHarborDbContext> Factory { get; }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); }
    }

    private sealed class ContextFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
    }
}
