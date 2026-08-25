using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Статистика клиентов выдачи и административные правила блокировки.</summary>
[ApiController, Route("api/v1/admin/access"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminAccessController(ProxyHarborDbContext db, ProxyAccessMonitor monitor) : ControllerBase
{
    /// <summary>Возвращает агрегаты за 30 дней, сводку и действующие правила.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string? query = null, CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000); pageSize = Math.Clamp(pageSize, 10, 100);
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var buckets = db.ProxyAccessBuckets.AsNoTracking().Where(x => x.LastSeenAt >= since);
        if (!string.IsNullOrWhiteSpace(query)) buckets = buckets.Where(x => x.IpAddress.Contains(query.Trim()));
        var grouped = buckets.GroupBy(x => new { x.IpAddress, x.UserId })
            .Select(x => new { x.Key.IpAddress, x.Key.UserId, requests = x.Sum(y => y.Requests),
                blockedRequests = x.Sum(y => y.BlockedRequests), proxyItems = x.Sum(y => y.ProxyItems),
                bytesSent = x.Sum(y => y.BytesSent), lastSeenAt = x.Max(y => y.LastSeenAt) });
        var total = await grouped.CountAsync(token);
        var items = await grouped.OrderByDescending(x => x.requests).ThenBy(x => x.IpAddress)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var rules = await db.AccessBlockRules.AsNoTracking().OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.Kind, x.Value, x.UserId, x.Reason, x.Enabled, x.ExpiresAt, x.CreatedAt })
            .ToListAsync(token);
        var summary = new { requests = await buckets.SumAsync(x => (long?)x.Requests, token) ?? 0,
            proxyItems = await buckets.SumAsync(x => (long?)x.ProxyItems, token) ?? 0,
            uniqueIps = await buckets.Select(x => x.IpAddress).Distinct().CountAsync(token),
            activeRules = rules.Count(x => x.Enabled && (x.ExpiresAt is null || x.ExpiresAt > DateTimeOffset.UtcNow)) };
        return Ok(new { items, page, pageSize, total, rules, summary });
    }

    /// <summary>Создаёт блокировку точного IP, CIDR или пользователя.</summary>
    [HttpPost("rules")]
    public async Task<IActionResult> CreateRule([FromBody] AccessRuleRequest request, CancellationToken token)
    {
        if (!AccessBlockKinds.All.Contains(request.Kind, StringComparer.Ordinal)) return BadRequest();
        var value = request.Value.Trim(); Guid? userId = null;
        if (request.Kind == AccessBlockKinds.User)
        {
            if (!Guid.TryParse(value, out var parsed) || !await db.Users.AnyAsync(x => x.Id == parsed, token)) return BadRequest();
            userId = parsed; value = parsed.ToString();
        }
        else if (request.Kind == AccessBlockKinds.Ip)
        {
            if (!IPAddress.TryParse(value, out var ip)) return BadRequest(); value = ip.ToString();
        }
        else if (!IPNetwork.TryParse(value, out var network)) return BadRequest(); else value = network.ToString();
        var rule = new AccessBlockRule { Kind = request.Kind, Value = value, UserId = userId,
            Reason = request.Reason.Trim(), ExpiresAt = request.ExpiresAt,
            AdministratorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!) };
        db.AccessBlockRules.Add(rule); await db.SaveChangesAsync(token); await monitor.ReloadRulesAsync(token);
        return CreatedAtAction(nameof(List), new { }, new { rule.Id });
    }

    /// <summary>Включает, выключает либо изменяет срок правила.</summary>
    [HttpPut("rules/{id:guid}")]
    public async Task<IActionResult> ToggleRule(Guid id, [FromBody] ToggleAccessRuleRequest request, CancellationToken token)
    {
        var rule = await db.AccessBlockRules.SingleOrDefaultAsync(x => x.Id == id, token);
        if (rule is null) return NotFound();
        rule.Enabled = request.Enabled; rule.ExpiresAt = request.ExpiresAt; rule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token); await monitor.ReloadRulesAsync(token); return NoContent();
    }
}

/// <summary>Новое правило доступа с обязательным обоснованием.</summary>
public sealed class AccessRuleRequest
{
    /// <summary>Тип цели.</summary>
    [Required, StringLength(16)] public string Kind { get; set; } = string.Empty;
    /// <summary>IP, CIDR или UUID пользователя.</summary>
    [Required, StringLength(128)] public string Value { get; set; } = string.Empty;
    /// <summary>Причина блокировки.</summary>
    [Required, StringLength(500, MinimumLength = 3)] public string Reason { get; set; } = string.Empty;
    /// <summary>Необязательное автоматическое окончание.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
/// <summary>Изменяемое состояние существующего правила.</summary>
public sealed class ToggleAccessRuleRequest
{
    /// <summary>Должно ли правило применяться.</summary>
    public bool Enabled { get; set; }
    /// <summary>Новый срок либо бессрочно.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
