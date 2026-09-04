using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

public sealed class CatalogOutputCacheTransportTests
{
    [Theory]
    [InlineData(PublicOutputCachePolicies.ProxyCatalog)]
    [InlineData(PublicOutputCachePolicies.VpnCatalog)]
    public async Task WarmAnonymousCacheCannotHideInvalidPageSize(string policyName)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddOutputCache(options => options.AddPolicy(policyName, policy => policy
            .With(context => PublicOutputCachePolicies.IsAnonymousFirstPage(context.HttpContext))
            .Expire(PublicOutputCachePolicies.CatalogExpiration)
            .SetVaryByQuery(policyName == PublicOutputCachePolicies.ProxyCatalog
                ? PublicOutputCachePolicies.ListVaryByQuery : PublicOutputCachePolicies.VpnListVaryByQuery)));
        await using var app = builder.Build();
        app.UseOutputCache();
        var executions = 0;
        // Same integer query-binding contract as the catalogs; a free response
        // always normalizes pagination. No DB or production application is started.
        app.MapGet("/catalog", (int page, int pageSize) =>
        {
            Interlocked.Increment(ref executions);
            return Results.Ok(new { Page = 1, PageSize = 10 });
        }).CacheOutput(policyName);
        await app.StartAsync();
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10)
        };
        using var warm = await client.GetAsync("/catalog?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        using var cached = await client.GetAsync("/catalog?page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.Equal(1, Volatile.Read(ref executions));
        using var invalid = await client.GetAsync("/catalog?page=1&pageSize=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var overflow = await client.GetAsync("/catalog?page=1&pageSize=2147483648");
        Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);
        using var validAgain = await client.GetAsync("/catalog?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, validAgain.StatusCode);
        Assert.Equal(1, Volatile.Read(ref executions));
    }
}
