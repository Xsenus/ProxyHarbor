using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Личный профиль, доступный любой активной учётной записи.</summary>
[ApiController]
[Authorize]
[Route("api/v1/account")]
[EnableRateLimiting("account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    ProxyHarborDbContext db) : ControllerBase
{
    /// <summary>Возвращает данные профиля и текущего тарифа.</summary>
    [HttpGet("profile")]
    public async Task<IActionResult> Profile()
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        var roles = await users.GetRolesAsync(user);
        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id);
        return Ok(new
        {
            user.Id,
            user.UserName,
            user.Email,
            user.DisplayName,
            user.PreferredLanguage,
            user.CreatedAt,
            user.LastLoginAt,
            roles,
            subscription = subscription is null ? null : new
            {
                subscription.Plan,
                subscription.Status,
                subscription.StartedAt,
                subscription.ExpiresAt
            }
        });
    }

    /// <summary>Меняет безопасные отображаемые данные без изменения email и прав.</summary>
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        if (!SupportedLanguages.IsSupported(request.PreferredLanguage))
            return BadRequest(new ProblemDetails { Title = "Unsupported language", Detail = "Use ru, en, de, fr or zh.", Status = 400 });
        user.PreferredLanguage = SupportedLanguages.Normalize(request.PreferredLanguage);
        var result = await users.UpdateAsync(user);
        return result.Succeeded ? NoContent() : IdentityProblem(result);
    }

    /// <summary>Меняет пароль после проверки текущего и отзывает остальные сессии.</summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded) return IdentityProblem(result);
        // ChangePasswordAsync отзывает старый security stamp. Обновляем только текущую
        // cookie, чтобы остальные браузеры завершили сессии при ближайшей проверке.
        await signIn.RefreshSignInAsync(user);
        return NoContent();
    }

    private static BadRequestObjectResult IdentityProblem(IdentityResult result) =>
        new(new ValidationProblemDetails(result.Errors.GroupBy(x => x.Code, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(error => error.Description).ToArray(), StringComparer.Ordinal))
        { Title = "Не удалось обновить профиль", Status = 400 });
}

/// <summary>Изменяемые пользователем публичные данные профиля.</summary>
public sealed class UpdateProfileRequest
{
    /// <summary>Отображаемое имя или null для очистки.</summary>
    [StringLength(120)] public string? DisplayName { get; set; }
    /// <summary>Двухбуквенный код одного из языков, поддерживаемых продуктом.</summary>
    [Required, StringLength(2, MinimumLength = 2)] public string PreferredLanguage { get; set; } = SupportedLanguages.Default;
}

/// <summary>Подтверждённая смена пароля из личного кабинета.</summary>
public sealed class ChangePasswordRequest
{
    /// <summary>Текущий пароль владельца.</summary>
    [Required, StringLength(256, MinimumLength = 1)] public string CurrentPassword { get; set; } = string.Empty;
    /// <summary>Новый пароль, проходящий серверную Identity policy.</summary>
    [Required, StringLength(256, MinimumLength = 12)] public string NewPassword { get; set; } = string.Empty;
}
