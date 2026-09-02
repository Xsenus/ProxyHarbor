using System.Security.Claims;
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
        Assert.Equal("Cookie, ApiKey realm=\"ProxyHarbor\"", context.Response.Headers.WWWAuthenticate);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.Equal("0", context.Response.Headers.Expires);
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
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.Equal("ApiKey", context.User.Identity?.AuthenticationType);
        Assert.True(context.User.IsInRole("Administrator"));
        Assert.DoesNotContain(AdminKey, string.Join('\n', context.Response.Headers), StringComparison.Ordinal);
        AssertSecurityHeaders(context.Response.Headers);
    }

    [Fact]
    public async Task CorrectAdminKeyAuthenticatesOperationalExportAsAdministrator()
    {
        var context = Context("/api/v1/export/json");
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers["X-Admin-Key"] = AdminKey;
        var nextCalled = false;
        var pipeline = Pipeline(httpContext =>
        {
            nextCalled = true;
            Assert.True(httpContext.User.IsInRole("Administrator"));
            return Task.CompletedTask;
        });

        await pipeline(context);

        Assert.True(nextCalled);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.True(context.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task PublicExportWithoutAdminKeyRemainsAnonymous()
    {
        var context = Context("/api/v1/export/json");
        context.Request.Method = HttpMethods.Get;
        var nextCalled = false;
        var pipeline = Pipeline(httpContext =>
        {
            nextCalled = true;
            Assert.False(httpContext.User.Identity?.IsAuthenticated);
            return Task.CompletedTask;
        });

        await pipeline(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task AdministratorCookieOperationalExportIsNotCacheable()
    {
        var context = Context("/api/v1/export/json");
        context.Request.Method = HttpMethods.Get;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "Administrator")
        ], "Cookies"));
        var nextCalled = false;
        var pipeline = Pipeline(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await pipeline(context);

        Assert.True(nextCalled);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
    }

    [Fact]
    public async Task InvalidAdminKeyCannotDowngradeOperationalExportToFreeAccess()
    {
        var context = Context("/api/v1/export/json");
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers["X-Admin-Key"] = "incorrect-operational-export-key";
        var pipeline = Pipeline(_ => throw new InvalidOperationException("invalid key must not reach export"));

        await pipeline(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task AuthenticatedAdministratorCookiePrincipalPassesWithoutApiKeyHeader()
    {
        var context = Context("/api/v1/admin/diagnostics");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "Administrator")
        ], "Cookies"));
        var nextCalled = false;
        var pipeline = Pipeline(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await pipeline(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task AuthenticatedUserWithoutAdministratorRoleReceivesForbidden()
    {
        var context = Context("/api/v1/admin/diagnostics");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "customer"),
            new Claim(ClaimTypes.Role, "User")
        ], "Cookies"));

        await Pipeline(_ => throw new InvalidOperationException("next must not run"))(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"));
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("Недостаточно прав", json.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task MultipleHeaderValuesCannotAuthenticateCommaContainingKey()
    {
        var context = Context("/api/v1/admin/sources");
        context.Request.Headers.Append("X-Admin-Key", "first");
        context.Request.Headers.Append("X-Admin-Key", "second");
        var nextCalled = false;
        var pipeline = Pipeline(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, "first,second");

        await pipeline(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task OversizedHeaderIsRejectedBeforeAuthentication()
    {
        var context = Context("/api/v1/admin/sources");
        context.Request.Headers["X-Admin-Key"] = new string('x', 257);
        var pipeline = Pipeline(_ => throw new InvalidOperationException("oversized key must not pass"));

        await pipeline(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidUnicodeHeaderIsRejectedWithoutReplacementCharacterCollision()
    {
        var context = Context("/api/v1/admin/sources");
        context.Request.Headers["X-Admin-Key"] = new string('\uD800', 24);
        var pipeline = Pipeline(_ => throw new InvalidOperationException("invalid UTF-16 key must not pass"));

        await pipeline(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public void ProductionKeyPolicyRejectsUnusableSecrets()
    {
        Assert.False(AdminApiKeyPolicy.IsValid(null));
        Assert.False(AdminApiKeyPolicy.IsValid(new string(' ', 24)));
        Assert.False(AdminApiKeyPolicy.IsValid(new string('\uD800', 24)));
        Assert.False(AdminApiKeyPolicy.IsValid(new string('x', 23)));
        Assert.False(AdminApiKeyPolicy.IsValid(new string('x', 257)));
        Assert.True(AdminApiKeyPolicy.IsValid(AdminKey));
        Assert.True(AdminApiKeyPolicy.IsValid("ключ-администратора-длиной-больше-24"));
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

    private static RequestDelegate Pipeline(RequestDelegate terminal, string adminKey = AdminKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:AdminApiKey"] = adminKey
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
        Assert.Equal("same-origin", headers["Cross-Origin-Resource-Policy"]);
        Assert.Equal("none", headers["X-Permitted-Cross-Domain-Policies"]);
        Assert.Equal("max-age=31536000", headers.StrictTransportSecurity);
        Assert.Equal("default-src 'none'; base-uri 'none'; frame-ancestors 'none'", headers.ContentSecurityPolicy);
    }
}
