using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Не позволяет случайно сузить каталог из 50 независимых провайдеров.</summary>
public sealed class BuiltInSourceCatalogTests
{
    [Fact]
    public void CatalogContainsEightyOneUniqueFeedsFromFiftyProviders()
    {
        Assert.Equal(81, BuiltInSourceCatalog.Sources.Count);
        Assert.Equal(81, BuiltInSourceCatalog.Sources.Select(x => x.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(50, BuiltInSourceCatalog.Sources.Select(x => x.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Enumerable.Range(1, 81), BuiltInSourceCatalog.Sources.Select(x => x.Rank));
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
        });
    }

    [Fact]
    public void CatalogCoversEverySupportedProtocol()
    {
        Assert.All(Enum.GetValues<ProxyProtocol>(), protocol =>
            Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Protocol == protocol));
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
            Priority = 999
        });

        Assert.True(response.IsBuiltIn);
        Assert.Equal(definition.Provider, response.Provider);
        Assert.Equal(definition.Rank, response.CatalogRank);
        Assert.Equal("renamed locally", response.Name);
        Assert.Equal(999, response.Priority);
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
        Assert.Null(response.CatalogRank);
    }
}
