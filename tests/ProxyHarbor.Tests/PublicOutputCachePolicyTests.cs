using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;

namespace ProxyHarbor.Tests;

/// <summary>Защищает bounded-семантику кэша keyset-каталога.</summary>
public sealed class PublicOutputCachePolicyTests
{
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
    public void SeekEndpointUsesBoundedFirstPagePolicy()
    {
        var method = typeof(ProxiesController).GetMethod(nameof(ProxiesController.Seek));
        var attribute = Assert.Single(method!.GetCustomAttributes<OutputCacheAttribute>());

        Assert.Equal(PublicOutputCachePolicies.SeekFirstPage, attribute.PolicyName);
    }
}
