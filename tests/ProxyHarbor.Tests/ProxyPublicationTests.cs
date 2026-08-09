using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

        await using var output = new AsyncOnlyMemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = output } }
        };
        Assert.IsType<EmptyResult>(await controller.Export("txt", null, CancellationToken.None));
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("8.8.8.8", text);
        Assert.DoesNotContain("1.1.1.1", text);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task JsonExportStreamsCamelCaseContractWithStringProtocol()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"json-export-{Guid.NewGuid():N}")
            .Options;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(Endpoint("2001:4860:4860::8888", ProxyStatus.Alive, DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }
        var controller = new ProxiesController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        await using var output = new AsyncOnlyMemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = output } }
        };

        Assert.IsType<EmptyResult>(await controller.Export("json", ProxyProtocol.Http, CancellationToken.None));

        using var json = JsonDocument.Parse(output.ToArray());
        var item = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("Http", item.GetProperty("protocol").GetString());
        Assert.Equal("http://[2001:4860:4860::8888]:8080", item.GetProperty("url").GetString());
        Assert.Contains("proxies-http.json", controller.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtremePageNumberIsRejectedBeforeDatabaseOffsetOverflows()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"pagination-{Guid.NewGuid():N}")
            .Options;
        var controller = new ProxiesController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));

        var action = await controller.Get(null, null, null, int.MaxValue, 1000, CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<BadRequestObjectResult>(action.Result).Value);
        Assert.Equal(400, problem.Status);
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

    /// <summary>Имитирует Kestrel с AllowSynchronousIO=false.</summary>
    private sealed class AsyncOnlyMemoryStream : MemoryStream
    {
        public override void Flush() => throw new InvalidOperationException("Synchronous flush is forbidden.");
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous write is forbidden.");
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new InvalidOperationException("Synchronous write is forbidden.");
        public override void WriteByte(byte value) =>
            throw new InvalidOperationException("Synchronous write is forbidden.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            base.Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = buffer.ToArray();
            base.Write(copy, 0, copy.Length);
            return ValueTask.CompletedTask;
        }
    }
}
