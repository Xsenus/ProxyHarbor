using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Серверная выборка счетов для общей формы и карточек провайдеров.</summary>
[ApiController, Route("api/v1/admin/payments/orders"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminPaymentOrdersController(ProxyHarborDbContext db) : ControllerBase
{
    /// <summary>Возвращает фильтруемую страницу счетов и глобальную сводку статусов.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string? status = null, [FromQuery] string? provider = null,
        [FromQuery] string? query = null, CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var orders = db.PaymentOrders.AsNoTracking().Include(x => x.User).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!PaymentStatuses.All.Contains(status, StringComparer.Ordinal)) return BadRequest();
            orders = orders.Where(x => x.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(provider))
        {
            if (!PaymentProviderConfiguration.Codes.Contains(provider, StringComparer.Ordinal)) return BadRequest();
            orders = orders.Where(x => x.Provider == provider);
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            orders = orders.Where(x => x.User.UserName!.Contains(term) || x.User.Email!.Contains(term) ||
                x.ProviderPaymentId != null && x.ProviderPaymentId.Contains(term));
        }
        return await BufferedReadSnapshot.ExecuteAsync(db, async _ =>
        {
            var total = await orders.CountAsync(token);
            var items = await orders.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new
                {
                    x.Id,
                    x.UserId,
                    x.User.UserName,
                    x.User.Email,
                    x.ProductCode,
                    x.Plan,
                    x.Provider,
                    x.PaymentMethod,
                    x.PaymentInstrument,
                    x.AmountMinor,
                    x.Currency,
                    x.Status,
                    x.ProviderPaymentId,
                    x.CreatedAt,
                    x.PaidAt,
                    x.UpdatedAt
                }).ToListAsync(token);
            var summary = await db.PaymentOrders.AsNoTracking().GroupBy(x => x.Status)
                .Select(x => new { status = x.Key, count = x.Count(), amountMinor = x.Sum(y => y.AmountMinor) })
                .ToListAsync(token);
            return (IActionResult)Ok(new { items, page, pageSize, total, summary });
        }, token);
    }
}

