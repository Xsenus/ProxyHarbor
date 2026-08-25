using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет выдачу и завершение browser-сессии без реального cookie handler.</summary>
public sealed class AuthControllerTests
{
    private const string Password = "browser-admin-password-at-least-24-chars";

    [Fact]
    public async Task ValidCredentialsCreateAdministratorSession()
    {
        var authentication = new RecordingAuthenticationService();
        var controller = CreateController(authentication);

        var result = await controller.Login(new AdminLoginRequest("admin", Password));

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(authentication.SignedInPrincipal);
        Assert.Equal("admin", authentication.SignedInPrincipal.Identity?.Name);
        Assert.True(authentication.SignedInPrincipal.IsInRole("Administrator"));
        Assert.False(authentication.Properties?.IsPersistent);
    }

    [Fact]
    public async Task InvalidCredentialsReturnGenericUnauthorizedWithoutSession()
    {
        var authentication = new RecordingAuthenticationService();
        var controller = CreateController(authentication);

        var result = await controller.Login(new AdminLoginRequest("admin", "wrong"));

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(unauthorized.Value);
        Assert.Equal("Неверный логин или пароль", problem.Title);
        Assert.Null(authentication.SignedInPrincipal);
    }

    [Fact]
    public async Task LogoutRemovesCurrentSessionAndSessionReturnsUsername()
    {
        var authentication = new RecordingAuthenticationService();
        var controller = CreateController(authentication);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")], "Cookies"));

        var session = Assert.IsType<OkObjectResult>(controller.Session());
        Assert.Contains("admin", session.Value?.ToString(), StringComparison.Ordinal);
        Assert.IsType<NoContentResult>(await controller.Logout());
        Assert.True(authentication.SignedOut);
    }

    private static AuthController CreateController(RecordingAuthenticationService authentication)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:AdminUsername"] = "admin",
            ["Security:AdminPassword"] = Password
        }).Build();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        return new AuthController(new AdminCredentialValidator(configuration))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services }
            }
        };
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        internal ClaimsPrincipal? SignedInPrincipal { get; private set; }
        internal AuthenticationProperties? Properties { get; private set; }
        internal bool SignedOut { get; private set; }

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignedInPrincipal = principal;
            Properties = properties;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOut = true;
            return Task.CompletedTask;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }
}
