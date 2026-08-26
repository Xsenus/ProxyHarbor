using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет серверную пагинацию, фильтры и безопасное управление VPN-каталогом.</summary>
public sealed class VpnControllerTests
{
    [Fact]
    public async Task PublicCatalogDefaultsToReachableAndExcludesIncompleteEndpoints()
    {
        var options = Options();
        var source = Source("Catalog", "https://8.8.8.8/catalog.txt");
        await SeedAsync(options, source,
            Endpoint(source, "1.1.1.1", VpnProtocol.Vless, VpnEndpointStatus.Reachable, 120,
                "US", "vless://public@1.1.1.1:443"),
            Endpoint(source, "8.8.8.8", VpnProtocol.WireGuard, VpnEndpointStatus.UnsupportedTransport, null));
        var controller = new VpnController(new TestDbFactory(options));

        var defaultPage = Page(await controller.Get(token: CancellationToken.None));
        var wireGuardPage = Page(await controller.Get(page: -1, pageSize: 500,
            protocol: VpnProtocol.WireGuard, status: VpnEndpointStatus.UnsupportedTransport,
            token: CancellationToken.None));

        Assert.Single(defaultPage.Items);
        Assert.Equal("US", defaultPage.Items[0].CountryCode);
        Assert.Equal("vless://public@1.1.1.1:443", defaultPage.Items[0].ConnectionUri);
        Assert.Empty(wireGuardPage.Items);
        Assert.Equal(100, wireGuardPage.PageSize);
        Assert.Equal(1, wireGuardPage.Page);
    }

    [Fact]
    public async Task FreeCatalogReturnsTenReadyLinksAndReportsTheFullCountryCatalog()
    {
        var options = Options();
        var source = Source("Catalog", "https://8.8.8.8/catalog.txt");
        var endpoints = Enumerable.Range(1, 24).Select(index =>
        {
            var country = index % 2 == 0 ? "DE" : "FR";
            return Endpoint(source, $"1.1.1.{index}", VpnProtocol.Vless,
                VpnEndpointStatus.Reachable, index * 10, country,
                $"vless://public-{index}@1.1.1.{index}:443");
        }).ToArray();
        await SeedAsync(options, source, endpoints);
        var controller = new VpnController(new TestDbFactory(options), new FreeAccessService());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var page = Page(await controller.Get(page: 3, pageSize: 100, token: CancellationToken.None));
        var german = Page(await controller.Get(country: ["de"], token: CancellationToken.None));
        var countries = Assert.IsAssignableFrom<IReadOnlyList<ProxyCountryDto>>(
            Assert.IsType<OkObjectResult>((await controller.Countries(CancellationToken.None)).Result).Value);

        Assert.Equal(24, page.Total);
        Assert.Equal(10, page.Items.Count);
        Assert.Equal(10, page.Accessible);
        Assert.True(page.Limited);
        Assert.Contains("24", page.Message, StringComparison.Ordinal);
        Assert.All(page.Items, item => Assert.StartsWith("vless://", item.ConnectionUri));
        Assert.Equal(12, german.Total);
        Assert.All(german.Items, item => Assert.Equal("DE", item.CountryCode));
        Assert.Equal(["DE", "FR"], countries.Select(item => item.Code).Order().ToArray());
    }

    [Fact]
    public async Task FreeVpnExportContainsAccessMetadataAndReadyUris()
    {
        var options = Options();
        var source = Source("Catalog", "https://8.8.8.8/catalog.txt");
        await SeedAsync(options, source, Enumerable.Range(1, 12).Select(index =>
            Endpoint(source, $"8.8.8.{index}", VpnProtocol.Trojan, VpnEndpointStatus.Reachable,
                index, "US", $"trojan://secret-{index}@8.8.8.{index}:443")).ToArray());
        var controller = new VpnController(new TestDbFactory(options), new FreeAccessService());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = Assert.IsType<FileContentResult>(await controller.Export("json", token: CancellationToken.None));
        using var json = System.Text.Json.JsonDocument.Parse(result.FileContents);

        Assert.Equal(12, json.RootElement.GetProperty("access").GetProperty("total").GetInt32());
        Assert.Equal(10, json.RootElement.GetProperty("access").GetProperty("accessible").GetInt32());
        Assert.True(json.RootElement.GetProperty("access").GetProperty("limited").GetBoolean());
        Assert.Equal(10, json.RootElement.GetProperty("vpn").GetArrayLength());
        Assert.StartsWith("trojan://", json.RootElement.GetProperty("vpn")[0].GetProperty("connectionUri").GetString());
        Assert.Equal("12", controller.Response.Headers["X-Catalog-Total"].ToString());
    }

    [Fact]
    public async Task FreeTxtExportKeepsReadyUriAndReportsAnUnrestrictedSmallCatalog()
    {
        var options = Options();
        var source = Source("Catalog", "https://8.8.8.8/catalog.txt");
        await SeedAsync(options, source,
            Endpoint(source, "9.9.9.9", VpnProtocol.Vless, VpnEndpointStatus.Reachable,
                90, "FR", "vless://public@9.9.9.9:443"));
        var controller = new VpnController(new TestDbFactory(options), new FreeAccessService());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = Assert.IsType<FileContentResult>(await controller.Export(
            "txt", country: ["fr"], token: CancellationToken.None));
        var text = System.Text.Encoding.UTF8.GetString(result.FileContents);

        Assert.Contains("# total: 1", text, StringComparison.Ordinal);
        Assert.Contains("vless://public@9.9.9.9:443", text, StringComparison.Ordinal);
        Assert.Equal("1", controller.Response.Headers["X-Catalog-Total"].ToString());
    }

    [Fact]
    public async Task PublicVpnApiRejectsUnknownFormatAndMalformedCountryCodes()
    {
        var options = Options();
        var controller = new VpnController(new TestDbFactory(options), new FreeAccessService());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        Assert.IsType<BadRequestObjectResult>((await controller.Get(country: ["DEU"], token: CancellationToken.None)).Result);
        Assert.IsType<ObjectResult>(await controller.Export("xml", token: CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Export("json", country: ["1!"], token: CancellationToken.None));
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
        VpnEndpointStatus status, int? latency, string? countryCode = null, string? connectionUri = null) => new()
        {
            Host = host,
            Port = 443,
            Protocol = protocol,
            Status = status,
            LatencyMs = latency,
            CountryCode = countryCode,
            ConnectionUri = connectionUri,
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

    private sealed class FreeAccessService : IFreeExportAccessService
    {
        public Task<FreeExportAccess> AcquireAsync(System.Security.Claims.ClaimsPrincipal principal, string? remoteIp,
            CancellationToken cancellationToken) => Task.FromResult(new FreeExportAccess(true, false, 10, null, "free"));
        public Task<bool> HasPaidAccessAsync(System.Security.Claims.ClaimsPrincipal principal,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