/// <summary>Отдельный административный реестр подписок с аудитом ручных изменений.</summary>
[ApiController, Route("api/v1/admin/subscriptions"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminSubscriptionsController(ProxyHarborDbContext db, UserManager<ApplicationUser> users) : ControllerBase
{
    /// <summary>Возвращает страницу подписок и оперативные показатели.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string? status = null, [FromQuery] string? plan = null,
        [FromQuery] string? query = null, CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var rows = db.Subscriptions.AsNoTracking().Include(x => x.User).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(plan)) rows = rows.Where(x => x.Plan == plan);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            rows = rows.Where(x => x.User.UserName!.Contains(term) || x.User.Email!.Contains(term));
        }
        return await BufferedReadSnapshot.ExecuteAsync(db, async _ =>
        {
            var now = DateTimeOffset.UtcNow;
            var total = await rows.CountAsync(token);
            var items = await rows.OrderByDescending(x => x.Status == SubscriptionStatuses.Active)
                .ThenBy(x => x.ExpiresAt).ThenBy(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new
                {
                    x.Id,
                    x.UserId,
                    x.User.UserName,
                    x.User.Email,
                    x.User.DisplayName,
                    x.Plan,
                    x.Status,
                    x.StartedAt,
                    x.ExpiresAt,
                    x.UpdatedAt
                }).ToListAsync(token);
            var summary = await db.Subscriptions.AsNoTracking().GroupBy(_ => 1)
                .Select(group => new
                {
                    active = group.Count(x => x.Status == SubscriptionStatuses.Active),
                    trialing = group.Count(x => x.Status == SubscriptionStatuses.Trialing),
                    suspended = group.Count(x => x.Status == SubscriptionStatuses.Suspended),
                    expiringSoon = group.Count(x => x.Status == SubscriptionStatuses.Active &&
                        x.ExpiresAt >= now && x.ExpiresAt <= now.AddDays(7))
                }).SingleOrDefaultAsync(token);
            return (IActionResult)Ok(new
            {
                items,
                page,
                pageSize,
                total,
                summary = summary ?? new { active = 0, trialing = 0, suspended = 0, expiringSoon = 0 }
            });
        }, token);
    }

    /// <summary>Изменяет, продлевает или приостанавливает подписку с записью аудита.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSubscriptionRequest request, CancellationToken token)
    {
        if (!SubscriptionPlans.All.Contains(request.Plan, StringComparer.Ordinal) ||
            !SubscriptionStatuses.All.Contains(request.Status, StringComparer.Ordinal) ||
            request.ExtensionDays is < -3660 or > 3660)
            return BadRequest(new ProblemDetails { Title = "Параметры подписки недопустимы", Status = 400 });
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            var subscription = await db.Subscriptions.SingleOrDefaultAsync(x => x.Id == id, token);
            if (subscription is null) return NotFound();
            var previousPlan = subscription.Plan;
            var previousStatus = subscription.Status;
            var previousExpires = subscription.ExpiresAt;
            var now = DateTimeOffset.UtcNow;
            var expires = request.ExpiresAt;
            if (request.ExtensionDays != 0)
                expires = (subscription.ExpiresAt.HasValue && subscription.ExpiresAt.Value > now
                    ? subscription.ExpiresAt.Value : now).AddDays(request.ExtensionDays);
            if (expires < subscription.StartedAt)
                return BadRequest(new ProblemDetails { Title = "Конец подписки не может быть раньше её начала", Status = 400 });
            subscription.Plan = request.Plan;
            subscription.Status = request.Status;
            subscription.ExpiresAt = expires;
            subscription.UpdatedAt = now;
            db.SubscriptionAdminActions.Add(new SubscriptionAdminAction
            {
                SubscriptionId = subscription.Id,
                AdministratorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
                Action = request.ExtensionDays == 0 ? "update" : "extend",
                PreviousPlan = previousPlan,
                PreviousStatus = previousStatus,
                PreviousExpiresAt = previousExpires,
                NewPlan = subscription.Plan,
                NewStatus = subscription.Status,
                NewExpiresAt = subscription.ExpiresAt,
                Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim()
            });
            await db.SaveChangesAsync(token);
            var user = await users.FindByIdAsync(subscription.UserId.ToString());
            if (user is not null)
            {
                var shouldSubscribe = subscription.Plan != SubscriptionPlans.Free &&
                    subscription.Status is SubscriptionStatuses.Active or SubscriptionStatuses.Trialing;
                var hasRole = await users.IsInRoleAsync(user, UserRoles.Subscriber);
                var roleResult = shouldSubscribe && !hasRole
                    ? await users.AddToRoleAsync(user, UserRoles.Subscriber)
                    : !shouldSubscribe && hasRole
                        ? await users.RemoveFromRoleAsync(user, UserRoles.Subscriber)
                        : IdentityResult.Success;
                if (!roleResult.Succeeded)
                    return Problem("Не удалось синхронизировать роль Subscriber.", statusCode: 500);
                var stampResult = await users.UpdateSecurityStampAsync(user);
                if (!stampResult.Succeeded)
                    return Problem("Не удалось завершить обновление защищённой сессии.", statusCode: 500);
            }
            if (transaction is not null) await transaction.CommitAsync(token);
            return NoContent();
        });
    }
}

/// <summary>Команда ручного изменения срока и состояния подписки.</summary>
public sealed class UpdateSubscriptionRequest
{
    /// <summary>Новый тариф.</summary>
    [Required, StringLength(32)] public string Plan { get; set; } = string.Empty;
    /// <summary>Новое состояние.</summary>
    [Required, StringLength(32)] public string Status { get; set; } = string.Empty;
    /// <summary>Явный новый срок.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Количество дней относительно текущего действующего срока.</summary>
    [Range(-3660, 3660)] public int ExtensionDays { get; set; }
    /// <summary>Причина для журнала аудита.</summary>
    [StringLength(500)] public string? Reason { get; set; }
}
