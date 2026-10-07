using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using SnapshotDatabase = ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests.SnapshotDatabase;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class VpnClashExportIntegrationTests
{
    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task EmptyClashCatalogReturnsNotFoundWithProductionRetryStrategy()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var result = Assert.IsType<ObjectResult>(await Controller(database, false).Export("clash"));
        Assert.Equal(404, result.StatusCode);
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task TransientPageFailureRetriesTheWholeReadOnlySnapshotBeforeReturningYaml()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await using (var db = database.Factory.CreateDbContext())
        {
            var source = Source();
            db.VpnSources.Add(source);
            db.VpnEndpoints.Add(Endpoint(source, "proxies: [{type: anytls, server: 8.8.8.8, port: 443, password: published}]"));
            await db.SaveChangesAsync();
        }
        var failure = new TransientPageFailure();
        var controller = Controller(database, true, failure);
        var file = Assert.IsType<FileContentResult>(await controller.Export("clash", limit: 1));
        Assert.Equal(2, failure.CountTransactions.Count);
        Assert.NotSame(failure.CountTransactions[0], failure.CountTransactions[1]);
        Assert.True(failure.Injected);
        Assert.Single(VpnFeedParser.Parse(System.Text.Encoding.UTF8.GetString(file.FileContents), VpnProtocol.Vless));
        Assert.Equal("1", controller.Response.Headers["X-Catalog-Total"]);
        Assert.Equal("1", controller.Response.Headers["X-Export-Profiles"]);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(false, 10)]
    [InlineData(true, 35)]
    public async Task PublicClashSelectionUsesRealPostgresAndBoundedPagesWithAccessQuotas(bool paid, int expected)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await using (var db = database.Factory.CreateDbContext())
        {
            var source = Source();
            db.VpnSources.Add(source);
            for (var index = 1; index <= 40; index++)
                db.VpnEndpoints.Add(Endpoint(source, $"proxies: [{{type: anytls, server: 8.8.8.{index}, port: 443, password: published}}]"));
            await db.SaveChangesAsync();
        }
        var controller = Controller(database, paid);
        var file = Assert.IsType<FileContentResult>(await controller.Export("clash", limit: 35));
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), controller.Response.Headers["X-Export-Profiles"]);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), controller.Response.Headers["X-Export-Configurations"]);
        Assert.Equal(expected, VpnFeedParser.Parse(System.Text.Encoding.UTF8.GetString(file.FileContents), VpnProtocol.Vless).Count);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl);
        await using var after = database.Factory.CreateDbContext();
        Assert.Equal(40, await after.VpnEndpoints.CountAsync());
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task DependencyQueryAcceptsEquivalentPublishedSettingsAndRejectsRotatedCredentialsOnPostgres()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await using (var db = database.Factory.CreateDbContext())
        {
            var source = Source();
            db.VpnSources.Add(source);
            db.VpnEndpoints.Add(Endpoint(source, "proxies: [{name: main, type: vless, server: 8.8.8.8, port: 443, uuid: published, dialer-proxy: peer}, {name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: current-secret}]"));
            var dependency = Endpoint(source, "proxies: [{password: current-secret, port: 1080, server: 1.1.1.1, type: socks5, name: renamed}]");
            dependency.CountryCode = "US";
            db.VpnEndpoints.Add(dependency);
            await db.SaveChangesAsync();
        }
        var controller = Controller(database, true);
        var file = Assert.IsType<FileContentResult>(await controller.Export("clash", protocol: VpnProtocol.Vless, country: ["DE"], limit: 2));
        Assert.Equal("2", controller.Response.Headers["X-Export-Profiles"]);
        Assert.Contains("current-secret", System.Text.Encoding.UTF8.GetString(file.FileContents));
        await using (var db = database.Factory.CreateDbContext())
        {
            var dependency = await db.VpnEndpoints.SingleAsync(x => x.Host == "1.1.1.1");
            dependency.ClashConfiguration = dependency.ClashConfiguration!.Replace("current-secret", "rotated-secret");
            await db.SaveChangesAsync();
        }
        var missing = Assert.IsType<ObjectResult>(await Controller(database, true).Export("clash", protocol: VpnProtocol.Vless));
        Assert.Equal(404, missing.StatusCode);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(missing.Value));
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task CountryRepresentationUnionAndObservationFreshnessTranslateOnPostgres()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        await using (var db = database.Factory.CreateDbContext())
        {
            var source = Source();
            db.VpnSources.Add(source);
            var yaml = Endpoint(source, "proxies: [{type: anytls, server: 8.8.8.8, port: 443, password: published}]");
            yaml.CountryCode = "JP";
            var both = Endpoint(source, "proxies: [{type: vless, server: 9.9.9.9, port: 443, uuid: published}]");
            both.ConnectionUri = "vless://published@9.9.9.9:443";
            var changed = Endpoint(source, "proxies: [{type: anytls, server: 1.1.1.1, port: 443, password: changed}]");
            changed.CountryCode = "FR";
            changed.ClashConfigurationObservedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            db.VpnEndpoints.AddRange(yaml, both, changed);
            await db.SaveChangesAsync();
        }
        var controller = Controller(database, false);
        static IReadOnlyList<ProxyCountryDto> Rows(ActionResult<IReadOnlyList<ProxyCountryDto>> result) =>
            Assert.IsAssignableFrom<IReadOnlyList<ProxyCountryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([new ProxyCountryDto("DE", 1)], Rows(await controller.Countries(CancellationToken.None)));
        Assert.Equal([new ProxyCountryDto("DE", 1), new ProxyCountryDto("JP", 1)], Rows(await controller.Countries(CancellationToken.None, "all")));
        Assert.Equal([new ProxyCountryDto("JP", 1)], Rows(await controller.Countries(CancellationToken.None, "clash", VpnProtocol.AnyTls)));
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task ChangedScalarInterpretationInCurrentDependencyRejectsTheWholeExportChain()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        const string peer = "proxies: [{name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: 0012}]";
        await using (var db = database.Factory.CreateDbContext())
        {
            var source = Source();
            db.VpnSources.Add(source);
            db.VpnEndpoints.Add(Endpoint(source, "proxies: [{type: vless, server: 8.8.8.8, port: 443, uuid: published, dialer-proxy: peer}, {name: peer, type: socks5, server: 1.1.1.1, port: 1080, password: 0012}]"));
            db.VpnEndpoints.Add(Endpoint(source, peer));
            await db.SaveChangesAsync();
        }
        Assert.IsType<FileContentResult>(await Controller(database, true).Export("clash", protocol: VpnProtocol.Vless));
        await using (var db = database.Factory.CreateDbContext())
        {
            var dependency = await db.VpnEndpoints.SingleAsync(row => row.Host == "1.1.1.1");
            dependency.ClashConfiguration = VpnFeedParser.Parse(peer.Replace("password: 0012", "password: '0012'", StringComparison.Ordinal), VpnProtocol.Vless).Single().ClashConfiguration;
            await db.SaveChangesAsync();
        }
        var rejected = Assert.IsType<ObjectResult>(await Controller(database, true).Export("clash", protocol: VpnProtocol.Vless));
        Assert.Equal(404, rejected.StatusCode);
        Assert.DoesNotContain("0012", System.Text.Json.JsonSerializer.Serialize(rejected.Value));
        await using (var db = database.Factory.CreateDbContext())
        {
            var dependency = await db.VpnEndpoints.SingleAsync(row => row.Host == "1.1.1.1");
            dependency.ClashConfiguration = VpnFeedParser.Parse(peer, VpnProtocol.Vless).Single().ClashConfiguration;
            await db.SaveChangesAsync();
        }
        Assert.IsType<FileContentResult>(await Controller(database, true).Export("clash", protocol: VpnProtocol.Vless));
    }

    private static VpnSource Source() => new()
    {
        Name = "Native YAML export fixture",
        Provider = "Integration test",
        License = "MIT",
        Url = "https://example.net/clash.yaml",
        DefaultProtocol = VpnProtocol.Vless
    };

    private static VpnEndpoint Endpoint(VpnSource source, string yaml)
    {
        var candidate = VpnFeedParser.Parse(yaml, VpnProtocol.Vless)[0];
        return new VpnEndpoint
        {
            Host = candidate.Host,
            Port = candidate.Port,
            Protocol = candidate.Protocol,
            Transport = candidate.Transport,
            ClashConfiguration = candidate.ClashConfiguration,
            CountryCode = "DE",
            Status = VpnEndpointStatus.Reachable,
            LastCheckedAt = DateTimeOffset.UtcNow,
            FirstSource = source,
            FirstSourceId = source.Id
        };
    }

    private static VpnController Controller(SnapshotDatabase database, bool paid, params DbCommandInterceptor[] interceptors)
    {
        using var db = database.Factory.CreateDbContext();
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql(db.Database.GetConnectionString(), npgsql =>
                npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null))
            .AddInterceptors(interceptors).Options;
        var factory = new ProxySourceImportStoreIntegrationTests.SnapshotDbFactory(options);
        using var retryingDb = factory.CreateDbContext();
        Assert.True(retryingDb.Database.CreateExecutionStrategy().RetriesOnFailure);
        return new VpnController(factory, new Access(paid))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    }

    private sealed class TransientPageFailure : DbCommandInterceptor
    {
        internal List<DbTransaction> CountTransactions { get; } = [];
        internal bool Injected { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("\"VpnEndpoints\"", StringComparison.Ordinal)) return result;
            Assert.NotNull(command.Transaction);
            Assert.Equal(System.Data.IsolationLevel.RepeatableRead, command.Transaction.IsolationLevel);
            await using var readOnly = new NpgsqlCommand("SHOW transaction_read_only",
                (NpgsqlConnection)command.Connection!, (NpgsqlTransaction)command.Transaction);
            Assert.Equal("on", await readOnly.ExecuteScalarAsync(cancellationToken));
            if (command.CommandText.Contains("count(*)", StringComparison.Ordinal))
                CountTransactions.Add(command.Transaction);
            else if (!Injected)
            {
                Injected = true;
                throw new PostgresException("Injected serialization failure", "ERROR", "ERROR", "40001");
            }
            return result;
        }
    }

    private sealed class Access(bool paid) : IFreeExportAccessService
    {
        public Task<bool> HasPaidAccessAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) => Task.FromResult(paid);
        public Task<FreeExportAccess> AcquireAsync(ClaimsPrincipal principal, string? remoteIp, CancellationToken cancellationToken) =>
            Task.FromResult(new FreeExportAccess(true, paid, paid ? 5000 : 10, null, paid ? "paid" : "free"));
    }
}
