using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет серверную пагинацию, фильтры и безопасное управление VPN-каталогом.</summary>
public sealed class VpnControllerTests
{
    [Fact]
    public async Task PublicCatalogDefaultsToReachableAndSupportsExplicitFilters()
    {
        var options = Options();
        var source = Source("Catalog", "https://8.8.8.8/catalog.txt");
        await SeedAsync(options, source,
            Endpoint(source, "1.1.1.1", VpnProtocol.Vless, VpnEndpointStatus.Reachable, 120),
            Endpoint(source, "8.8.8.8", VpnProtocol.WireGuard, VpnEndpointStatus.UnsupportedTransport, null));
        var controller = new VpnController(new TestDbFactory(options));

        var defaultPage = Page(await controller.Get(token: CancellationToken.None));
        var wireGuardPage = Page(await controller.Get(page: -1, pageSize: 500,
            protocol: VpnProtocol.WireGuard, status: VpnEndpointStatus.UnsupportedTransport,
            token: CancellationToken.None));

        Assert.Single(defaultPage.Items);
        Assert.Equal("Catalog", defaultPage.Items[0].SourceName);
        Assert.Equal(source.Url, defaultPage.Items[0].SourceUrl);
        Assert.Single(wireGuardPage.Items);
        Assert.Equal(100, wireGuardPage.PageSize);
        Assert.Equal(1, wireGuardPage.Page);
    }

    [Fact]
    public void PublicSourceSummaryDescribesAllSupportedProtocols()
    {
        var result = new VpnController(null!).Sources();

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task AdminListsSearchesAndFiltersEndpointsWithBoundedPages()
    {
        var options = Options();
        var source = Source("Needle feed", "https://8.8.8.8/needle.txt", "Needle provider");
        await SeedAsync(options, source,
            Endpoint(source, "1.1.1.1", VpnProtocol.Trojan, VpnEndpointStatus.Reachable, 50),
            Endpoint(source, "9.9.9.9", VpnProtocol.Vmess, VpnEndpointStatus.Unreachable, null));
        var controller = Admin(options);

        var sourcePage = Page(await controller.Sources(page: 0, pageSize: 999, search: " provider ", token: CancellationToken.None));
        var endpointPage = Page(await controller.Endpoints(page: 1, pageSize: 10,
            protocol: VpnProtocol.Trojan, status: VpnEndpointStatus.Reachable, token: CancellationToken.None));

        Assert.Single(sourcePage.Items);
        Assert.False(sourcePage.Items[0].IsBuiltIn);
        Assert.Single(endpointPage.Items);
    }

    [Fact]
    public async Task AdminCreatesUpdatesDeletesCustomSourceAndRejectsDuplicate()
    {
        var options = Options();
        var controller = Admin(options);
        var request = Request("Custom", "https://8.8.8.8/custom.txt");

        var created = Assert.IsType<CreatedResult>((await controller.Add(request, CancellationToken.None)).Result);
        var response = Assert.IsType<AdminVpnSourceResponse>(created.Value);
        var duplicate = await controller.Add(Request("Duplicate", request.Url), CancellationToken.None);
        var updated = await controller.Update(response.Id,
            Request("Updated", "https://8.8.4.4/updated.txt", enabled: false), CancellationToken.None);
        var deleted = await controller.Delete(response.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(duplicate.Result);
        Assert.Equal("Updated", Assert.IsType<OkObjectResult>(updated.Result).Value is AdminVpnSourceResponse item ? item.Name : null);
        Assert.IsType<NoContentResult>(deleted);
        Assert.IsType<NotFoundResult>(await controller.Delete(Guid.NewGuid(), CancellationToken.None));
        Assert.IsType<NotFoundResult>((await controller.Update(Guid.NewGuid(), request, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task BuiltInSourceCanOnlyBeDisabledAndInvalidRequestsAreRejected()
    {
        var options = Options();
        var definition = BuiltInVpnSourceCatalog.Sources[0];
        var source = Source(definition.Name, definition.Url, definition.Provider, definition.Protocol, definition.License);
        await SeedAsync(options, source);
        var controller = Admin(options);

        var updated = await controller.Update(source.Id, Request("Changed", "https://8.8.8.8/changed.txt", false), CancellationToken.None);
        var deleted = await controller.Delete(source.Id, CancellationToken.None);
        var invalid = await controller.Add(Request("x", "http://127.0.0.1/feed", license: "x"), CancellationToken.None);

        Assert.False(Assert.IsType<AdminVpnSourceResponse>(Assert.IsType<OkObjectResult>(updated.Result).Value).Enabled);
        Assert.IsType<NoContentResult>(deleted);
        Assert.IsType<BadRequestObjectResult>(invalid.Result);
        await using var verify = new ProxyHarborDbContext(options);
        Assert.False((await verify.VpnSources.SingleAsync()).Enabled);
    }

    private static SaveVpnSourceRequest Request(string name, string url, bool enabled = true, string license = "MIT") => new()
    {
        Name = name,
        Provider = "Custom provider",
        Url = url,
        Protocol = VpnProtocol.Vless,
        Enabled = enabled,
        Priority = 50,
        License = license
    };

    private static VpnSource Source(string name, string url, string provider = "Provider",
        VpnProtocol protocol = VpnProtocol.Vless, string license = "MIT") => new()
        {
            Name = name,
            Provider = provider,
            Url = url,
            DefaultProtocol = protocol,
            License = license
        };

    private static VpnEndpoint Endpoint(VpnSource source, string host, VpnProtocol protocol,
        VpnEndpointStatus status, int? latency) => new()
        {
            Host = host,
            Port = 443,
            Protocol = protocol,
            Status = status,
            LatencyMs = latency,
            FirstSourceId = source.Id,
            FirstSource = source
        };

    private static async Task SeedAsync(DbContextOptions<ProxyHarborDbContext> options, VpnSource source,
        params VpnEndpoint[] endpoints)
    {
        await using var db = new ProxyHarborDbContext(options);
        db.VpnSources.Add(source);
        db.VpnEndpoints.AddRange(endpoints);
        await db.SaveChangesAsync();
    }

    private static DbContextOptions<ProxyHarborDbContext> Options() =>
        new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"vpn-controller-{Guid.NewGuid():N}").Options;

    private static AdminVpnController Admin(DbContextOptions<ProxyHarborDbContext> options) =>
        new(new TestDbFactory(options), null!);

    private static PagedResult<T> Page<T>(ActionResult<PagedResult<T>> result) =>
        Assert.IsType<PagedResult<T>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
