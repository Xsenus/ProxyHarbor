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
    public async Task<IActionResult> Profile(CancellationToken token = default)
    {
        if (!TryGetCurrentUserId(out var userId)) return Unauthorized();
        return await BufferedReadSnapshot.ExecuteAsync<IActionResult>(db,
            async (CancellationToken snapshotToken) =>
        {
            // Профиль, подписка, реферальная сводка и username бота возвращаются
            // одним SQL вместо пяти независимых round-trip. Роли и активные токены
            // остаются отдельными ограниченными выборками в том же snapshot.
            var account = await db.Users.AsNoTracking()
                .Where(x => x.Id == userId && x.IsActive)
                .Select(x => new
                {
                    x.Id,
                    x.UserName,
                    x.Email,
                    x.DisplayName,
                    x.PreferredLanguage,
                    x.CreatedAt,
                    x.LastLoginAt,
                    x.ReferralCode,
                    Subscription = x.Subscription == null ? null : new
                    {
                        x.Subscription.Plan,
                        x.Subscription.Status,
                        x.Subscription.StartedAt,
                        x.Subscription.ExpiresAt
                    },
                    ReferralCount = x.Referrals.Count,
                    ReferralRewardDays = x.Referrals.SelectMany(referral => referral.Rewards)
                        .Sum(reward => (int?)reward.DaysGranted) ?? 0,
                    BotUsername = db.TelegramBotConfigurations
                        .Where(configuration => configuration.Id == 1)
                        .Select(configuration => configuration.BotUsername)
                        .SingleOrDefault()
                })
                .SingleOrDefaultAsync(snapshotToken);
            if (account is null) return Unauthorized();

            // Identity store uses only the stable Id for this bounded role lookup.
            // Password hash, security stamp and other private Identity columns therefore
            // never leave PostgreSQL merely to render the profile.
            var roles = await users.GetRolesAsync(new ApplicationUser { Id = account.Id });
            var tokens = await apiTokens.ListAsync(userId, snapshotToken);
            var paidAccess = UserApiTokenService.HasPaidAccess(
                account.Subscription?.Plan,
                account.Subscription?.Status,
                account.Subscription?.ExpiresAt,
                roles,
                DateTimeOffset.UtcNow);
            return Ok(new
            {
                account.Id,
                account.UserName,
                account.Email,
                account.DisplayName,
                account.PreferredLanguage,
                account.CreatedAt,
                account.LastLoginAt,
                account.ReferralCode,
                roles,
                subscription = account.Subscription is null ? null : new
                {
                    account.Subscription.Plan,
                    account.Subscription.Status,
                    account.Subscription.StartedAt,
                    account.Subscription.ExpiresAt
                },
                entitlements = new { unlimitedProxyAccess = paidAccess, apiTokens = paidAccess },
                referral = new
                {
                    code = account.ReferralCode,
                    link = $"{Request.Scheme}://{Request.Host}/register?ref={Uri.EscapeDataString(account.ReferralCode)}",
                    telegramLink = string.IsNullOrWhiteSpace(account.BotUsername) ? null :
                        $"https://t.me/{Uri.EscapeDataString(account.BotUsername)}?start=ref_{Uri.EscapeDataString(account.ReferralCode)}",
                    invited = account.ReferralCount,
                    remaining = Math.Max(0, ReferralRewards.MaximumReferralsPerUser - account.ReferralCount),
                    maximum = ReferralRewards.MaximumReferralsPerUser,
                    rewardDays = account.ReferralRewardDays
                },
                apiTokens = tokens.Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.DisplaySuffix,
                    scopes = x.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    x.CreatedAt,
                    x.LastUsedAt,
                    x.RevokedAt,
                    active = x.RevokedAt is null && paidAccess
                })
            });
        }, token);
    }

    /// <summary>Постранично показывает приглашённых клиентов и каждое начисление владельцу ссылки.</summary>
    [HttpGet("referrals")]
    public async Task<IActionResult> Referrals(
        [FromQuery, Range(1, 100_000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 10,
        CancellationToken token = default)
    {
        if (!TryGetCurrentUserId(out var userId)) return Unauthorized();
        return await BufferedReadSnapshot.ExecuteAsync<IActionResult>(db,
            async (CancellationToken snapshotToken) =>
        {
            // Активность аккаунта и точный total определяются одним запросом.
            var account = await db.Users.AsNoTracking()
                .Where(x => x.Id == userId && x.IsActive)
                .Select(x => new { Total = x.Referrals.Count })
                .SingleOrDefaultAsync(snapshotToken);
            if (account is null) return Unauthorized();

            var items = await db.ReferralRelationships.AsNoTracking()
                .Where(x => x.ReferrerUserId == userId)
                .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new
                {
                    x.Id,
                    x.CreatedAt,
                    user = new { x.ReferredUser.UserName, x.ReferredUser.Email, x.ReferredUser.DisplayName },
                    rewards = x.Rewards.OrderByDescending(r => r.CreatedAt).Select(r => new
                    {
                        r.Id,
                        r.Kind,
                        r.DaysGranted,
                        r.CreatedAt,
                        productCode = r.PaymentOrder == null ? null : r.PaymentOrder.ProductCode,
                        durationDays = r.PaymentOrder == null ? (int?)null : r.PaymentOrder.DurationDays
                    })
                }).ToArrayAsync(snapshotToken);
            return Ok(new { items, page, pageSize, total = account.Total });
        }, token);
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

    /// <summary>
    /// Возвращает постраничную историю запросов владельца. Отозванные токены не
    /// показываются в списке активных, но их аудит остаётся доступен здесь.
    /// </summary>
    [HttpGet("api-tokens/history")]
    public async Task<IActionResult> ApiTokenHistory(
        [FromQuery, Range(1, 100_000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 10,
        [FromQuery] Guid? tokenId = null,
        CancellationToken token = default)
    {
        if (!TryGetCurrentUserId(out var userId)) return Unauthorized();
        return await BufferedReadSnapshot.ExecuteAsync<IActionResult>(db,
            async (CancellationToken snapshotToken) =>
        {
            // Проверка активного владельца и принадлежности фильтра токена не требует
            // двух последовательных lookup-запросов.
            var account = await db.Users.AsNoTracking()
                .Where(x => x.Id == userId && x.IsActive)
                .Select(x => new
                {
                    TokenExists = !tokenId.HasValue || x.ApiTokens.Any(apiToken => apiToken.Id == tokenId.Value)
                })
                .SingleOrDefaultAsync(snapshotToken);
            if (account is null) return Unauthorized();
            if (!account.TokenExists) return NotFound();

            var query = db.UserApiTokenRequests.AsNoTracking().Where(x => x.UserId == userId);
            if (tokenId.HasValue) query = query.Where(x => x.UserApiTokenId == tokenId.Value);
            var total = await query.CountAsync(snapshotToken);
            var items = await query.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new
                {
                    x.Id,
                    token = new { x.UserApiTokenId, x.UserApiToken.Name, x.UserApiToken.DisplaySuffix, x.UserApiToken.RevokedAt },
                    x.IpAddress,
                    x.Method,
                    x.Path,
                    x.Query,
                    x.StatusCode,
                    x.ItemCount,
                    x.DurationMs,
                    x.RequestedAt
                }).ToArrayAsync(snapshotToken);
            return Ok(new { items, page, pageSize, total });
        }, token);
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
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken token = default)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded) return IdentityProblem(result);
        var revokedAt = DateTimeOffset.UtcNow;
        if (db.Database.IsRelational())
        {
            // Один UPDATE независимо от числа токенов вместо SELECT + N UPDATE.
            await db.UserApiTokens.Where(x => x.UserId == user.Id && x.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, revokedAt), token);
        }
        else
        {
            // InMemory используется быстрыми unit-тестами и не реализует ExecuteUpdate.
            var activeTokens = await db.UserApiTokens
                .Where(x => x.UserId == user.Id && x.RevokedAt == null).ToListAsync(token);
            foreach (var apiToken in activeTokens) apiToken.RevokedAt = revokedAt;
            await db.SaveChangesAsync(token);
        }
        // ChangePasswordAsync отзывает старый security stamp. Обновляем только текущую
        // cookie, чтобы остальные браузеры завершили сессии при ближайшей проверке.
        await signIn.RefreshSignInAsync(user);
        return NoContent();
    }

    private bool TryGetCurrentUserId(out Guid userId) =>
        Guid.TryParse(users.GetUserId(User), out userId);

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
