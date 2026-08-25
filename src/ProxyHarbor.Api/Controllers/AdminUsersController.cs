using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Административное управление ролями и коммерческими entitlement-параметрами.</summary>
[ApiController]
[Route("api/v1/admin/users")]
[Authorize(Roles = UserRoles.Administrator)]
[EnableRateLimiting("admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminUsersController(
    UserManager<ApplicationUser> users,
    ProxyHarborDbContext db) : ControllerBase
{
    /// <summary>Возвращает bounded-страницу аккаунтов без password/token/security stamp.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<object>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var total = await db.Users.CountAsync();
        var rows = await db.Users.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var ids = rows.Select(x => x.Id).ToArray();
        var subscriptions = await db.Subscriptions.AsNoTracking().Where(x => ids.Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId);
        var result = new List<object>(rows.Count);
        foreach (var user in rows)
        {
            var roles = await users.GetRolesAsync(user);
            subscriptions.TryGetValue(user.Id, out var subscription);
            result.Add(new
            {
                user.Id,
                user.UserName,
                user.Email,
                user.DisplayName,
                user.IsActive,
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
        return Ok(new PagedResult<object>(result, page, pageSize, total));
    }

    /// <summary>Атомарно обновляет активность, роли и тариф выбранной учётной записи.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserAccessRequest request)
    {
        var requestedRoles = request.Roles.Distinct(StringComparer.Ordinal).ToArray();
        if (requestedRoles.Length == 0 || requestedRoles.Any(role => !UserRoles.All.Contains(role, StringComparer.Ordinal)) ||
            !requestedRoles.Contains(UserRoles.User, StringComparer.Ordinal))
            return BadRequest(new ProblemDetails { Title = "Набор ролей недопустим", Status = 400 });
        if (!SubscriptionPlans.All.Contains(request.Plan, StringComparer.Ordinal) ||
            !SubscriptionStatuses.All.Contains(request.Status, StringComparer.Ordinal))
            return BadRequest(new ProblemDetails { Title = "Тариф или статус подписки недопустим", Status = 400 });

        // Роли, активность, security stamp и подписка изменяются одной повторяемой
        // serializable-транзакцией, совместимой с Npgsql retrying strategy.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return NotFound();
            var currentRoles = await users.GetRolesAsync(user);
            if (currentRoles.Contains(UserRoles.Administrator, StringComparer.Ordinal) &&
                (!requestedRoles.Contains(UserRoles.Administrator, StringComparer.Ordinal) || !request.IsActive))
            {
                var administrators = await users.GetUsersInRoleAsync(UserRoles.Administrator);
                if (administrators.Count(candidate => candidate.IsActive) <= 1)
                    return Conflict(new ProblemDetails
                    {
                        Title = "Нельзя отключить или лишить прав последнего активного администратора",
                        Status = 409
                    });
            }

            user.IsActive = request.IsActive;
            var updateResult = await users.UpdateAsync(user);
            if (!updateResult.Succeeded) return IdentityProblem(updateResult);
            var removed = await users.RemoveFromRolesAsync(user, currentRoles.Except(requestedRoles, StringComparer.Ordinal));
            if (!removed.Succeeded) return IdentityProblem(removed);
            var added = await users.AddToRolesAsync(user, requestedRoles.Except(currentRoles, StringComparer.Ordinal));
            if (!added.Succeeded) return IdentityProblem(added);

            var subscription = await db.Subscriptions.SingleOrDefaultAsync(x => x.UserId == id);
            if (subscription is null)
            {
                subscription = new UserSubscription { UserId = id };
                db.Subscriptions.Add(subscription);
            }
            subscription.Plan = request.Plan;
            subscription.Status = request.Status;
            subscription.ExpiresAt = request.ExpiresAt;
            subscription.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            var stampResult = await users.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded) return IdentityProblem(stampResult);
            await transaction.CommitAsync();
            return NoContent();
        });
    }

    private static BadRequestObjectResult IdentityProblem(IdentityResult result) =>
        new(new ValidationProblemDetails(result.Errors.GroupBy(x => x.Code, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(error => error.Description).ToArray(), StringComparer.Ordinal))
        { Title = "Не удалось обновить права пользователя", Status = 400 });
}

/// <summary>Полный желаемый снимок прав и подписки выбранного аккаунта.</summary>
public sealed class UpdateUserAccessRequest
{
    /// <summary>Разрешён ли новый вход.</summary>
    public bool IsActive { get; set; }
    /// <summary>Роли из закрытого списка UserRoles.</summary>
    [Required, MinLength(1), MaxLength(3)] public string[] Roles { get; set; } = [];
    /// <summary>Код тарифа.</summary>
    [Required, StringLength(32)] public string Plan { get; set; } = string.Empty;
    /// <summary>Состояние подписки.</summary>
    [Required, StringLength(32)] public string Status { get; set; } = string.Empty;
    /// <summary>Конец доступа или null для бессрочного/free.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
