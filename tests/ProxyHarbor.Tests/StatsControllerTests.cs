using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует корректность сводки после объединения агрегатов большой таблицы.</summary>
public sealed class StatsControllerTests
{
    [Fact]
    public async Task SummarySeparatesFreshStaleAndScheduledProxies()
    {
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"stats-{Guid.NewGuid():N}", root).Options;
        var now = DateTimeOffset.UtcNow;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.AddRange(
                Endpoint("1.1.1.1", ProxyProtocol.Http, ProxyStatus.Alive, now.AddMinutes(-1), now.AddMinutes(5), 100),
                Endpoint("8.8.8.8", ProxyProtocol.Https, ProxyStatus.Alive, now.AddHours(-1), now.AddMinutes(-1), 900),
                Endpoint("9.9.9.9", ProxyProtocol.Http, ProxyStatus.Pending, null, null, null),
                Endpoint("4.4.4.4", ProxyProtocol.Socks5, ProxyStatus.Dead, now, now.AddMinutes(5), null));
            seed.Sources.Add(new ProxySource
            {
                Name = "source",
                Url = "https://example.com/list",
                LastResultTruncated = true
            });
            seed.Runs.Add(new CollectionRun { StartedAt = now, Status = "completed", FinishedAt = now });
            await seed.SaveChangesAsync();
        }
        var controller = new StatsController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));

        var result = Assert.IsType<OkObjectResult>(await controller.Get(CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var rootElement = json.RootElement;

        Assert.Equal(1, rootElement.GetProperty("alive").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("staleAlive").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("pending").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("dead").GetInt32());
        Assert.Equal(2, rootElement.GetProperty("dueForCheck").GetInt32());
        Assert.Equal(2, rootElement.GetProperty("scheduledChecks").GetInt32());
        Assert.Equal(100, rootElement.GetProperty("averageLatencyMs").GetDouble());
        Assert.Equal(1, rootElement.GetProperty("sources").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("truncatedSources").GetInt32());
        var protocol = Assert.Single(rootElement.GetProperty("byProtocol").EnumerateArray());
        Assert.Equal(1, protocol.GetProperty("count").GetInt32());
    }

    private static ProxyEndpoint Endpoint(
        string host,
        ProxyProtocol protocol,
        ProxyStatus status,
        DateTimeOffset? checkedAt,
        DateTimeOffset? nextCheckAt,
        int? latency) => new()
        {
            Host = host,
            Port = 8080,
            Protocol = protocol,
            Status = status,
            LastCheckedAt = checkedAt,
            NextCheckAt = nextCheckAt,
            LatencyMs = latency,
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
