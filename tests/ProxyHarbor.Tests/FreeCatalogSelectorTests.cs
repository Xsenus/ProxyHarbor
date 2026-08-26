using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует состав бесплатной витрины независимо от HTTP и провайдера БД.</summary>
public sealed class FreeCatalogSelectorTests
{
    private sealed record Candidate(string Id, string Country, int Latency);

    [Fact]
    public void SelectsTwoFromFastBandThenMediumAndPrefersDifferentCountries()
    {
        var candidates = Enumerable.Range(0, 50)
            .Select(index => new Candidate($"id-{index}", $"C{index % 18:00}", 100 + index * 20))
            .ToArray();

        var result = FreeCatalogSelector.Select(candidates, x => x.Id, x => x.Country, 10,
            DateTimeOffset.FromUnixTimeSeconds(12_000));

        Assert.Equal(10, result.Count);
        Assert.Equal(2, result.Count(x => x.Latency < candidates[10].Latency));
        Assert.True(result.Select(x => x.Country).Distinct().Count() >= 8);
    }

    [Fact]
    public void SelectionIsStableInsideWindowAndChangesInNextWindow()
    {
        var candidates = Enumerable.Range(0, 100)
            .Select(index => new Candidate($"id-{index}", $"C{index % 25:00}", index))
            .ToArray();
        var now = DateTimeOffset.FromUnixTimeSeconds(60_001);

        var first = FreeCatalogSelector.Select(candidates, x => x.Id, x => x.Country, 10, now);
        var sameWindow = FreeCatalogSelector.Select(candidates, x => x.Id, x => x.Country, 10, now.AddMinutes(9));
        var nextWindow = FreeCatalogSelector.Select(candidates, x => x.Id, x => x.Country, 10, now.AddMinutes(10));

        Assert.Equal(first.Select(x => x.Id), sameWindow.Select(x => x.Id));
        Assert.NotEqual(first.Select(x => x.Id), nextWindow.Select(x => x.Id));
    }
}
