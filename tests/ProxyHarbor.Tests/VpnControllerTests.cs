using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет серверную пагинацию, фильтры и безопасное управление VPN-каталогом.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class VpnControllerTests
{
    [Fact]
    public async Task BusyClashExportIsBoundedAndCanceledRequestsReleaseTheirSlots()
    {
        var options = Options();
        var access = new BlockingClashAccess();
        VpnController Controller() => new(new TestDbFactory(options), access)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        using var cancellation = new CancellationTokenSource();
        var first = Controller().Export("clash", token: cancellation.Token);
        var second = Controller().Export("clash", token: cancellation.Token);
        try
        {
            await access.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var rejected = Controller();
            Assert.Equal(503, Assert.IsType<ObjectResult>(await rejected.Export("clash")).StatusCode);
            Assert.Equal("1", rejected.Response.Headers.RetryAfter);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
        }
        Assert.Equal(404, Assert.IsType<ObjectResult>(await PublicClash(options).Export("clash")).StatusCode);
    }

    private sealed class BlockingClashAccess : IFreeExportAccessService
    {
        internal TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int started;
        public async Task<bool> HasPaidAccessAsync(System.Security.Claims.ClaimsPrincipal principal, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref started) == 2) BothStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return false;
        }
        public Task<FreeExportAccess> AcquireAsync(System.Security.Claims.ClaimsPrincipal principal, string? remoteIp, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task PublicClashExportAcceptsYamlOnlyEndpointsAndPreservesSettingsWithoutInventingUris()
    {
        var options = Options();
        var source = Source("Clash", "https://8.8.8.8/clash.yaml");
        var row = ClashEndpoint(source, "proxies: [{type: anytls, server: 8.8.8.8, port: 443, password: published, sni: tls.example.net}]");
        await SeedAsync(options, source, row);
        var controller = PublicClash(options);
        var file = Assert.IsType<FileContentResult>(await controller.Export("CLASH", country: ["de"], protocol: VpnProtocol.AnyTls));
        var body = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.Equal("application/yaml; charset=utf-8", file.ContentType);
        Assert.Equal("vpn-configurations.yaml", file.FileDownloadName);
        Assert.Contains("published", body);
        Assert.Contains("tls.example.net", body);
        Assert.DoesNotContain("clash://", body);
        Assert.Equal("free", controller.Response.Headers["X-Access-Tier"]);
        Assert.Equal("1", controller.Response.Headers["X-Export-Profiles"]);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl);
        Assert.Null(row.ConnectionUri);
        Assert.Single(VpnFeedParser.Parse(body, VpnProtocol.Vless));
    }

    [Fact]
    public async Task FreeClashExportCountsDependencyProfilesAndDoesNotDropTheDialerChain()
    {
        var options = Options();
        var source = Source("Clash", "https://8.8.8.8/clash.yaml");
        const string peer = "{name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: peer-secret}";
        var rows = Enumerable.Range(1, 12).Select(index => ClashEndpoint(source,
            $"proxies: [{{name: main, type: vless, server: 8.8.8.{index}, port: 443, uuid: published, dialer-proxy: peer}}, {peer}]", $"8.8.8.{index}")).ToList();
        rows.Add(ClashEndpoint(source, "proxies: [" + peer + "]"));
        await SeedAsync(options, source, rows.ToArray());
        var controller = PublicClash(options);
        var file = Assert.IsType<FileContentResult>(await controller.Export("clash", protocol: VpnProtocol.Vless, limit: 5000));
        var document = ClashYamlFeedReader.ReadRequired(System.Text.Encoding.UTF8.GetString(file.FileContents));
        var profiles = (YamlDotNet.RepresentationModel.YamlSequenceNode)document.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("proxies")];
        Assert.Equal(10, profiles.Children.Count);
        Assert.Equal("10", controller.Response.Headers["X-Export-Profiles"]);
        Assert.Equal("5", controller.Response.Headers["X-Export-Configurations"]);
        Assert.Equal("12", controller.Response.Headers["X-Catalog-Total"]);
        Assert.Equal("10", controller.Response.Headers["X-Export-Limit"]);
        foreach (var primary in profiles.Children.Cast<YamlDotNet.RepresentationModel.YamlMappingNode>().Where(node => node.Children.ContainsKey(new YamlDotNet.RepresentationModel.YamlScalarNode("dialer-proxy"))))
        {
            var dependency = primary.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("dialer-proxy")];
            Assert.Contains(profiles.Children.Cast<YamlDotNet.RepresentationModel.YamlMappingNode>(), node => node.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("name")].Equals(dependency));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("country")]
    [InlineData("pending")]
    [InlineData("rotated")]
    [InlineData("checked-before-observation")]
    public async Task PublicClashExportRefusesUnvalidatedOrOutdatedDependencyCredentials(string fault)
    {
        var options = Options();
        var source = Source("Clash", "https://8.8.8.8/clash.yaml");
        var primary = ClashEndpoint(source, "proxies: [{name: main, type: vless, server: 8.8.8.8, port: 443, uuid: published, dialer-proxy: peer}, {name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: old-secret}]", "8.8.8.8");
        var peer = ClashEndpoint(source, $"proxies: [{{name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: {(fault == "rotated" ? "new-secret" : "old-secret")}}}]");
        if (fault == "stale") peer.LastCheckedAt = DateTimeOffset.UtcNow.AddDays(-1);
        if (fault == "country") peer.CountryCode = null;
        if (fault == "pending") peer.Status = VpnEndpointStatus.Pending;
        if (fault == "checked-before-observation") peer.ClashConfigurationObservedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await SeedAsync(options, source, fault == "missing" ? [primary] : [primary, peer]);
        var result = Assert.IsType<ObjectResult>(await PublicClash(options).Export("clash", protocol: VpnProtocol.Vless));
        Assert.Equal(404, result.StatusCode);
        Assert.DoesNotContain("old-secret", System.Text.Json.JsonSerializer.Serialize(result.Value));
        Assert.DoesNotContain("new-secret", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task PaidClashExportHonorsRequestedProfileLimitAndCountryAndFreshnessFilters()
    {
        var options = Options();
        var source = Source("Clash", "https://8.8.8.8/clash.yaml");
        var rows = Enumerable.Range(1, 40).Select(index => ClashEndpoint(source, $"proxies: [{{type: vless, server: 8.8.8.{index}, port: 443, uuid: published}}]")).ToArray();
        rows[0].CountryCode = "US";
        rows[1].LastCheckedAt = DateTimeOffset.UtcNow.AddDays(-1);
        rows[2].CountryCode = null;
        rows[3].Status = VpnEndpointStatus.Unreachable;
        await SeedAsync(options, source, rows);
        var controller = PublicClash(options, paid: true);
        Assert.IsType<FileContentResult>(await controller.Export("clash", country: ["DE"], limit: 35));
        Assert.Equal("paid", controller.Response.Headers["X-Access-Tier"]);
        Assert.Equal("36", controller.Response.Headers["X-Catalog-Total"]);
        Assert.Equal("35", controller.Response.Headers["X-Export-Profiles"]);
        Assert.Equal("35", controller.Response.Headers["X-Export-Configurations"]);
        Assert.False(controller.Response.Headers.ContainsKey("Link"));
    }

    private static VpnEndpoint ClashEndpoint(VpnSource source, string yaml, string? host = null)
    {
        var parsed = VpnFeedParser.Parse(yaml, VpnProtocol.Vless);
        var candidate = host is null ? parsed[0] : parsed.Single(item => item.Host == host);
        var row = Endpoint(source, candidate.Host, candidate.Protocol, VpnEndpointStatus.Reachable, 10, "DE");
        row.Port = candidate.Port;
        row.Transport = candidate.Transport;
        row.ClashConfiguration = candidate.ClashConfiguration;
        return row;
    }

    [Fact]
    public async Task OversizedPublicClashExportReturnsAnErrorInsteadOfSilentlyTruncatingSettings()
    {
        var options = Options();
        var source = Source("Clash", "https://8.8.8.8/clash.yaml");
        var extra = new string('x', 14_000);
        var rows = Enumerable.Range(1, 300).Select(index => ClashEndpoint(source,
            $"proxies: [{{type: vless, server: large-{index}.example.net, port: 443, uuid: published, future-option: '{extra}'}}]")).ToArray();
        await SeedAsync(options, source, rows);
        var result = Assert.IsType<ObjectResult>(await PublicClash(options, paid: true).Export("clash", limit: 300));
        Assert.Equal(409, result.StatusCode);
        Assert.DoesNotContain(extra, System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    private static VpnController PublicClash(DbContextOptions<ProxyHarborDbContext> options, bool paid = false)
    {
        var controller = paid ? new VpnController(new TestDbFactory(options)) : new VpnController(new TestDbFactory(options), new FreeAccessService());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public async Task AdminClashDownloadReturnsCompleteYamlAndListingExposesOnlyAvailability()
    {
        var options = Options();
        var source = Source("Clash", "https://8.8.8.8/clash.yaml");
        var row = Endpoint(source, "8.8.8.8", VpnProtocol.AnyTls, VpnEndpointStatus.Pending, null);
        row.ClashConfiguration = VpnFeedParser.Parse("proxies: [{type: anytls, server: 8.8.8.8, port: 443, password: published}]", VpnProtocol.Vless)[0].ClashConfiguration;
        await SeedAsync(options, source, row);
        var controller = Admin(options);
        var file = Assert.IsType<FileContentResult>(await controller.DownloadClash(row.Id));
        Assert.Equal("application/yaml; charset=utf-8", file.ContentType);
        Assert.Equal($"proxyharbor-{row.Id:N}.yaml", file.FileDownloadName);
        Assert.Contains("published", System.Text.Encoding.UTF8.GetString(file.FileContents));
        Assert.Equal(VpnProtocol.AnyTls, Assert.Single(VpnFeedParser.Parse(System.Text.Encoding.UTF8.GetString(file.FileContents), VpnProtocol.Vless)).Protocol);
        var page = AdminEndpointPage(await controller.Endpoints());
        Assert.True(Assert.Single(page.Items).HasClashConfiguration);
        Assert.DoesNotContain("published", System.Text.Json.JsonSerializer.Serialize(page));
    }

    [Fact]
    public async Task AdminClashDownloadRejectsMissingOrMismatchedSavedConfigurationWithoutDisclosure()
    {
        var options = Options();
        var source = Source("Clash", "https://8.8.8.8/clash.yaml");
        var row = Endpoint(source, "8.8.8.8", VpnProtocol.Vless, VpnEndpointStatus.Pending, null);
        await SeedAsync(options, source, row);
        var controller = Admin(options);
        Assert.IsType<NotFoundResult>(await controller.DownloadClash(Guid.NewGuid()));
        Assert.IsType<NotFoundResult>(await controller.DownloadClash(row.Id));
        await using (var db = new ProxyHarborDbContext(options))
        {
            var saved = await db.VpnEndpoints.SingleAsync();
            saved.ClashConfiguration = "proxies: [{type: vless, server: 127.0.0.1, port: 443, password: private-secret}]";
            await db.SaveChangesAsync();
        }
        var result = Assert.IsType<ObjectResult>(await controller.DownloadClash(row.Id));
        Assert.Equal(409, result.StatusCode);
        Assert.DoesNotContain("private-secret", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public void AdminClashDownloadInheritsAdministratorAuthorization()
    {
        var authorization = Assert.Single(typeof(AdminVpnController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true));
        Assert.Equal(UserRoles.Administrator, Assert.IsType<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(authorization).Roles);
        Assert.Empty(typeof(AdminVpnController).GetMethod(nameof(AdminVpnController.DownloadClash))!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
    }

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
    public async Task CountryRepresentationsIncludeYamlOnlyRowsWithoutDoubleCountingOrChangingLegacyUris()
    {
        var options = Options();
        var source = Source("Countries", "https://8.8.8.8/countries.txt");
        var now = DateTimeOffset.UtcNow;
        var uri = Endpoint(source, "1.1.1.1", VpnProtocol.Vless, VpnEndpointStatus.Reachable, 1, "DE", "vless://published@1.1.1.1:443");
        var yaml = Endpoint(source, "8.8.8.8", VpnProtocol.AnyTls, VpnEndpointStatus.Reachable, 2, "JP");
        yaml.ClashConfiguration = "proxies: [{type: anytls, server: 8.8.8.8, port: 443, password: published}]";
        yaml.ClashConfigurationObservedAt = now.AddMinutes(-1);
        yaml.LastCheckedAt = now;
        var both = Endpoint(source, "9.9.9.9", VpnProtocol.Vless, VpnEndpointStatus.Reachable, 3, "DE", "vless://published@9.9.9.9:443");
        both.ClashConfiguration = "proxies: [{type: vless, server: 9.9.9.9, port: 443, uuid: published}]";
        both.ClashConfigurationObservedAt = now.AddMinutes(-1);
        both.LastCheckedAt = now;
        var changed = Endpoint(source, "1.0.0.1", VpnProtocol.AnyTls, VpnEndpointStatus.Reachable, 4, "FR");
        changed.ClashConfiguration = yaml.ClashConfiguration;
        changed.LastCheckedAt = now.AddMinutes(-1);
        changed.ClashConfigurationObservedAt = now;
        var stale = Endpoint(source, "8.8.4.4", VpnProtocol.AnyTls, VpnEndpointStatus.Reachable, 5, "US");
        stale.ClashConfiguration = yaml.ClashConfiguration;
        stale.LastCheckedAt = now.AddDays(-7);
        var unknown = Endpoint(source, "4.2.2.1", VpnProtocol.AnyTls, VpnEndpointStatus.Reachable, 6);
        unknown.ClashConfiguration = yaml.ClashConfiguration;
        unknown.LastCheckedAt = now;
        var pending = Endpoint(source, "4.2.2.2", VpnProtocol.AnyTls, VpnEndpointStatus.Pending, 7, "GB");
        pending.ClashConfiguration = yaml.ClashConfiguration;
        pending.LastCheckedAt = now;
        await SeedAsync(options, source, uri, yaml, both, changed, stale, unknown, pending);
        var controller = new VpnController(new TestDbFactory(options), new FreeAccessService());

        static IReadOnlyList<ProxyCountryDto> Rows(ActionResult<IReadOnlyList<ProxyCountryDto>> result) =>
            Assert.IsAssignableFrom<IReadOnlyList<ProxyCountryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([new ProxyCountryDto("DE", 2)], Rows(await controller.Countries(CancellationToken.None)));
        Assert.Equal([new ProxyCountryDto("DE", 2), new ProxyCountryDto("JP", 1)], Rows(await controller.Countries(CancellationToken.None, "ALL")));
        Assert.Equal([new ProxyCountryDto("DE", 1), new ProxyCountryDto("JP", 1)], Rows(await controller.Countries(CancellationToken.None, "clash")));
        Assert.Equal([new ProxyCountryDto("JP", 1)], Rows(await controller.Countries(CancellationToken.None, "all", VpnProtocol.AnyTls)));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("")]
    [InlineData("yaml")]
    public async Task CountryEndpointRejectsUnsupportedRepresentation(string format)
    {
        var controller = new VpnController(new TestDbFactory(Options()));
        var result = await controller.Countries(CancellationToken.None, format);
        Assert.IsType<BadRequestObjectResult>(result.Result);
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
    public async Task PublicCatalogCountriesAndExportExcludeStaleReachableEndpoints()
    {
        var database = Options();
        var source = Source("Catalog", "https://8.8.8.8/catalog.txt");
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var fresh = Endpoint(source, "1.1.1.1", VpnProtocol.Vless,
            VpnEndpointStatus.Reachable, 20, "US", "vless://fresh@1.1.1.1:443");
        fresh.LastCheckedAt = now.AddMinutes(-14);
        var stale = Endpoint(source, "8.8.8.8", VpnProtocol.Vless,
            VpnEndpointStatus.Reachable, 10, "DE", "vless://stale@8.8.8.8:443");
        stale.LastCheckedAt = now.AddMinutes(-16);
        await SeedAsync(database, source, fresh, stale);
        var controller = new VpnController(
            new TestDbFactory(database),
            new FreeAccessService(),
            Microsoft.Extensions.Options.Options.Create(new CollectorOptions
            {
                VpnPublicFreshnessMinutes = 15
            }),
            new FixedTimeProvider(now));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var page = Page(await controller.Get(token: CancellationToken.None));
        var countries = Assert.IsAssignableFrom<IReadOnlyList<ProxyCountryDto>>(
            Assert.IsType<OkObjectResult>((await controller.Countries(CancellationToken.None)).Result).Value);
        var export = Assert.IsType<FileContentResult>(await controller.Export(
            "txt", token: CancellationToken.None));
        var text = System.Text.Encoding.UTF8.GetString(export.FileContents);

        Assert.Equal(1, page.Total);
        Assert.Equal("1.1.1.1", Assert.Single(page.Items).Host);
        Assert.Equal("US", Assert.Single(countries).Code);
        Assert.Contains("vless://fresh@1.1.1.1:443", text, StringComparison.Ordinal);
        Assert.DoesNotContain("vless://stale@8.8.8.8:443", text, StringComparison.Ordinal);
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
            Endpoint(source, "1.1.1.1", VpnProtocol.Trojan, VpnEndpointStatus.Reachable, 50, "US"),
            Endpoint(source, "9.9.9.9", VpnProtocol.Vmess, VpnEndpointStatus.Unreachable, null, "DE"));
        var controller = Admin(options);

        var sourcePage = Page(await controller.Sources(page: 0, pageSize: 999, search: " provider ", token: CancellationToken.None));
        var endpointPage = AdminEndpointPage(await controller.Endpoints(page: 1, pageSize: 10,
            protocol: VpnProtocol.Trojan, status: VpnEndpointStatus.Reachable, transport: "tcp",
            country: "us", query: "1.1", sort: "quality", order: "desc", token: CancellationToken.None));
        var facetPage = AdminEndpointPage(await controller.Endpoints(page: 1, pageSize: 10,
            protocol: VpnProtocol.Trojan, status: VpnEndpointStatus.Reachable, transport: "tcp",
            country: "us", sort: "lastChecked", order: "desc", token: CancellationToken.None));

        Assert.Single(sourcePage.Items);
        Assert.False(sourcePage.Items[0].IsBuiltIn);
        Assert.Single(endpointPage.Items);
        Assert.Equal(2, endpointPage.Summary.Total);
        Assert.Equal(1, endpointPage.Summary.Reachable);
        Assert.Equal(2, endpointPage.Countries.Count);
        Assert.Equal("US", endpointPage.Items[0].CountryCode);
        Assert.Equal(1, facetPage.Total);
        Assert.Single(facetPage.Items);
        Assert.Equal(400, Assert.IsType<ObjectResult>((await controller.Endpoints(transport: "icmp", token: CancellationToken.None)).Result).StatusCode);
        Assert.Equal(400, Assert.IsType<ObjectResult>((await controller.Endpoints(country: "USA", token: CancellationToken.None)).Result).StatusCode);
        Assert.Equal(400, Assert.IsType<ObjectResult>((await controller.Endpoints(query: new string('x', 129), token: CancellationToken.None)).Result).StatusCode);
        Assert.Equal(400, Assert.IsType<ObjectResult>((await controller.Endpoints(sort: "unknown", token: CancellationToken.None)).Result).StatusCode);
        Assert.Equal(400, Assert.IsType<ObjectResult>((await controller.Endpoints(order: "sideways", token: CancellationToken.None)).Result).StatusCode);
    }

    [Fact]
    public async Task AdminVpnRegistryPreservesExactOrderOnEveryPage()
    {
        var options = Options();
        var source = Source("Deep page", "https://8.8.8.8/deep-page.txt");
        var now = DateTimeOffset.UtcNow;
        var endpoints = Enumerable.Range(0, 21)
            .Select(index => Endpoint(source, $"198.51.100.{index + 1}", VpnProtocol.Vless,
                VpnEndpointStatus.Unreachable, null, "US"))
            .ToArray();
        for (var index = 0; index < endpoints.Length; index++)
            endpoints[index].LastCheckedAt = now.AddMinutes(-index);
        await SeedAsync(options, source, endpoints);

        var firstResult = await Admin(options).Endpoints(page: 1, pageSize: 10,
            status: VpnEndpointStatus.Unreachable, sort: "lastChecked", order: "desc",
            token: CancellationToken.None);
        var secondResult = await Admin(options).Endpoints(page: 2, pageSize: 10,
            status: VpnEndpointStatus.Unreachable, sort: "lastChecked", order: "desc",
            token: CancellationToken.None);

        var firstPage = AdminEndpointPage(firstResult);
        var secondPage = AdminEndpointPage(secondResult);
        Assert.Equal(21, firstPage.Total);
        Assert.Equal(21, secondPage.Total);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(index => $"198.51.100.{index}"),
            firstPage.Items.Select(item => item.Host));
        Assert.Equal(
            Enumerable.Range(11, 10).Select(index => $"198.51.100.{index}"),
            secondPage.Items.Select(item => item.Host));
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

    [Fact]
    public async Task ChangingCustomFeedRepresentationClearsConditionalFetchState()
    {
        var options = Options();
        var now = DateTimeOffset.UtcNow;
        var source = Source("Conditional feed", "https://8.8.8.8/old.txt");
        source.LastFetchedAt = now;
        source.LastSucceededAt = now;
        source.LastContentFetchedAt = now;
        source.HttpETag = "\"old-v1\"";
        source.HttpLastModifiedAt = now.AddHours(-1);
        source.LastItemCount = 42;
        source.ConsecutiveFailures = 3;
        source.NextFetchAt = now.AddHours(2);
        source.LastError = "old failure";
        await SeedAsync(options, source);

        var result = await Admin(options).Update(
            source.Id,
            Request("Conditional feed", "https://8.8.4.4/new.txt"),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        await using var verify = new ProxyHarborDbContext(options);
        var saved = await verify.VpnSources.AsNoTracking().SingleAsync();
        Assert.Null(saved.LastFetchedAt);
        Assert.Null(saved.LastSucceededAt);
        Assert.Null(saved.LastContentFetchedAt);
        Assert.Null(saved.HttpETag);
        Assert.Null(saved.HttpLastModifiedAt);
        Assert.Null(saved.NextFetchAt);
        Assert.Null(saved.LastError);
        Assert.Equal(0, saved.LastItemCount);
        Assert.Equal(0, saved.ConsecutiveFailures);
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
            LastCheckedAt = DateTimeOffset.UtcNow,
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

    private static AdminVpnEndpointPage AdminEndpointPage(ActionResult<AdminVpnEndpointPage> result) =>
        Assert.IsType<AdminVpnEndpointPage>(Assert.IsType<OkObjectResult>(result.Result).Value);

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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
