using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Не позволяет публичному API снова выдавать прокси с устаревшей проверкой.</summary>
public sealed class ProxyPublicationTests
{
    [Fact]
    public void HttpsCategoryUsesHttpConnectTransportUri()
    {
        var endpoint = Endpoint("8.8.4.4", ProxyStatus.Alive, DateTimeOffset.UtcNow);
        endpoint.Protocol = ProxyProtocol.Https;

        var dto = ProxyDto.From(endpoint);

        Assert.Equal(ProxyProtocol.Https, dto.Protocol);
        Assert.Equal("http://8.8.4.4:8080", dto.Url);
    }

    [Fact]
    public async Task PublicListAndExportContainOnlyFreshAliveProxies()
    {
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"publication-{Guid.NewGuid():N}", root)
            .Options;
        var now = DateTimeOffset.UtcNow;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.AddRange(
                Endpoint("8.8.8.8", ProxyStatus.Alive, now.AddMinutes(-1)),
                Endpoint("1.1.1.1", ProxyStatus.Alive, now.AddHours(-2)),
                Endpoint("9.9.9.9", ProxyStatus.Dead, now));
            await seed.SaveChangesAsync();
        }

        var controller = new ProxiesController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        var listAction = await controller.Get(null, null, null, 1, 100, CancellationToken.None);
        var page = Assert.IsType<PagedResult<ProxyDto>>(Assert.IsType<OkObjectResult>(listAction.Result).Value);
        Assert.Single(page.Items);
        Assert.Equal("8.8.8.8", page.Items[0].Host);

        var export = Assert.IsType<FileContentResult>(await controller.Export("txt", null, CancellationToken.None));
        var text = Encoding.UTF8.GetString(export.FileContents);
        Assert.Contains("8.8.8.8", text);
        Assert.DoesNotContain("1.1.1.1", text);
    }

    private static ProxyEndpoint Endpoint(string host, ProxyStatus status, DateTimeOffset checkedAt) => new()
    {
        Host = host,
        Port = 8080,
        Protocol = ProxyProtocol.Http,
        Status = status,
        LastCheckedAt = checkedAt,
        NextCheckAt = checkedAt.AddMinutes(5),
        SuccessfulChecks = status == ProxyStatus.Alive ? 1 : 0,
        FailedChecks = status == ProxyStatus.Dead ? 1 : 0
    };

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
