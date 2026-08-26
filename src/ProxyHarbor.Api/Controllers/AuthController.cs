using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Управляет регистрацией, входом, сессией и восстановлением аккаунта.</summary>
[ApiController]
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    ProxyHarborDbContext db,
    IAccountEmailSender emailSender,
    IUserApiTokenService apiTokens,
    ILogger<AuthController> logger) : ControllerBase
{
    private static readonly Action<ILogger, Guid, Exception?> RecoveryEmailFailed = LoggerMessage.Define<Guid>(
        LogLevel.Error, new EventId(1501, "RecoveryEmailFailed"),
        "Не удалось отправить письмо восстановления аккаунта {UserId}");
    /// <summary>Принимает логин или email и выдаёт общую HttpOnly cookie-сессию.</summary>
    [HttpPost("login")]
    [EnableRateLimiting("account-login")]
    public async Task<IActionResult> Login([FromBody] AccountLoginRequest request)
    {
        var user = await FindByIdentifierAsync(request.Username);
        if (user is null || !user.IsActive) return InvalidCredentials();

        var result = await signIn.PasswordSignInAsync(user, request.Password, false, lockoutOnFailure: true);
        if (!result.Succeeded) return InvalidCredentials();

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);
        return Ok(await CreateSessionAsync(user));
    }

    /// <summary>
    /// Обменивает персональный API-токен на HttpOnly cookie. Токен не переносится в URL,
    /// localStorage или последующие браузерные запросы.
    /// </summary>
    [HttpPost("token-login")]
    [EnableRateLimiting("account-login")]
    public async Task<IActionResult> TokenLogin([FromBody] TokenLoginRequest request, CancellationToken token)
    {
        var authentication = await apiTokens.AuthenticateAsync(request.Token.Trim(), token);
        if (authentication is null) return InvalidToken();
        await signIn.SignInAsync(authentication.User, isPersistent: false);
        authentication.User.LastLoginAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(authentication.User);
        return Ok(await CreateSessionAsync(authentication.User));
    }

    /// <summary>Создаёт бесплатный аккаунт и сразу открывает пользовательскую сессию.</summary>
    [HttpPost("register")]
    [EnableRateLimiting("account-register")]
    public async Task<IActionResult> Register([FromBody] RegisterAccountRequest request)
    {
        // Npgsql использует retrying execution strategy. Поэтому вся транзакция,
        // включая Identity-записи и подписку, должна быть единицей повторения.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var user = new ApplicationUser
            {
                UserName = request.Username.Trim(),
                Email = request.Email.Trim(),
                DisplayName = request.DisplayName?.Trim(),
                PreferredLanguage = SupportedLanguages.Normalize(request.PreferredLanguage),
                IsActive = true
            };
            var created = await users.CreateAsync(user, request.Password);
            if (!created.Succeeded) return IdentityProblem(created, "Не удалось создать аккаунт");
            var roleResult = await users.AddToRoleAsync(user, UserRoles.User);
            if (!roleResult.Succeeded) return IdentityProblem(roleResult, "Не удалось назначить права аккаунта");

            db.Subscriptions.Add(new UserSubscription { UserId = user.Id });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            await signIn.SignInAsync(user, isPersistent: false);
            return StatusCode(StatusCodes.Status201Created, await CreateSessionAsync(user));
        });
    }

    /// <summary>Не раскрывая наличие email, отправляет одноразовую ссылку восстановления.</summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting("account-recovery")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!emailSender.IsConfigured)
            return Problem(title: "Восстановление по email временно недоступно", statusCode: 503);

        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is { IsActive: true })
        {
            try
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                await emailSender.SendPasswordResetAsync(user.Email!, token, user.PreferredLanguage, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Ответ одинаков для существующего и неизвестного адреса: email нельзя перечислить.
                RecoveryEmailFailed(logger, user.Id, exception);
            }
        }
        return Accepted(new { message = "Если аккаунт существует, ссылка отправлена на указанную почту." });
    }

    /// <summary>Применяет одноразовый Identity token и отзывает прежние cookie-сессии.</summary>
    [HttpPost("reset-password")]
    [EnableRateLimiting("account-recovery")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive)
            return BadRequest(new ProblemDetails { Title = "Ссылка недействительна или устарела", Status = 400 });
        var result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new ProblemDetails { Title = "Ссылка недействительна или устарела", Status = 400 });
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
        }
        // Восстановление пароля считается событием компрометации учётных данных:
        // вместе с cookie отзываются и все ранее выданные интеграционные токены.
        var activeTokens = await db.UserApiTokens.Where(x => x.UserId == user.Id && x.RevokedAt == null).ToListAsync();
        foreach (var apiToken in activeTokens) apiToken.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await users.UpdateSecurityStampAsync(user);
        return NoContent();
    }

    /// <summary>Возвращает профиль, роли и entitlement-параметры текущей сессии.</summary>
    [HttpGet("session")]
    [Authorize]
    [EnableRateLimiting("account")]
    public async Task<IActionResult> Session()
    {
        var user = await users.GetUserAsync(User);
        return user is null || !user.IsActive ? Unauthorized() : Ok(await CreateSessionAsync(user));
    }

    /// <summary>Инвалидирует cookie текущего браузера.</summary>
    [HttpPost("logout")]
    [Authorize]
    [EnableRateLimiting("account")]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }

    private async Task<ApplicationUser?> FindByIdentifierAsync(string identifier)
    {
        var value = identifier.Trim();
        return value.Contains('@', StringComparison.Ordinal)
            ? await users.FindByEmailAsync(value)
            : await users.FindByNameAsync(value);
    }

    private async Task<object> CreateSessionAsync(ApplicationUser user)
    {
        var roles = await users.GetRolesAsync(user);
        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id);
        var paidAccess = roles.Contains(UserRoles.Administrator, StringComparer.Ordinal) ||
            subscription is { Status: SubscriptionStatuses.Active or SubscriptionStatuses.Trialing } &&
            subscription.Plan is SubscriptionPlans.Pro or SubscriptionPlans.Unlimited &&
            (subscription.ExpiresAt is null || subscription.ExpiresAt > DateTimeOffset.UtcNow);
        return new
        {
            id = user.Id,
            username = user.UserName,
            email = user.Email,
            displayName = user.DisplayName,
            preferredLanguage = user.PreferredLanguage,
            roles,
            subscription = subscription is null ? null : new
            {
                subscription.Plan,
                subscription.Status,
                subscription.StartedAt,
                subscription.ExpiresAt
            },
            entitlements = new { unlimitedProxyAccess = paidAccess }
        };
    }

    private UnauthorizedObjectResult InvalidCredentials() =>
        Unauthorized(new ProblemDetails { Title = "Неверный логин, email или пароль", Status = 401 });

    private UnauthorizedObjectResult InvalidToken() =>
        Unauthorized(new ProblemDetails { Title = "Токен недействителен, отозван или подписка закончилась", Status = 401 });

    private static BadRequestObjectResult IdentityProblem(IdentityResult result, string title) =>
        new(new ValidationProblemDetails(result.Errors.GroupBy(x => x.Code, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(error => error.Description).ToArray(), StringComparer.Ordinal))
        { Title = title, Status = 400 });
}

