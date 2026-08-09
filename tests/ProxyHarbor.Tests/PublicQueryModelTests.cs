using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует физический контракт индексов горячей публичной выдачи.</summary>
public sealed class PublicQueryModelTests
{
    [Fact]
    public void AliveIndexesMatchStableApiOrderAndFreshnessCounts()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=model_only;Username=model_only")
            .Options;
        using var db = new ProxyHarborDbContext(options);
        // Runtime model намеренно выкидывает design-time annotations индексов.
        var model = db.GetService<IDesignTimeModel>().Model;
        var indexes = model.FindEntityType(typeof(ProxyEndpoint))!.GetIndexes()
            .ToDictionary(index => index.GetDatabaseName()!, StringComparer.Ordinal);

        AssertIndex(
            indexes["IX_Proxies_Alive_PublicOrder"],
            ["LatencyMs", "SuccessfulChecks", "Id", "LastCheckedAt"],
            [false, true, false, false]);
        AssertIndex(
            indexes["IX_Proxies_Alive_Protocol_PublicOrder"],
            ["Protocol", "LatencyMs", "SuccessfulChecks", "Id", "LastCheckedAt"],
            [false, false, true, false, false]);
        AssertIndex(indexes["IX_Proxies_Alive_LastCheckedAt"], ["LastCheckedAt"], [false]);
        AssertIndex(
            indexes["IX_Proxies_Alive_Protocol_LastCheckedAt"],
            ["Protocol", "LastCheckedAt"],
            [false, false]);

        Assert.DoesNotContain("IX_Proxies_Status_LatencyMs_LastCheckedAt", indexes.Keys);
        Assert.DoesNotContain("IX_Proxies_Status_Protocol_LatencyMs_LastCheckedAt", indexes.Keys);
    }

    private static void AssertIndex(IReadOnlyIndex index, string[] properties, bool[] descending)
    {
        Assert.Equal(properties, index.Properties.Select(property => property.Name));
        Assert.Equal(descending, index.IsDescending ?? Enumerable.Repeat(false, properties.Length));
        Assert.Equal("\"Status\" = 1", index.GetFilter());
        Assert.True(index.IsCreatedConcurrently());
    }
}
