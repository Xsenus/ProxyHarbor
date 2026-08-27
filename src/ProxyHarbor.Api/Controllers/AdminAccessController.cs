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
        [FromQuery] string? query = null, [FromQuery] string sort = "requests",
        [FromQuery] string order = "desc", CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000); pageSize = Math.Clamp(pageSize, 10, 100);
        if (!new[] { "ip", "requests", "proxyItems", "bytesSent", "lastSeen" }.Contains(sort)) return BadRequest();
        if (!TryDescending(order, out var descending)) return BadRequest();
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var buckets = db.ProxyAccessBuckets.AsNoTracking().Where(x =>
            x.LastSeenAt >= since && !x.Endpoint.StartsWith(ProxyAccessMonitor.SitePagePrefix));
        if (!string.IsNullOrWhiteSpace(query)) buckets = buckets.Where(x => x.IpAddress.Contains(query.Trim()));
        var grouped = buckets.GroupBy(x => x.IpAddress)
            .Select(x => new
            {
                IpAddress = x.Key,
                requests = x.Sum(y => y.Requests),
                blockedRequests = x.Sum(y => y.BlockedRequests),
                proxyItems = x.Sum(y => y.ProxyItems),
                bytesSent = x.Sum(y => y.BytesSent),
                lastSeenAt = x.Max(y => y.LastSeenAt)
            });
        var total = await grouped.CountAsync(token);
        var ordered = sort switch
        {
            "ip" => descending ? grouped.OrderByDescending(x => x.IpAddress) : grouped.OrderBy(x => x.IpAddress),
            "proxyItems" => descending ? grouped.OrderByDescending(x => x.proxyItems) : grouped.OrderBy(x => x.proxyItems),
            "bytesSent" => descending ? grouped.OrderByDescending(x => x.bytesSent) : grouped.OrderBy(x => x.bytesSent),
            "lastSeen" => descending ? grouped.OrderByDescending(x => x.lastSeenAt) : grouped.OrderBy(x => x.lastSeenAt),
            _ => descending ? grouped.OrderByDescending(x => x.requests) : grouped.OrderBy(x => x.requests)
        };
        var rows = await ordered.ThenBy(x => x.IpAddress)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var ips = rows.Select(x => x.IpAddress).ToArray();
        var accountLinks = await buckets.Where(x => ips.Contains(x.IpAddress) && x.UserId != null)
            .OrderByDescending(x => x.LastSeenAt)
            .Select(x => new { x.IpAddress, x.UserId, x.LastSeenAt }).ToListAsync(token);
        var accountIds = accountLinks.Select(x => x.UserId!.Value).Distinct().ToArray();
        var accounts = await db.Users.AsNoTracking().Where(x => accountIds.Contains(x.Id))
            .Select(x => new { x.Id, x.UserName, x.Email, x.DisplayName }).ToDictionaryAsync(x => x.Id, token);
        var activeIpRules = await ActiveIpRules(token);
        var items = rows.Select(row =>
        {
            var link = accountLinks.FirstOrDefault(x => x.IpAddress == row.IpAddress);
            var account = link?.UserId is Guid id && accounts.TryGetValue(id, out var found) ? found : null;
            return new
            {
                row.IpAddress,
                userId = link?.UserId,
                userName = account?.UserName,
                email = account?.Email,
                displayName = account?.DisplayName,
                row.requests,
                row.blockedRequests,
                row.proxyItems,
                row.bytesSent,
                row.lastSeenAt,
                isBlocked = activeIpRules.Contains(row.IpAddress)
            };
        }).ToArray();
        var summary = new
        {
            requests = await buckets.SumAsync(x => (long?)x.Requests, token) ?? 0,
            proxyItems = await buckets.SumAsync(x => (long?)x.ProxyItems, token) ?? 0,
            uniqueIps = await buckets.Select(x => x.IpAddress).Distinct().CountAsync(token),
            activeRules = await db.AccessBlockRules.CountAsync(x => x.Enabled && (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), token)
        };
        return Ok(new { items, page, pageSize, total, sort, order, summary });
    }

    /// <summary>Возвращает посетителей React-сайта и отдельную сводку за 30 дней.</summary>
    [HttpGet("visitors")]
    public async Task<IActionResult> Visitors([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string? query = null, [FromQuery] string sort = "lastSeen",
        [FromQuery] string order = "desc", CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000); pageSize = Math.Clamp(pageSize, 10, 100);
        if (!new[] { "ip", "pageViews", "pages", "firstSeen", "lastSeen" }.Contains(sort)) return BadRequest();
        if (!TryDescending(order, out var descending)) return BadRequest();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-30);
        var buckets = db.ProxyAccessBuckets.AsNoTracking().Where(x =>
            x.LastSeenAt >= since && x.Endpoint.StartsWith(ProxyAccessMonitor.SitePagePrefix));
        if (!string.IsNullOrWhiteSpace(query)) buckets = buckets.Where(x => x.IpAddress.Contains(query.Trim()));

        var grouped = buckets.GroupBy(x => x.IpAddress)
            .Select(x => new
            {
                IpAddress = x.Key,
                PageViews = x.Sum(y => y.Requests),
                Pages = x.Select(y => y.Endpoint).Distinct().Count(),
                FirstSeenAt = x.Min(y => y.BucketStartedAt),
                LastSeenAt = x.Max(y => y.LastSeenAt)
            });
        var total = await grouped.CountAsync(token);
        var ordered = sort switch
        {
            "ip" => descending ? grouped.OrderByDescending(x => x.IpAddress) : grouped.OrderBy(x => x.IpAddress),
            "pageViews" => descending ? grouped.OrderByDescending(x => x.PageViews) : grouped.OrderBy(x => x.PageViews),
            "pages" => descending ? grouped.OrderByDescending(x => x.Pages) : grouped.OrderBy(x => x.Pages),
            "firstSeen" => descending ? grouped.OrderByDescending(x => x.FirstSeenAt) : grouped.OrderBy(x => x.FirstSeenAt),
            _ => descending ? grouped.OrderByDescending(x => x.LastSeenAt) : grouped.OrderBy(x => x.LastSeenAt)
        };
        var rows = await ordered.ThenBy(x => x.IpAddress)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var ips = rows.Select(x => x.IpAddress).ToArray();
        var links = await buckets.Where(x => ips.Contains(x.IpAddress) && x.UserId != null)
            .OrderByDescending(x => x.LastSeenAt).Select(x => new { x.IpAddress, x.UserId }).ToListAsync(token);
        var userIds = links.Select(x => x.UserId!.Value).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id))
            .Select(x => new { x.Id, x.UserName, x.Email, x.DisplayName }).ToDictionaryAsync(x => x.Id, token);
        var items = rows.Select(x =>
        {
            var link = links.FirstOrDefault(y => y.IpAddress == x.IpAddress);
            var account = link?.UserId is Guid id && users.TryGetValue(id, out var found) ? found : null;
            return new
            {
                x.IpAddress,
                userId = link?.UserId,
                userName = account?.UserName,
                email = account?.Email,
                displayName = account?.DisplayName,
                x.PageViews,
                x.Pages,
                x.FirstSeenAt,
                x.LastSeenAt
            };
        }).ToArray();
        var summary = new
        {
            pageViews = await buckets.SumAsync(x => (long?)x.Requests, token) ?? 0,
            uniqueVisitors = await buckets.Select(x => x.IpAddress).Distinct().CountAsync(token),
            authenticatedVisitors = await buckets.Where(x => x.UserId != null)
                .Select(x => x.UserId).Distinct().CountAsync(token),
            active24Hours = await buckets.Where(x => x.LastSeenAt >= now.AddHours(-24))
                .Select(x => x.IpAddress).Distinct().CountAsync(token)
        };
        var blockedIps = await ActiveIpRules(token);
        var enriched = items.Select(x => new
        {
            x.IpAddress,
            x.userId,
            x.userName,
            x.email,
            x.displayName,
            x.PageViews,
            x.Pages,
            x.FirstSeenAt,
            x.LastSeenAt,
            isBlocked = blockedIps.Contains(x.IpAddress)
        }).ToArray();
        return Ok(new { items = enriched, page, pageSize, total, sort, order, summary, retentionDays = 90 });
    }

    /// <summary>Возвращает отдельные переходы по страницам с серверной пагинацией.</summary>
    [HttpGet("visitors/history")]
    public async Task<IActionResult> VisitHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string? query = null, [FromQuery] string sort = "visitedAt",
        [FromQuery] string order = "desc", CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000); pageSize = Math.Clamp(pageSize, 10, 100);
        if (!new[] { "ip", "page", "visitedAt" }.Contains(sort)) return BadRequest();
        if (!TryDescending(order, out var descending)) return BadRequest();
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var source = db.SiteVisitLogs.AsNoTracking().Where(x => x.VisitedAt >= since);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var value = query.Trim();
            source = source.Where(x => x.IpAddress.Contains(value) || x.Page.Contains(value));
        }
        var total = await source.CountAsync(token);
        var ordered = sort switch
        {
            "ip" => descending ? source.OrderByDescending(x => x.IpAddress) : source.OrderBy(x => x.IpAddress),
            "page" => descending ? source.OrderByDescending(x => x.Page) : source.OrderBy(x => x.Page),
            _ => descending ? source.OrderByDescending(x => x.VisitedAt) : source.OrderBy(x => x.VisitedAt)
        };
        var items = await ordered.ThenByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.IpAddress,
                x.UserId,
                x.Page,
                x.VisitedAt,
                userName = x.User != null ? x.User.UserName : null,
                email = x.User != null ? x.User.Email : null,
                displayName = x.User != null ? x.User.DisplayName : null
            })
            .ToListAsync(token);
        return Ok(new { items, page, pageSize, total, sort, order, retentionDays = 90 });
    }

    /// <summary>Возвращает правила отдельным постраничным реестром.</summary>
    [HttpGet("rules")]
    public async Task<IActionResult> Rules([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string? query = null, [FromQuery] string sort = "createdAt",
        [FromQuery] string order = "desc", CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000); pageSize = Math.Clamp(pageSize, 10, 100);
        if (!new[] { "target", "createdAt", "expiresAt", "status" }.Contains(sort)) return BadRequest();
        if (!TryDescending(order, out var descending)) return BadRequest();
        var source = db.AccessBlockRules.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var value = query.Trim();
            source = source.Where(x => x.Value.Contains(value) || x.Reason.Contains(value));
        }
        var total = await source.CountAsync(token);
        var ordered = sort switch
        {
            "target" => descending ? source.OrderByDescending(x => x.Value) : source.OrderBy(x => x.Value),
            "expiresAt" => descending ? source.OrderByDescending(x => x.ExpiresAt) : source.OrderBy(x => x.ExpiresAt),
            "status" => descending ? source.OrderByDescending(x => x.Enabled) : source.OrderBy(x => x.Enabled),
            _ => descending ? source.OrderByDescending(x => x.CreatedAt) : source.OrderBy(x => x.CreatedAt)
        };
        var items = await ordered.ThenByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.Kind,
                x.Value,
                x.UserId,
                x.Reason,
                x.Enabled,
                x.ExpiresAt,
                x.CreatedAt,
                x.UpdatedAt
            }).ToListAsync(token);
        return Ok(new { items, page, pageSize, total, sort, order });
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
            if (!IPAddress.TryParse(value, out var ip)) return BadRequest(); value = ProxyAccessMonitor.NormalizeAddress(ip);
        }
        else if (!IPNetwork.TryParse(value, out var network)) return BadRequest(); else value = network.ToString();
        var rule = new AccessBlockRule
        {
            Kind = request.Kind,
            Value = value,
            UserId = userId,
            Reason = request.Reason.Trim(),
            ExpiresAt = request.ExpiresAt,
            AdministratorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!)
        };
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

    private async Task<HashSet<string>> ActiveIpRules(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return (await db.AccessBlockRules.AsNoTracking().Where(x => x.Kind == AccessBlockKinds.Ip &&
                x.Enabled && (x.ExpiresAt == null || x.ExpiresAt > now)).Select(x => x.Value).ToListAsync(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool TryDescending(string order, out bool descending)
    {
        descending = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);
        return descending || string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
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
