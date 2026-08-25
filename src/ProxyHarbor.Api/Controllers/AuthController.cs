using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Создаёт и завершает защищённую cookie-сессию административного интерфейса.</summary>
[ApiController]
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(AdminCredentialValidator credentials) : ControllerBase
{
    /// <summary>Проверяет логин с паролем и выдаёт HttpOnly SameSite-сессию.</summary>
    [HttpPost("login")]
    [EnableRateLimiting("admin-login")]
    public async Task<IActionResult> Login([FromBody] AdminLoginRequest request)
    {
        if (!credentials.Validate(request.Username, request.Password))
            return Unauthorized(new ProblemDetails { Title = "Неверный логин или пароль", Status = 401 });

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, request.Username),
            new Claim(ClaimTypes.Role, "Administrator")
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        return Ok(new { username = request.Username });
    }

    /// <summary>Возвращает состояние текущей административной сессии.</summary>
    [HttpGet("session")]
    [Authorize(Roles = "Administrator")]
    [EnableRateLimiting("admin")]
    public IActionResult Session() => Ok(new { username = User.Identity?.Name });

    /// <summary>Инвалидирует cookie текущего браузера.</summary>
    [HttpPost("logout")]
    [Authorize(Roles = "Administrator")]
    [EnableRateLimiting("admin")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}

/// <summary>Bounded JSON-модель административного входа.</summary>
public sealed record AdminLoginRequest(
    [Required, StringLength(AdminUsernamePolicy.MaximumLength, MinimumLength = AdminUsernamePolicy.MinimumLength)] string Username,
    [Required, StringLength(AdminApiKeyPolicy.MaximumLength, MinimumLength = 1)] string Password);
