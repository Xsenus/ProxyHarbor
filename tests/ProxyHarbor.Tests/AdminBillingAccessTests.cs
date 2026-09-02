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
    public async Task AccessFlushKeepsVisitQueueIndependentFromCounterPersistenceFailure()
    {
        await using var fixture = new Fixture();
        var monitor = new ProxyAccessMonitor(fixture.Factory, NullLogger<ProxyAccessMonitor>.Instance);

        // Пустой periodic tick не должен открывать БД или создавать служебные строки.
        await monitor.FlushOnceAsync(CancellationToken.None);
        var context = Context("/api/v1/telemetry/visit", "192.0.2.55");
        monitor.RecordSiteVisit(context, "account");

        // Unit fixture использует SQLite: PostgreSQL COPY ожидаемо возвращает bucket
        // в память, но независимый visit queue всё равно обязан сохраниться.
        await monitor.FlushOnceAsync(CancellationToken.None);
        var visit = Assert.Single(await fixture.Db.SiteVisitLogs.AsNoTracking().ToArrayAsync());
        Assert.Equal("192.0.2.55", visit.IpAddress);
        Assert.Equal("account", visit.Page);

        // Повторный flush повторяет только возвращённый counter и не дублирует visit.
        await monitor.FlushOnceAsync(CancellationToken.None);
        Assert.Single(await fixture.Db.SiteVisitLogs.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task SiteVisitBufferIsBoundedWithoutBlockingRequestProcessing()
    {
        await using var fixture = new Fixture();
        var monitor = new ProxyAccessMonitor(
            fixture.Factory,
            NullLogger<ProxyAccessMonitor>.Instance,
            maximumBufferedSiteVisits: 3);
        var context = Context("/api/v1/telemetry/visit", "192.0.2.56");

        for (var index = 0; index < 5; index++) monitor.RecordSiteVisit(context, "account");

        Assert.Equal(3, monitor.BufferedSiteVisitCount);
        Assert.Equal(2, monitor.DroppedSiteVisitCount);
        await monitor.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(0, monitor.BufferedSiteVisitCount);
        Assert.Equal(0, monitor.DroppedSiteVisitCount);
        Assert.Equal(3, await fixture.Db.SiteVisitLogs.CountAsync());
    }

    [Fact]
    public async Task AccessCounterBufferRejectsOnlyNewBucketsAtCapacity()
    {
        await using var fixture = new Fixture();
        var monitor = new ProxyAccessMonitor(
            fixture.Factory,
            NullLogger<ProxyAccessMonitor>.Instance,
            maximumBufferedAccessCounters: 3);

        for (var index = 1; index <= 5; index++)
            monitor.Record(Context("/api/v1/proxies", $"192.0.2.{index}"), "catalog", blocked: false);

        // Повтор уже принятого ключа продолжает агрегироваться и не требует нового слота.
        monitor.Record(Context("/api/v1/proxies", "192.0.2.1"), "catalog", blocked: true);

        Assert.Equal(3, monitor.BufferedAccessCounterCount);
        Assert.Equal(2, monitor.DroppedAccessCounterCount);
    }

    [Fact]
    public async Task AccessCounterCapacityRemainsStrictUnderConcurrentWriters()
    {
        await using var fixture = new Fixture();
        const int capacity = 64;
        const int attempts = 1_000;
        var monitor = new ProxyAccessMonitor(
            fixture.Factory,
            NullLogger<ProxyAccessMonitor>.Instance,
            maximumBufferedAccessCounters: capacity);

        Parallel.For(0, attempts, index =>
        {
            var address = $"198.51.{index / 250}.{index % 250 + 1}";
            monitor.Record(Context("/api/v1/proxies", address), "catalog", blocked: false);
        });

        Assert.Equal(capacity, monitor.BufferedAccessCounterCount);
        Assert.Equal(attempts - capacity, monitor.DroppedAccessCounterCount);
    }

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
    public async Task EmptySubscriptionRegistryReturnsZeroSummary()
    {
        await using var fixture = new Fixture();
        var result = Assert.IsType<OkObjectResult>(await new AdminSubscriptionsController(fixture.Db, null!).List(
            token: CancellationToken.None));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":0", json);
        Assert.Contains("\"active\":0", json);
        Assert.Contains("\"trialing\":0", json);
        Assert.Contains("\"suspended\":0", json);
        Assert.Contains("\"expiringSoon\":0", json);
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
    public async Task AccessRegistriesReturnZeroSummariesWhenNoTrafficExists()
    {
        await using var fixture = new Fixture();
        var controller = new AdminAccessController(fixture.Db,
            new ProxyAccessMonitor(fixture.Factory, NullLogger<ProxyAccessMonitor>.Instance));

        var traffic = Assert.IsType<OkObjectResult>(await controller.List(token: CancellationToken.None));
        var trafficJson = System.Text.Json.JsonSerializer.Serialize(traffic.Value);
        Assert.Contains("\"total\":0", trafficJson);
        Assert.Contains("\"requests\":0", trafficJson);
        Assert.Contains("\"proxyItems\":0", trafficJson);

        var visitors = Assert.IsType<OkObjectResult>(await controller.Visitors(token: CancellationToken.None));
        var visitorJson = System.Text.Json.JsonSerializer.Serialize(visitors.Value);
        Assert.Contains("\"total\":0", visitorJson);
        Assert.Contains("\"pageViews\":0", visitorJson);
        Assert.Contains("\"uniqueVisitors\":0", visitorJson);
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
        foreach (var sort in new[] { "ip", "requests", "proxyItems", "bytesSent", "lastSeen" })
            foreach (var order in new[] { "asc", "desc" })
                Assert.IsType<OkObjectResult>(await controller.List(sort: sort, order: order, token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.List(sort: "unknown", token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.List(order: "sideways", token: CancellationToken.None));
    }

    [Fact]
    public async Task AccessAndVisitorRegistriesDeduplicateIpAcrossAuthenticationStates()
    {
        await using var fixture = new Fixture();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "member", Email = "member@example.test" };
        fixture.Db.Users.Add(user);
        fixture.Db.ProxyAccessBuckets.AddRange(
            Bucket("203.0.113.40", 4, 40),
            Bucket("203.0.113.40", 6, 60, userId: user.Id),
            Bucket("203.0.113.40", 2, 0, "page:home"),
            Bucket("203.0.113.40", 3, 0, "page:account", user.Id));
        await fixture.Db.SaveChangesAsync();
        var controller = WithPrincipal(new AdminAccessController(fixture.Db,
            new ProxyAccessMonitor(fixture.Factory, NullLogger<ProxyAccessMonitor>.Instance)), user.Id);

        var traffic = Assert.IsType<OkObjectResult>(await controller.List(token: CancellationToken.None));
        var trafficJson = System.Text.Json.JsonSerializer.Serialize(traffic.Value);
        Assert.Contains("\"total\":1", trafficJson);
        Assert.Contains("\"requests\":10", trafficJson);
        Assert.Contains("member@example.test", trafficJson);

        var visitors = Assert.IsType<OkObjectResult>(await controller.Visitors(token: CancellationToken.None));
        var visitorJson = System.Text.Json.JsonSerializer.Serialize(visitors.Value);
        Assert.Contains("\"total\":1", visitorJson);
        Assert.Contains("\"PageViews\":5", visitorJson);
        Assert.Contains("member@example.test", visitorJson);
        foreach (var sort in new[] { "ip", "pageViews", "pages", "firstSeen", "lastSeen" })
            foreach (var order in new[] { "asc", "desc" })
                Assert.IsType<OkObjectResult>(await controller.Visitors(sort: sort, order: order, token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.Visitors(sort: "unknown", token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.Visitors(order: "sideways", token: CancellationToken.None));
    }

    [Fact]
    public async Task VisitHistoryAndRulesAreServerPagedAndSortable()
    {
        await using var fixture = new Fixture();
        var admin = new ApplicationUser { Id = Guid.NewGuid(), UserName = "admin", Email = "admin@example.test" };
        fixture.Db.Users.Add(admin);
        fixture.Db.SiteVisitLogs.AddRange(
            new SiteVisitLog { IpAddress = "198.51.100.2", Page = "home", VisitedAt = DateTimeOffset.UtcNow.AddMinutes(-2) },
            new SiteVisitLog { IpAddress = "198.51.100.1", UserId = admin.Id, Page = "admin-access", VisitedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        fixture.Db.AccessBlockRules.Add(new AccessBlockRule
        {
            Kind = AccessBlockKinds.Ip,
            Value = "198.51.100.9",
            Reason = "test rule",
            AdministratorId = admin.Id
        });
        await fixture.Db.SaveChangesAsync();
        var controller = WithPrincipal(new AdminAccessController(fixture.Db,
            new ProxyAccessMonitor(fixture.Factory, NullLogger<ProxyAccessMonitor>.Instance)), admin.Id);

        var history = Assert.IsType<OkObjectResult>(await controller.VisitHistory(
            pageSize: 10, sort: "ip", order: "asc", token: CancellationToken.None));
        var historyJson = System.Text.Json.JsonSerializer.Serialize(history.Value);
        Assert.Contains("admin@example.test", historyJson);
        Assert.Contains("\"total\":2", historyJson);
        foreach (var sort in new[] { "ip", "page", "visitedAt" })
            foreach (var order in new[] { "asc", "desc" })
                Assert.IsType<OkObjectResult>(await controller.VisitHistory(sort: sort, order: order, token: CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.VisitHistory(query: "admin-access", token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.VisitHistory(sort: "wrong", token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.VisitHistory(order: "sideways", token: CancellationToken.None));

        var rules = Assert.IsType<OkObjectResult>(await controller.Rules(token: CancellationToken.None));
        Assert.Contains("198.51.100.9", System.Text.Json.JsonSerializer.Serialize(rules.Value));
        foreach (var sort in new[] { "target", "createdAt", "expiresAt", "status" })
            foreach (var order in new[] { "asc", "desc" })
                Assert.IsType<OkObjectResult>(await controller.Rules(sort: sort, order: order, token: CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.Rules(query: "test rule", token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.Rules(sort: "wrong", token: CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.Rules(order: "sideways", token: CancellationToken.None));
    }

    [Theory]
    [InlineData("::ffff:172.31.40.1", "172.31.40.1")]
    [InlineData("203.0.113.4", "203.0.113.4")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void ClientAddressesAreStoredCanonically(string raw, string expected) =>
        Assert.Equal(expected, ProxyAccessMonitor.NormalizeAddress(System.Net.IPAddress.Parse(raw)));

    [Fact]
    public void MissingClientAddressUsesStableSentinel() =>
        Assert.Equal("unknown", ProxyAccessMonitor.NormalizeAddress(null));

    [Fact]
    public async Task VisitorRegistrySeparatesPageViewsFromProxyTraffic()
    {
        await using var fixture = new Fixture();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "visitor",
            Email = "visitor@example.test",
            DisplayName = "Посетитель"
        };
        fixture.Db.Users.Add(user);
        fixture.Db.ProxyAccessBuckets.AddRange(
            Bucket("198.51.100.9", 5, 50),
            Bucket("198.51.100.9", 3, 0, "page:home", user.Id),
            Bucket("198.51.100.9", 2, 0, "page:account", user.Id),
            Bucket("198.51.100.10", 1, 0, "page:home"));
        await fixture.Db.SaveChangesAsync();
        var monitor = new ProxyAccessMonitor(fixture.Factory, NullLogger<ProxyAccessMonitor>.Instance);
        var controller = WithPrincipal(new AdminAccessController(fixture.Db, monitor), user.Id);

        var traffic = Assert.IsType<OkObjectResult>(await controller.List(token: CancellationToken.None));
        var trafficJson = System.Text.Json.JsonSerializer.Serialize(traffic.Value);
        Assert.Contains("\"requests\":5", trafficJson);
        Assert.DoesNotContain("\"requests\":11", trafficJson);

        var visitors = Assert.IsType<OkObjectResult>(await controller.Visitors(token: CancellationToken.None));
        var visitorJson = System.Text.Json.JsonSerializer.Serialize(visitors.Value);
        Assert.Contains("\"pageViews\":6", visitorJson);
        Assert.Contains("\"uniqueVisitors\":2", visitorJson);
        Assert.Contains("\"authenticatedVisitors\":1", visitorJson);
        Assert.Contains("\"Pages\":2", visitorJson);
        Assert.Contains("visitor@example.test", visitorJson);
        var filtered = Assert.IsType<OkObjectResult>(await controller.Visitors(
            query: "198.51.100.10", token: CancellationToken.None));
        Assert.Contains("\"total\":1", System.Text.Json.JsonSerializer.Serialize(filtered.Value));
    }

    [Theory]
    [InlineData(null, "home")]
    [InlineData("", "home")]
    [InlineData("/?utm_source=ignored", "home")]
    [InlineData("/login", "login")]
    [InlineData("/admin/login", "login")]
    [InlineData("/register", "register")]
    [InlineData("/forgot-password", "forgot-password")]
    [InlineData("/reset-password", "reset-password")]
    [InlineData("/account", "account")]
    [InlineData("/account/profile", "account")]
    [InlineData("/admin", "admin-overview")]
    [InlineData("/admin/operations", "admin-operations")]
    [InlineData("/admin/sources", "admin-sources")]
    [InlineData("/admin/proxies", "admin-proxies")]
    [InlineData("/admin/backups", "admin-backups")]
    [InlineData("/admin/users", "admin-users")]
    [InlineData("/admin/payments", "admin-payments")]
    [InlineData("/admin/telegram", "admin-telegram")]
    [InlineData("/admin/subscriptions", "admin-subscriptions")]
    [InlineData("/ADMIN/ACCESS/", "admin-access")]
    [InlineData("/unknown/private-value?token=secret", "other")]
    public void SiteVisitPathsAreReducedToStablePrivacySafeCodes(string? path, string expected) =>
        Assert.Equal(expected, SiteTelemetryController.NormalizePage(path));

    [Fact]
    public async Task SiteTelemetryHonorsGlobalPrivacyControlAndAcceptsAnonymousVisit()
    {
        await using var fixture = new Fixture();
        var monitor = new ProxyAccessMonitor(fixture.Factory, NullLogger<ProxyAccessMonitor>.Instance);
        var controller = new SiteTelemetryController(monitor)
        {
            ControllerContext = new ControllerContext { HttpContext = Context("/api/v1/telemetry/visit", "192.0.2.20") }
        };
        controller.Request.Headers["Sec-GPC"] = "1";
        Assert.IsType<NoContentResult>(controller.Visit(new SiteVisitRequest { Path = "/account?secret=ignored" }));
        controller.Request.Headers.Remove("Sec-GPC");
        Assert.IsType<NoContentResult>(controller.Visit(new SiteVisitRequest { Path = "/account?secret=ignored" }));
    }

    [Fact]
    public async Task AccessMiddlewareSkipsOtherPathsRecordsAllowedAndRejectsBlockedClients()
    {
        await using var fixture = new Fixture();
        var admin = new ApplicationUser { Id = Guid.NewGuid(), UserName = "admin", Email = "admin@example.test" };
        fixture.Db.Users.Add(admin);
        fixture.Db.AccessBlockRules.Add(new AccessBlockRule
        {
            Kind = AccessBlockKinds.Ip,
            Value = "203.0.113.5",
            Reason = "test",
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
        allowed.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString())],
            "test"));
        await middleware.InvokeAsync(allowed, monitor);
        var export = Context("/api/v1/export/txt", "192.0.2.3");
        await middleware.InvokeAsync(export, monitor);
        // Пустой ответ без item metrics — допустимый сценарий и не должен
        // ломать неблокирующий access counter.
        var empty = Context("/api/v1/proxies", "192.0.2.4");
        await new ProxyAccessMiddleware(_ => Task.CompletedTask).InvokeAsync(empty, monitor);
        var blocked = Context("/api/v1/proxies/seek", "203.0.113.5");
        await middleware.InvokeAsync(blocked, monitor);

        Assert.Equal(3, nextCalls);
        Assert.Equal(StatusCodes.Status403Forbidden, blocked.Response.StatusCode);
        Assert.True(blocked.Response.Body.Length > 0);
    }

    private static PaymentOrder Order(ApplicationUser user, string provider, string status, long amount) => new()
    {
        User = user,
        UserId = user.Id,
        ProductCode = "pro-30",
        Plan = SubscriptionPlans.Pro,
        Provider = provider,
        AmountMinor = amount,
        Currency = "RUB",
        DurationDays = 30,
        Status = status,
        ProviderPaymentId = Guid.NewGuid().ToString("N")
    };

    private static ProxyAccessBucket Bucket(string ip, int requests, long items,
        string endpoint = "catalog", Guid? userId = null) => new()
        {
            BucketStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            IpAddress = ip,
            UserId = userId,
            Endpoint = endpoint,
            Requests = requests,
            ProxyItems = items,
            LastSeenAt = DateTimeOffset.UtcNow
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
