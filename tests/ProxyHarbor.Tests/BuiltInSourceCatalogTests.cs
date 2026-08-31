using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Не позволяет случайно сузить каталог из 80 независимых провайдеров.</summary>
public sealed class BuiltInSourceCatalogTests
{
    [Fact]
    public void CatalogContainsThreeHundredTenUniqueFeedsFromEightyProviders()
    {
        Assert.Equal(310, BuiltInSourceCatalog.Sources.Count);
        Assert.Equal(310, BuiltInSourceCatalog.Sources.Select(x => x.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(80, BuiltInSourceCatalog.Sources.Select(x => x.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(80, BuiltInSourceCatalog.Sources.Select(x => x.ProviderIdentity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(80, BuiltInSourceCatalog.ProviderCount);
        Assert.Equal(Enumerable.Range(1, 310), BuiltInSourceCatalog.Sources.Select(x => x.Rank));
    }

    [Fact]
    public void EveryBuiltInFeedUsesPublicHttpsEndpoint()
    {
        Assert.All(BuiltInSourceCatalog.Sources, source =>
        {
            Assert.True(Uri.TryCreate(source.Url, UriKind.Absolute, out var uri));
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
            Assert.False(string.IsNullOrWhiteSpace(source.Name));
            Assert.False(string.IsNullOrWhiteSpace(source.Provider));
            Assert.Matches("^(github|host):[a-z0-9.-]+$", source.ProviderIdentity);
        });
        Assert.All(BuiltInSourceCatalog.Sources.GroupBy(source => source.Provider), group =>
            Assert.Single(group.Select(source => source.ProviderIdentity).Distinct(StringComparer.Ordinal)));
        Assert.All(BuiltInSourceCatalog.Sources.GroupBy(source => source.ProviderIdentity), group =>
            Assert.Single(group.Select(source => source.Provider).Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    public void CatalogCoversEverySupportedProtocol()
    {
        Assert.All(Enum.GetValues<ProxyProtocol>(), protocol =>
            Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Protocol == protocol));
    }

    [Fact]
    public void TheSpeedXAndDatabayUseCanonicalRawGithubBranchUrls()
    {
        var feeds = BuiltInSourceCatalog.Sources
            .Where(source => source.Provider is "TheSpeedX" or "Databay Labs")
            .ToArray();

        Assert.Equal(6, feeds.Length);
        Assert.All(feeds, source =>
        {
            Assert.DoesNotContain("/refs/heads/", source.Url, StringComparison.Ordinal);
            Assert.Contains("/master/", source.Url, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AdminSourceResponseExposesCanonicalCatalogMetadata()
    {
        var definition = BuiltInSourceCatalog.Sources[12];
        var response = SourceResponse.From(new ProxySource
        {
            Name = "renamed locally",
            Url = definition.Url,
            DefaultProtocol = definition.Protocol,
            Priority = 999,
            LastResultTruncated = true
        });

        Assert.True(response.IsBuiltIn);
        Assert.Equal(definition.Provider, response.Provider);
        Assert.Equal(definition.ProviderIdentity, response.ProviderIdentity);
        Assert.Equal(definition.Rank, response.CatalogRank);
        Assert.Equal("renamed locally", response.Name);
        Assert.Equal(999, response.Priority);
        Assert.True(response.LastResultTruncated);
        Assert.Same(definition, BuiltInSourceCatalog.FindByUrl(definition.Url));
        Assert.Null(BuiltInSourceCatalog.FindByUrl(definition.Url.ToUpperInvariant()));
    }

    [Fact]
    public void AdminSourceResponseDoesNotMisclassifyCustomUrl()
    {
        var response = SourceResponse.From(new ProxySource
        {
            Name = "custom",
            Url = "https://example.com/proxies.txt",
            DefaultProtocol = ProxyProtocol.Http
        });

        Assert.False(response.IsBuiltIn);
        Assert.Null(response.Provider);
        Assert.Null(response.ProviderIdentity);
        Assert.Null(response.CatalogRank);
    }
}
