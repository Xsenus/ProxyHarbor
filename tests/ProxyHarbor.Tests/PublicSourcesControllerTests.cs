using Microsoft.AspNetCore.Mvc;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует публичный контракт полного встроенного source-каталога.</summary>
public sealed class PublicSourcesControllerTests
{
    [Fact]
    public void CatalogGroupsEveryFeedByIndependentProviderInRankOrder()
    {
        var response = Assert.IsType<PublicSourceCatalogResponse>(
            Assert.IsType<OkObjectResult>(new SourcesController().Get().Result).Value);

        Assert.Equal(BuiltInSourceCatalog.LastAuditedOn, response.LastAuditedOn);
        Assert.Equal(81, response.FeedCount);
        Assert.Equal(50, response.ProviderCount);
        Assert.Equal(50, response.Providers.Count);
        Assert.Equal(81, response.Providers.Sum(provider => provider.Feeds.Count));
        Assert.Equal(Enumerable.Range(1, 50), response.Providers.Select(provider => provider.Rank));
        Assert.Equal(50, response.Providers.Select(provider => provider.Name)
            .Distinct(StringComparer.Ordinal).Count());

        Assert.All(response.Providers, provider =>
        {
            Assert.NotEmpty(provider.Name);
            Assert.NotEmpty(provider.Protocols);
            Assert.NotEmpty(provider.Feeds);
            Assert.Equal(
                provider.Feeds.Select(feed => feed.Protocol).Distinct(),
                provider.Protocols);
            Assert.All(provider.Feeds, feed =>
            {
                Assert.True(Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri));
                Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
                Assert.Same(BuiltInSourceCatalog.FindByUrl(feed.Url),
                    BuiltInSourceCatalog.Sources.Single(source => source.Rank == feed.Rank));
            });
        });
    }
}
