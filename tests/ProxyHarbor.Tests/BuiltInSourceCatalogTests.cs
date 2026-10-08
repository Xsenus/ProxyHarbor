using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Не позволяет случайно сузить каталог независимых провайдеров.</summary>
public sealed class BuiltInSourceCatalogTests
{
    [Fact]
    public void ProxioMixedPreservesPublishedProtocolsAcrossSnapshotWindows()
    {
        var feed = BuiltInSourceCatalog.Sources.Single(source => source.Name == "Proxio Mixed");
        Assert.Equal("https://raw.githubusercontent.com/proxio-io/proxy-list/main/all.json", feed.Url);
        const string body = """
            {"source":"https://proxio.io","count":3,"proxies":[
              {"ip":"8.8.8.8","port":8080,"protocols":["HTTP","HTTPS"]},
              {"ip":"1.1.1.1","port":1080,"protocols":["SOCKS4","SOCKS5"]},
              {"ip":"9.9.9.9","port":2525,"protocols":["CONNECT25","CONNECT80"]}]}
            """;
        var parsed = SourceFeedParser.ParseRequired(body, feed.Protocol);
        Assert.Equal(
            [("8.8.8.8", 8080, ProxyProtocol.Http), ("8.8.8.8", 8080, ProxyProtocol.Https),
             ("1.1.1.1", 1080, ProxyProtocol.Socks4), ("1.1.1.1", 1080, ProxyProtocol.Socks5)], parsed);
        var snapshot = ProxyCandidateSnapshotCodec.Encode(body, feed.Protocol);
        Assert.Equal(4, snapshot.Count);
        var decoded = new List<ProxyCandidateKey>();
        var index = 0;
        while (true)
        {
            var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, index, 1, candidate =>
            {
                decoded.Add(candidate);
                return true;
            });
            Assert.True(window.NextIndex > index);
            index = window.NextIndex;
            if (window.Completed) break;
        }
        Assert.Equal(parsed, decoded.Select(candidate => candidate.ToEndpoint()).ToArray());
    }

    [Fact]
    public void OctoberSourcesUseDocumentedAggregatePathsAndLiveLitportBranch()
    {
        string[] publishers = ["Litport", "Maximilian Feix", "ProxyWhirl"];
        foreach (var publisher in publishers)
        {
            var feeds = BuiltInSourceCatalog.Sources.Where(source => source.Provider == publisher).ToArray();
            Assert.Equal(4, feeds.Length);
            // These publishers document four legacy feed categories. TLS-to-proxy
            // is an independent opt-in transport and must not relabel their HTTPS lists.
            ProxyProtocol[] publishedProtocols = [ProxyProtocol.Http, ProxyProtocol.Https, ProxyProtocol.Socks4, ProxyProtocol.Socks5];
            Assert.Equal(publishedProtocols, feeds.Select(source => source.Protocol));
            Assert.All(feeds, source => Assert.EndsWith(
                publisher == "Litport" && source.Protocol == ProxyProtocol.Https ? ".json" : ".txt",
                source.Url, StringComparison.Ordinal));
        }
        Assert.All(BuiltInSourceCatalog.Sources.Where(source => source.Provider == "Litport"), source =>
            Assert.Contains("/free-proxy-list/live/proxies/", source.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogContainsExpectedUniqueFeedsAndProviders()
    {
        Assert.Equal(547, BuiltInSourceCatalog.Sources.Count);
        Assert.Equal(547, BuiltInSourceCatalog.Sources.Select(x => x.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(281, BuiltInSourceCatalog.Sources.Select(x => x.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(281, BuiltInSourceCatalog.Sources.Select(x => x.ProviderIdentity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(281, BuiltInSourceCatalog.ProviderCount);
        Assert.Equal(Enumerable.Range(1, 547), BuiltInSourceCatalog.Sources.Select(x => x.Rank));
    }

    [Fact]
    public void CatalogReplacesEmptyLocalVpnAndHtmlInputsWithTextFeeds()
    {
        string[] retiredUrls =
        [
            "https://raw.githubusercontent.com/Akshay7273/ProxyMan-free-proxy-list/main/protocols/http.txt",
            "https://raw.githubusercontent.com/Akshay7273/ProxyMan-free-proxy-list/main/protocols/socks4.txt",
            "https://raw.githubusercontent.com/Akshay7273/ProxyMan-free-proxy-list/main/protocols/socks5.txt",
            "https://raw.githubusercontent.com/iamthebestm85/Proxy-Scraper-And-Checker/main/proxy.txt",
            "https://raw.githubusercontent.com/just-not-google/full-free-proxy/main/http.txt",
            "https://raw.githubusercontent.com/Allaux/fresh-proxy-list/main/http.txt",
            "https://raw.githubusercontent.com/ProTechEx/PROXY-List/master/http.txt",
            "https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/by-country/DZ.txt",
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

        Assert.Equal(95, feeds.Length);
        Assert.All(feeds, source => Assert.True(
            source.Url.Contains("/countries/", StringComparison.Ordinal) ||
            source.Url.Contains("/by-country/", StringComparison.Ordinal)));
        Assert.DoesNotContain(feeds, source => source.Name is
            "Proxifly country MD" or "Proxifly country UZ" or
            "Proxifly country CY" or "Proxifly country LU" or "HProxy country DZ");
    }

    [Fact]
    public void ExpansionRetainsPublishedProviderFeeds()
    {
        // Four retired core feeds and four retired expansion feeds are excluded.
        var feeds = BuiltInSourceCatalog.Sources.Skip(351).ToArray();

        Assert.Equal(196, feeds.Length);
        Assert.Equal(196, feeds.Select(source => source.ProviderIdentity).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DynamicXyzs996CountryFeedsAreExcludedInFavorOfStableAggregates()
    {
        var feeds = BuiltInSourceCatalog.Sources
            .Where(source => source.Provider == "XYZS996")
            .ToArray();

        Assert.Equal(3, feeds.Length);
        Assert.DoesNotContain(feeds, source => source.Url.Contains("/proxies/countries/", StringComparison.Ordinal));
        Assert.Contains(feeds, source => source.Url.EndsWith("/proxies/all/data.json", StringComparison.Ordinal));
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
    public void CatalogCoversEveryLegacyProtocolAndLimitsUnverifiedProxyTlsToAuditedProxiflyFeeds()
    {
        ProxyProtocol[] legacyProtocols = [ProxyProtocol.Http, ProxyProtocol.Https, ProxyProtocol.Socks4, ProxyProtocol.Socks5];
        Assert.All(legacyProtocols, protocol =>
            Assert.Contains(BuiltInSourceCatalog.Sources, source => source.Protocol == protocol));
        Assert.All(BuiltInSourceCatalog.Sources, source => Assert.True(Enum.IsDefined(source.Protocol)));
        var tlsFeeds = BuiltInSourceCatalog.Sources.Where(source =>
            source.Protocol is ProxyProtocol.HttpTls or ProxyProtocol.HttpTlsUnverified).ToArray();
        Assert.Equal(36, tlsFeeds.Length);
        Assert.All(tlsFeeds, source =>
        {
            Assert.Equal("Proxifly", source.Provider);
            Assert.Equal(ProxyProtocol.HttpTlsUnverified, source.Protocol);
            Assert.True(source.Name == "Proxifly HTTPS" ||
                source.Name.StartsWith("Proxifly country ", StringComparison.Ordinal));
        });
        Assert.Equal(35, tlsFeeds.Count(source => source.Name.StartsWith("Proxifly country ", StringComparison.Ordinal)));
        Assert.Single(tlsFeeds, source => source.Name == "Proxifly HTTPS");
    }

    [Fact]
    public void ProxiflyMixedCountryFeedRetainsExplicitTransportsThroughCompleteSnapshot()
    {
        const string content = """
            http://8.8.8.8:8080
            https://8.8.8.8:8080
            socks4://8.8.8.8:8080
            socks5://8.8.8.8:8080
            """;
        var country = BuiltInSourceCatalog.Sources.First(source => source.Name == "Proxifly country RU");
        var snapshot = ProxyCandidateSnapshotCodec.Encode(content, country.Protocol);
        var decoded = new List<ProxyProtocol>();
        var window = ProxyCandidateSnapshotCodec.ReadWindow(snapshot.Payload, 0, snapshot.Count, key =>
        {
            decoded.Add(key.ToEndpoint().Protocol);
            return true;
        });
        Assert.True(window.Completed);
        Assert.Equal(4, window.NextIndex);
        Assert.Equal(4, snapshot.Count);
        ProxyProtocol[] expected = [ProxyProtocol.Http, ProxyProtocol.Socks4, ProxyProtocol.Socks5, ProxyProtocol.HttpTlsUnverified];
        Assert.Equal(expected, decoded.OrderBy(protocol => protocol));
    }

    [Fact]
    public void GamtUsesCurrentPublishedHttpExport()
    {
        var source = Assert.Single(BuiltInSourceCatalog.Sources, feed => feed.Name == "Proxy List Gamt HTTP");
        Assert.Equal("https://raw.githubusercontent.com/Denisyoya/Proxy-List-Gamt/main/results/txt/http.txt", source.Url);
        Assert.Equal(ProxyProtocol.Http, source.Protocol);
        Assert.DoesNotContain(BuiltInSourceCatalog.Sources, feed => feed.Url.Contains("Proxy-List-Gamt/main/proxy/", StringComparison.Ordinal));
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
