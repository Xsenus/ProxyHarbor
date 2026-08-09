using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует аутентификацию admin API и заголовки прямого HTTP-периметра.</summary>
public sealed class ApiSecurityMiddlewareTests
{
    private const string AdminKey = "integration-admin-key-at-least-24-chars";

    [Fact]
    public async Task MissingAdminKeyReturnsNonCacheableChallengeAndSecurityHeaders()
    {
        var nextCalled = false;
        var context = Context("/api/v1/admin/sources");
        var pipeline = Pipeline(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await pipeline(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("ApiKey realm=\"ProxyHarbor\"", context.Response.Headers.WWWAuthenticate);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        AssertSecurityHeaders(context.Response.Headers);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(401, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task CorrectAdminKeyPassesWithoutLeakingItIntoResponse()
    {
        var context = Context("/api/v1/admin/sources");
        context.Request.Headers["X-Admin-Key"] = AdminKey;
        var pipeline = Pipeline(async httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            await httpContext.Response.StartAsync();
        });

        await pipeline(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.DoesNotContain(AdminKey, string.Join('\n', context.Response.Headers), StringComparison.Ordinal);
        AssertSecurityHeaders(context.Response.Headers);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData(AdminKey + "-suffix")]
    public async Task DifferentLengthAndPrefixKeysAreRejected(string providedKey)
    {
        var nextCalled = false;
        var context = Context("/api/v1/admin/sources");
        context.Request.Headers["X-Admin-Key"] = providedKey;
        var pipeline = Pipeline(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await pipeline(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/proxies")]
    [InlineData("/api/v1/administrator")]
    public async Task NonAdminRoutesDoNotRequireKey(string path)
    {
        var nextCalled = false;
        var context = Context(path);
        var pipeline = Pipeline(async httpContext =>
        {
            nextCalled = true;
            await httpContext.Response.WriteAsync("ok");
        });

        await pipeline(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertSecurityHeaders(context.Response.Headers);
    }

    private static RequestDelegate Pipeline(RequestDelegate terminal)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:AdminApiKey"] = AdminKey
        }).Build();
        var admin = new AdminApiKeyMiddleware(terminal, configuration);
        var security = new SecurityHeadersMiddleware(admin.InvokeAsync);
        return security.InvokeAsync;
    }

    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static void AssertSecurityHeaders(IHeaderDictionary headers)
    {
        Assert.Equal("nosniff", headers.XContentTypeOptions);
        Assert.Equal("DENY", headers.XFrameOptions);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
        Assert.Equal("camera=(), microphone=(), geolocation=()", headers["Permissions-Policy"]);
        Assert.Equal("same-origin", headers["Cross-Origin-Opener-Policy"]);
        Assert.Equal("none", headers["X-Permitted-Cross-Domain-Policies"]);
        Assert.Equal("default-src 'none'; base-uri 'none'; frame-ancestors 'none'", headers.ContentSecurityPolicy);
    }
}