/// <summary>Bounded-модель входа; Username может содержать логин либо email.</summary>
public sealed class AccountLoginRequest
{
    /// <summary>Логин или email без account enumeration.</summary>
    [Required, StringLength(254, MinimumLength = 3)] public string Username { get; set; } = string.Empty;
    /// <summary>Текущий пароль.</summary>
    [Required, StringLength(256, MinimumLength = 1)] public string Password { get; set; } = string.Empty;
}

/// <summary>API-токен передаётся только в JSON-теле защищённого POST-запроса.</summary>
public sealed class TokenLoginRequest
{
    /// <summary>Полный токен формата ph_live_…</summary>
    [Required, StringLength(160, MinimumLength = 80)] public string Token { get; set; } = string.Empty;
}

/// <summary>Данные регистрации бесплатного аккаунта.</summary>
public sealed class RegisterAccountRequest
{
    /// <summary>Переносимый уникальный логин.</summary>
    [Required, RegularExpression("^[A-Za-z0-9._-]{3,64}$")] public string Username { get; set; } = string.Empty;
    /// <summary>Уникальный адрес для входа и восстановления.</summary>
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = string.Empty;
    /// <summary>Необязательное отображаемое имя.</summary>
    [StringLength(120)] public string? DisplayName { get; set; }
    /// <summary>Пароль, дополнительно проверяемый Identity policy.</summary>
    [Required, StringLength(256, MinimumLength = 12)] public string Password { get; set; } = string.Empty;
    /// <summary>Язык сайта, писем и привязанного Telegram-бота.</summary>
    [Required, StringLength(2, MinimumLength = 2)] public string PreferredLanguage { get; set; } = SupportedLanguages.Default;
}

/// <summary>Запрос ссылки восстановления без account enumeration.</summary>
public sealed class ForgotPasswordRequest
{
    /// <summary>Email потенциального владельца.</summary>
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = string.Empty;
}

/// <summary>Одноразовая смена пароля по Identity token из email.</summary>
public sealed class ResetPasswordRequest
{
    /// <summary>Email из ссылки восстановления.</summary>
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = string.Empty;
    /// <summary>URL-decoded одноразовый token.</summary>
    [Required, StringLength(4096)] public string Token { get; set; } = string.Empty;
    /// <summary>Новый пароль.</summary>
    [Required, StringLength(256, MinimumLength = 12)] public string NewPassword { get; set; } = string.Empty;
}
