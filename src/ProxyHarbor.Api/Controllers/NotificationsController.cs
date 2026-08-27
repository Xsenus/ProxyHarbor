using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Надёжная доставка персональных уведомлений в веб-кабинет.</summary>
[ApiController, Route("api/v1/account/notifications"), Authorize, EnableRateLimiting("account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationsController(UserManager<ApplicationUser> users, ProxyHarborDbContext db) : ControllerBase
{
    /// <summary>Возвращает ещё не показанные уведомления текущего пользователя.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        var items = await db.UserNotifications.AsNoTracking()
            .Where(x => x.UserId == user.Id && x.DeliveredAt == null)
            .OrderBy(x => x.CreatedAt).Take(20)
            .Select(x => new { x.Id, x.Kind, x.Message, x.ActionUrl, x.CreatedAt })
            .ToArrayAsync(token);
        return Ok(items);
    }

    /// <summary>Подтверждает показ уведомления и исключает повторный toast.</summary>
    [HttpPost("{id:guid}/delivered")]
    public async Task<IActionResult> Delivered(Guid id, CancellationToken token)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        var notification = await db.UserNotifications.SingleOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, token);
        if (notification is null) return NotFound();
        notification.DeliveredAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        return NoContent();
    }
}
