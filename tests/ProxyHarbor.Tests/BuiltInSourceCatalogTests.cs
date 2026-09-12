using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Не позволяет случайно сузить каталог независимых провайдеров.</summary>
public sealed class BuiltInSourceCatalogTests
{
    [Fact]
    public void CatalogContainsExpectedUniqueFeedsAndProviders()
    {
        Assert.Equal(543, BuiltInSourceCatalog.Sources.Count);
        Assert.Equal(543, BuiltInSourceCatalog.Sources.Select(x => x.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(283, BuiltInSourceCatalog.Sources.Select(x => x.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(283, BuiltInSourceCatalog.Sources.Select(x => x.ProviderIdentity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(283, BuiltInSourceCatalog.ProviderCount);
        Assert.Equal(Enumerable.Range(1, 543), BuiltInSourceCatalog.Sources.Select(x => x.Rank));
    }

    [Fact]
    public void CatalogReplacesEmptyLocalVpnAndHtmlInputsWithTextFeeds()
    {
        string[] retiredUrls =
        [
            "https://raw.githubusercontent.com/gproxynet/free-proxy-list/main/http.txt",
            "https://raw.githubusercontent.com/lanzm/MetaFetch/master/list.txt",
            "https://raw.githubusercontent.com/KhaiNguyenDuc/proxy-generator/main/proxies.txt",
            "https://cyber-gateway.net/get-proxy/free-proxy/24-free-http-proxy",
            "https://raw.githubusercontent.com/BuntheaTaing/Proxy-Scraper/main/proxy.txt",
            "https://raw.githubusercontent.com/CrateC/proxy_list/main/proxies.txt",
            "https://raw.githubusercontent.com/du0ngtrunghieu/proxy-scraper/main/http.txt",
            "https://raw.githubusercontent.com/Yogazyy/PROXY-List/main/http.txt",
            "https://raw.githubusercontent.com/mishakorzik/100000-Proxy/main/proxy.txt"
        ];

        Assert.DoesNotContain(BuiltInSourceCatalog.Sources, source => retiredUrls.Contains(source.Url));
        Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Provider == "merlinepedra25");
        Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Provider == "ahahaabas");
        Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Provider == "Timskt");
        Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Provider == "webdevsk");
        Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Provider == "AKANINE00");
        Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Provider == "Hugo-WB");
        Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Provider == "MatteoGitM");
        Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Provider == "SaraanshSharma");
        Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Provider == "a2u");
    }

    [Fact]
    public void RegionalExpansionContainsOnlyPublishedCountryFeeds()
    {
        var feeds = BuiltInSourceCatalog.Sources.Where(source =>
            source.Name.StartsWith("HProxy country ", StringComparison.Ordinal) ||
            source.Name.StartsWith("Proxifly country ", StringComparison.Ordinal)).ToArray();

        Assert.Equal(96, feeds.Length);
        Assert.All(feeds, source => Assert.True(
            source.Url.Contains("/countries/", StringComparison.Ordinal) ||
            source.Url.Contains("/by-country/", StringComparison.Ordinal)));
        Assert.DoesNotContain(feeds, source => source.Name is
            "Proxifly country MD" or "Proxifly country UZ" or
            "Proxifly country CY" or "Proxifly country LU");
    }

    [Fact]
    public void IndependentExpansionAddsExactlyTwoHundredProviders()
    {
        var feeds = BuiltInSourceCatalog.Sources.Skip(343).ToArray();

        Assert.Equal(200, feeds.Length);
        Assert.Equal(200, feeds.Select(source => source.ProviderIdentity).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DynamicXyzs996CountryFeedsAreExcludedInFavorOfStableAggregates()
    {
        var feeds = BuiltInSourceCatalog.Sources
            .Where(source => source.Provider == "XYZS996")
            .ToArray();

        Assert.Equal(3, feeds.Length);
        Assert.DoesNotContain(feeds, source => source.Url.Contains("/proxies/countries/", StringComparison.Ordinal));
        Assert.Contains(feeds, source => source.Url.EndsWith("/all.txt", StringComparison.Ordinal));
        Assert.Contains(feeds, source => source.Url.EndsWith("/http.txt", StringComparison.Ordinal));
        Assert.Contains(feeds, source => source.Url.EndsWith("/https.txt", StringComparison.Ordinal));
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
