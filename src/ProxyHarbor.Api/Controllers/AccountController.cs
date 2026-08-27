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
    ProxyHarborDbContext db,
    IUserApiTokenService apiTokens) : ControllerBase
{
    /// <summary>Возвращает данные профиля и текущего тарифа.</summary>
    [HttpGet("profile")]
    public async Task<IActionResult> Profile()
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        var roles = await users.GetRolesAsync(user);
        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id);
        var paidAccess = UserApiTokenService.HasPaidAccess(subscription, roles, DateTimeOffset.UtcNow);
        var tokens = await apiTokens.ListAsync(user.Id, HttpContext.RequestAborted);
        var referralCount = await db.ReferralRelationships.CountAsync(x => x.ReferrerUserId == user.Id);
        var referralRewardDays = await db.ReferralRewards
            .Where(x => x.ReferralRelationship.ReferrerUserId == user.Id)
            .SumAsync(x => (int?)x.DaysGranted) ?? 0;
        return Ok(new
        {
            user.Id,
            user.UserName,
            user.Email,
            user.DisplayName,
            user.PreferredLanguage,
            user.CreatedAt,
            user.LastLoginAt,
            user.ReferralCode,
            roles,
            subscription = subscription is null ? null : new
            {
                subscription.Plan,
                subscription.Status,
                subscription.StartedAt,
                subscription.ExpiresAt
            },
            entitlements = new { unlimitedProxyAccess = paidAccess, apiTokens = paidAccess },
            referral = new
            {
                code = user.ReferralCode,
                link = $"{Request.Scheme}://{Request.Host}/register?ref={Uri.EscapeDataString(user.ReferralCode)}",
                invited = referralCount,
                remaining = Math.Max(0, ReferralRewards.MaximumReferralsPerUser - referralCount),
                maximum = ReferralRewards.MaximumReferralsPerUser,
                rewardDays = referralRewardDays
            },
            apiTokens = tokens.Select(x => new
            {
                x.Id, x.Name, x.DisplaySuffix, scopes = x.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                x.CreatedAt, x.LastUsedAt, x.RevokedAt, active = x.RevokedAt is null && paidAccess
            })
        });
    }

    /// <summary>Постранично показывает приглашённых клиентов и каждое начисление владельцу ссылки.</summary>
    [HttpGet("referrals")]
    public async Task<IActionResult> Referrals(
        [FromQuery, Range(1, 100_000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 10,
        CancellationToken token = default)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        var query = db.ReferralRelationships.AsNoTracking().Where(x => x.ReferrerUserId == user.Id);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.CreatedAt,
                user = new { x.ReferredUser.UserName, x.ReferredUser.Email, x.ReferredUser.DisplayName },
                rewards = x.Rewards.OrderByDescending(r => r.CreatedAt).Select(r => new
                {
                    r.Id, r.Kind, r.DaysGranted, r.CreatedAt,
                    productCode = r.PaymentOrder == null ? null : r.PaymentOrder.ProductCode,
                    durationDays = r.PaymentOrder == null ? (int?)null : r.PaymentOrder.DurationDays
                })
            }).ToArrayAsync(token);
        return Ok(new { items, page, pageSize, total });
    }

    /// <summary>Выпускает токен и показывает полный секрет ровно один раз.</summary>
    [HttpPost("api-tokens")]
    public async Task<IActionResult> IssueApiToken([FromBody] IssueApiTokenRequest request, CancellationToken token)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        try { return StatusCode(StatusCodes.Status201Created, await apiTokens.IssueAsync(user.Id, request.Name, token)); }
        catch (UnauthorizedAccessException exception)
        { return Problem(title: exception.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (InvalidOperationException exception)
        { return Problem(title: exception.Message, statusCode: StatusCodes.Status409Conflict); }
    }

    /// <summary>Необратимо отзывает выбранный токен; повторный вызов идемпотентен.</summary>
    [HttpDelete("api-tokens/{tokenId:guid}")]
    public async Task<IActionResult> RevokeApiToken(Guid tokenId, CancellationToken token)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        await apiTokens.RevokeAsync(user.Id, tokenId, token);
        return NoContent();
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
        var activeTokens = await db.UserApiTokens.Where(x => x.UserId == user.Id && x.RevokedAt == null).ToListAsync();
        foreach (var apiToken in activeTokens) apiToken.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
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

/// <summary>Название новой интеграции, показываемое рядом с безопасным суффиксом.</summary>
public sealed class IssueApiTokenRequest
{
    /// <summary>Например «Рабочий сервер» или «Мой скрипт».</summary>
    [StringLength(80)] public string Name { get; set; } = "Основной API-токен";
}
