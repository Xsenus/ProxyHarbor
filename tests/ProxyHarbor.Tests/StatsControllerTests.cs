using System.Reflection;
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

/// <summary>Фиксирует корректность сводки после объединения агрегатов большой таблицы.</summary>
public sealed class StatsControllerTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SummarySeparatesFreshStaleAndScheduledProxies()
    {
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"stats-{Guid.NewGuid():N}", root).Options;
        var now = DateTimeOffset.UtcNow;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            var leased = Endpoint(
                "8.8.8.8", ProxyProtocol.Https, ProxyStatus.Alive,
                now.AddHours(-1), now.AddMinutes(-1), 900);
            seed.Proxies.AddRange(
                Endpoint("1.1.1.1", ProxyProtocol.Http, ProxyStatus.Alive, now.AddMinutes(-1), now.AddMinutes(5), 100),
                leased,
                Endpoint("9.9.9.9", ProxyProtocol.Http, ProxyStatus.Pending, null, null, null),
                Endpoint("4.4.4.4", ProxyProtocol.Socks5, ProxyStatus.Dead, now, now.AddMinutes(5), null));
            seed.ProxyValidationLeases.Add(new ProxyValidationLease
            {
                ProxyId = leased.Id,
                LeaseId = Guid.NewGuid(),
                LeaseUntil = now.AddMinutes(1)
            });
            seed.Sources.Add(new ProxySource
            {
                Name = "source",
                Url = "https://example.com/list",
                LastResultTruncated = true
            });
            seed.Runs.Add(new CollectionRun
            {
                StartedAt = now,
                Status = "completed",
                FinishedAt = now,
                Error = "internal-source-secret-must-not-be-public"
            });
            await seed.SaveChangesAsync();
        }
        var controller = new StatsController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));

        var action = await controller.Get(CancellationToken.None);
        var result = Assert.IsType<StatsResponse>(Assert.IsType<OkObjectResult>(action.Result).Value);

        Assert.Equal(1, result.Alive);
        Assert.Equal(1, result.StaleAlive);
        Assert.Equal(1, result.Pending);
        Assert.Equal(1, result.Dead);
        Assert.Equal(1, result.DueForCheck);
        Assert.Equal(1, result.ChecksInProgress);
        Assert.Equal(2, result.ScheduledChecks);
        Assert.Equal(100, result.AverageLatencyMs);
        Assert.Equal(1, result.Sources);
        Assert.Equal(1, result.TruncatedSources);
        Assert.Equal(1, Assert.Single(result.ByProtocol).Count);
        Assert.Equal("completed", Assert.IsType<PublicCollectionRunResponse>(result.LastRun).Status);
        var wireJson = JsonSerializer.Serialize(result, WebJsonOptions);
        Assert.Contains("\"lastRun\":", wireJson, StringComparison.Ordinal);
        Assert.DoesNotContain("internal-source-secret-must-not-be-public", wireJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessMetadataPublishesNamedStatsSchemaWithoutPersistenceEntity()
    {
        var success = typeof(StatsController).GetMethod(nameof(StatsController.Get))!
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Single(attribute => attribute.StatusCode == StatusCodes.Status200OK);

        Assert.Equal(typeof(StatsResponse), success.Type);
        Assert.Null(typeof(PublicCollectionRunResponse).GetProperty(nameof(CollectionRun.Id)));
        Assert.Null(typeof(PublicCollectionRunResponse).GetProperty(nameof(CollectionRun.Error)));
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
