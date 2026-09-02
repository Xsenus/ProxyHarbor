using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Административный аудит реферальных связей и начислений.</summary>
[ApiController, Route("api/v1/admin/referrals"), Authorize(Roles = UserRoles.Administrator)]
[EnableRateLimiting("admin"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminReferralsController(ProxyHarborDbContext db) : ControllerBase
{
    /// <summary>Возвращает постраничный журнал реферальных регистраций и начислений.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery, Range(1, 100_000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 10,
        CancellationToken token = default)
    {
        return await BufferedReadSnapshot.ExecuteAsync<IActionResult>(db,
            async (CancellationToken snapshotToken) =>
        {
            var query = db.ReferralRelationships.AsNoTracking();
            var total = await query.CountAsync(snapshotToken);
            // Один проход по растущему reward-журналу вместо двух полных агрегатов.
            var rewards = await db.ReferralRewards.AsNoTracking()
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    RewardDays = group.Sum(x => x.DaysGranted),
                    PurchaseRewards = group.Count(x => x.Kind == ReferralRewardKinds.Purchase)
                })
                .SingleOrDefaultAsync(snapshotToken);
            var items = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new
                {
                    x.Id,
                    x.CreatedAt,
                    referrer = new { x.ReferrerUserId, x.ReferrerUser.UserName, x.ReferrerUser.Email, x.ReferrerUser.DisplayName },
                    referred = new { x.ReferredUserId, x.ReferredUser.UserName, x.ReferredUser.Email, x.ReferredUser.DisplayName },
                    rewardDays = x.Rewards.Sum(r => r.DaysGranted),
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
            return Ok(new
            {
                items,
                page,
                pageSize,
                total,
                summary = new
                {
                    referrals = total,
                    rewardDays = rewards?.RewardDays ?? 0,
                    purchaseRewards = rewards?.PurchaseRewards ?? 0
                }
            });
        }, token);
    }
}
