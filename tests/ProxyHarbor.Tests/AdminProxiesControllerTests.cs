using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class AdminProxiesControllerTests
{
    [Fact]
    public async Task RegistryReturnsFilteredPageAndGlobalLifetimeSummary()
    {
        var now = DateTimeOffset.UtcNow;
        await using var fixture = new Fixture();
        fixture.Db.Proxies.AddRange(
            Proxy("203.0.113.10", ProxyStatus.Alive, "DE", now.AddHours(-2), 120, 9, 1),
            Proxy("198.51.100.20", ProxyStatus.Dead, "US", null, null, 3, 4, now.AddDays(-3)),
            Proxy("192.0.2.30", ProxyStatus.Pending, null, null, null, 0, 0));
        await fixture.Db.SaveChangesAsync();

        var collectorOptions = Options.Create(new CollectorOptions
        {
            PublicFreshnessMinutes = 15,
            DeadRetentionDays = 7
        });
        using var snapshotCache = new ProxyMetricsSnapshotCache(
            fixture.Factory,
            collectorOptions,
            NullLogger<ProxyMetricsSnapshotCache>.Instance,
            TimeProvider.System);
        var controller = new AdminProxiesController(fixture.Factory, collectorOptions, snapshotCache);
        var result = await controller.Get(1, 10, ProxyStatus.Alive, null, "de", "203.0.113", "active");

        var page = Assert.IsType<OkObjectResult>(result.Result).Value as AdminProxyPage;
        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal("203.0.113.10", item.Host);
        Assert.Equal("DE", item.CountryCode);
        Assert.InRange(item.ActiveForSeconds!.Value, 7_190, 7_210);
        Assert.Equal(3, page.Summary.Total);
        Assert.Equal(1, page.Summary.Alive);
        Assert.Equal(1, page.Summary.FreshAlive);
        Assert.Equal(1, page.Summary.Pending);
        Assert.Equal(1, page.Summary.Dead);
        Assert.Equal(2, page.Summary.EverAlive);
        Assert.Equal(120, page.Summary.AverageAliveLatencyMs);
        Assert.Equal(2, page.Summary.Countries);
        Assert.Equal(2, page.Countries.Count);

        Assert.IsType<OkObjectResult>((await controller.Get()).Result);
        Assert.Equal(1, snapshotCache.DatabaseReads);
    }

    [Theory]
    [InlineData("RUS", "lastChecked")]
    [InlineData("DE", "unknown")]
    public async Task RegistryRejectsInvalidFilters(string country, string sort)
    {
        await using var fixture = new Fixture();
        var controller = new AdminProxiesController(fixture.Factory,
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        var result = await controller.Get(country: country, sort: sort);
        Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, ((ObjectResult)result.Result!).StatusCode);
    }

    [Fact]
    public void RegistryRequiresAdministratorRole()
    {
        var authorize = Assert.Single(typeof(AdminProxiesController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal(UserRoles.Administrator, authorize.Roles);
    }

    private static ProxyEndpoint Proxy(string host, ProxyStatus status, string? country,
        DateTimeOffset? currentAliveSince, int? latency, int successful, int failed,
        DateTimeOffset? firstAliveAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var firstAlive = firstAliveAt ?? currentAliveSince;
        return new ProxyEndpoint
        {
            Host = host,
            Port = 8080,
            Protocol = ProxyProtocol.Http,
            Status = status,
            CountryCode = country,
            LatencyMs = latency,
            FirstSeenAt = now.AddDays(-5),
            LastSeenAt = now,
            LastCheckedAt = status == ProxyStatus.Pending ? null : now.AddMinutes(-1),
            FirstAliveAt = firstAlive,
            LastAliveAt = firstAlive is null ? null : now.AddMinutes(-1),
            CurrentAliveSince = currentAliveSince,
            SuccessfulChecks = successful,
            FailedChecks = failed,
            ConsecutiveFailedChecks = status == ProxyStatus.Dead ? Math.Min(1, failed) : 0
        };
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DbContextOptions<ProxyHarborDbContext> options =
            new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase($"admin-proxies-{Guid.NewGuid():N}").Options;
        public Fixture() { Db = new ProxyHarborDbContext(options); Factory = new ContextFactory(options); }
        public ProxyHarborDbContext Db { get; }
        public IDbContextFactory<ProxyHarborDbContext> Factory { get; }
        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    private sealed class ContextFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
