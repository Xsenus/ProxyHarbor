using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;

namespace ProxyHarbor.Tests;

/// <summary>Защищает bounded-семантику кэша keyset-каталога.</summary>
public sealed class PublicOutputCachePolicyTests
{
    [Fact]
    public void CountryIsPartOfEveryProxyPublicationCacheKey()
    {
        Assert.Contains("country", PublicOutputCachePolicies.ListVaryByQuery);
        Assert.Contains("country", PublicOutputCachePolicies.SeekVaryByQuery);
        Assert.Equal(PublicOutputCachePolicies.ListVaryByQuery.Length,
            PublicOutputCachePolicies.ListVaryByQuery.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(PublicOutputCachePolicies.SeekVaryByQuery.Length,
            PublicOutputCachePolicies.SeekVaryByQuery.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void VpnCatalogCacheKeyIncludesEveryPublicFilter()
    {
        Assert.Equal(
            ["page", "pageSize", "protocol", "status", "country"],
            PublicOutputCachePolicies.VpnListVaryByQuery);
        Assert.Equal(PublicOutputCachePolicies.VpnListVaryByQuery.Length,
            PublicOutputCachePolicies.VpnListVaryByQuery.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CatalogCacheExplicitlyRejectsAuthenticatedPrincipalAndVariesByCulture()
    {
        var anonymous = new DefaultHttpContext();
        Assert.True(PublicOutputCachePolicies.IsAnonymous(anonymous));

        var authenticated = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity([], "test"))
        };
        Assert.False(PublicOutputCachePolicies.IsAnonymous(authenticated));

        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(new KeyValuePair<string, string>("ui-culture", "de-DE"),
                PublicOutputCachePolicies.CultureKey(anonymous));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData(typeof(ProxiesController), nameof(ProxiesController.Get), PublicOutputCachePolicies.ProxyCatalog)]
    [InlineData(typeof(ProxiesController), nameof(ProxiesController.Countries), PublicOutputCachePolicies.Countries)]
    [InlineData(typeof(VpnController), nameof(VpnController.Get), PublicOutputCachePolicies.VpnCatalog)]
    [InlineData(typeof(VpnController), nameof(VpnController.Countries), PublicOutputCachePolicies.Countries)]
    public void FrequentPublicEndpointsUseBoundedNamedPolicy(Type controller, string methodName, string policyName)
    {
        var method = controller.GetMethods().Single(method =>
            method.Name == methodName && method.IsPublic &&
            method.GetCustomAttributes<OutputCacheAttribute>().Any());
        var cache = Assert.Single(method.GetCustomAttributes<OutputCacheAttribute>());

        Assert.Equal(policyName, cache.PolicyName);
        Assert.Equal(TimeSpan.FromSeconds(10), PublicOutputCachePolicies.CatalogExpiration);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("?protocol=Socks5&pageSize=100", true)]
    [InlineData("?after=cursor", false)]
    [InlineData("?after=", false)]
    public void OnlyCursorStartIsEligibleForSeekCache(string query, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);

        Assert.Equal(expected, PublicOutputCachePolicies.IsSeekFirstPage(context));
    }

    [Fact]
    public void EntitlementAwareSeekEndpointIsNeverSharedThroughOutputCache()
    {
        var method = typeof(ProxiesController).GetMethod(nameof(ProxiesController.Seek));
        Assert.Empty(method!.GetCustomAttributes<OutputCacheAttribute>());
    }

    [Fact]
    public void ExpensiveMetricsSnapshotHasDedicatedTwoScrapeCacheWindow()
    {
        var method = typeof(MetricsController).GetMethod(nameof(MetricsController.Get));
        var cache = Assert.Single(method!.GetCustomAttributes<OutputCacheAttribute>());

        Assert.Equal(PublicOutputCachePolicies.Metrics, cache.PolicyName);
        Assert.Equal(TimeSpan.FromSeconds(30), PublicOutputCachePolicies.MetricsExpiration);
        Assert.True(PublicOutputCachePolicies.MetricsExpiration >=
            PublicOutputCachePolicies.SummaryExpiration * 2);
    }
}
