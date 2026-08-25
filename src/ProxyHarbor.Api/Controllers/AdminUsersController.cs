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
    public async Task<ActionResult<PagedResult<AdminUserResponse>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? activity = null,
        [FromQuery] string? plan = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        search = search?.Trim();
        if (search?.Length > 200)
            return BadRequest(new ProblemDetails { Title = "Поисковый запрос слишком длинный", Status = 400 });
        if (activity is not null && activity is not ("active" or "disabled"))
            return BadRequest(new ProblemDetails { Title = "Фильтр активности недопустим", Status = 400 });
        if (plan is not null && !SubscriptionPlans.All.Contains(plan, StringComparer.Ordinal))
            return BadRequest(new ProblemDetails { Title = "Фильтр тарифа недопустим", Status = 400 });

        // Фильтры применяются до Count/Skip/Take, поэтому браузер никогда не получает
        // полный реестр пользователей и страница сохраняет постоянный объём работы.
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            if (db.Database.ProviderName?.Contains("InMemory", StringComparison.Ordinal) == true)
                query = query.Where(user =>
                    (user.UserName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (user.Email ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (user.DisplayName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));
            else
            {
                var pattern = $"%{search}%";
                query = query.Where(user =>
                    EF.Functions.ILike(user.UserName ?? string.Empty, pattern) ||
                    EF.Functions.ILike(user.Email ?? string.Empty, pattern) ||
                    EF.Functions.ILike(user.DisplayName ?? string.Empty, pattern));
            }
        }
        if (activity == "active") query = query.Where(user => user.IsActive);
        if (activity == "disabled") query = query.Where(user => !user.IsActive);
        if (plan is not null)
        {
            query = plan == SubscriptionPlans.Free
                ? query.Where(user => !db.Subscriptions.Any(item => item.UserId == user.Id) ||
                                      db.Subscriptions.Any(item => item.UserId == user.Id && item.Plan == plan))
                : query.Where(user => db.Subscriptions.Any(item => item.UserId == user.Id && item.Plan == plan));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var ids = rows.Select(x => x.Id).ToArray();
        var subscriptions = await db.Subscriptions.AsNoTracking().Where(x => ids.Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, cancellationToken);

        // Роли всей страницы извлекаются одним запросом вместо N+1 запросов через
        // UserManager.GetRolesAsync для каждой строки.
        var roleRows = await db.UserRoles.AsNoTracking()
            .Where(link => ids.Contains(link.UserId))
            .Join(db.Roles.AsNoTracking(), link => link.RoleId, role => role.Id,
                (link, role) => new { link.UserId, role.Name })
            .ToListAsync(cancellationToken);
        var rolesByUser = roleRows.GroupBy(row => row.UserId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Name!)
                .Where(name => !string.IsNullOrWhiteSpace(name)).OrderBy(name => name).ToArray());

        var result = new List<AdminUserResponse>(rows.Count);
        foreach (var user in rows)
        {
            subscriptions.TryGetValue(user.Id, out var subscription);
            rolesByUser.TryGetValue(user.Id, out var roles);
            result.Add(new AdminUserResponse(user.Id, user.UserName ?? string.Empty,
                user.Email ?? string.Empty, user.DisplayName, user.IsActive, user.CreatedAt,
                user.LastLoginAt, roles ?? [], subscription is null ? null :
                    new AdminUserSubscriptionResponse(subscription.Plan, subscription.Status,
                        subscription.StartedAt, subscription.ExpiresAt)));
        }
        return Ok(new PagedResult<AdminUserResponse>(result, page, pageSize, total));
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

/// <summary>Безопасная строка административного реестра пользователей.</summary>
public sealed record AdminUserResponse(Guid Id, string UserName, string Email, string? DisplayName,
    bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, string[] Roles,
    AdminUserSubscriptionResponse? Subscription);

/// <summary>Коммерческий доступ пользователя без платёжных реквизитов.</summary>
public sealed record AdminUserSubscriptionResponse(string Plan, string Status,
    DateTimeOffset StartedAt, DateTimeOffset? ExpiresAt);

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
