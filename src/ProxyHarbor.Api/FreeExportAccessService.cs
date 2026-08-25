using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Результат серверной проверки права на потоковую выгрузку прокси.</summary>
public sealed record FreeExportAccess(
    bool Allowed,
    bool IsPaid,
    int Limit,
    DateTimeOffset? NextAllowedAt,
    string Tier);

/// <summary>Проверяет подписку и атомарно резервирует одно бесплатное окно выгрузки.</summary>
public interface IFreeExportAccessService
{
    /// <summary>Возвращает платное право либо атомарно занимает очередное бесплатное окно.</summary>
    Task<FreeExportAccess> AcquireAsync(
        ClaimsPrincipal principal,
        string? remoteIp,
        CancellationToken cancellationToken);
}

/// <summary>
/// PostgreSQL остаётся источником истины для cooldown. Conditional UPDATE и уникальный
/// ключ не позволяют двум параллельным запросам получить бесплатную выдачу одновременно.
/// </summary>
public sealed class FreeExportAccessService(IDbContextFactory<ProxyHarborDbContext> dbFactory)
    : IFreeExportAccessService
{
    /// <summary>Максимум адресов в одной бесплатной выгрузке.</summary>
    public const int FreeLimit = 10;
    /// <summary>Минимальный интервал между бесплатными выгрузками.</summary>
    public const int CooldownSeconds = 600;
    /// <summary>Единый текст ограничения и перехода к подписке.</summary>
    public const string UpgradeMessage =
        "Бесплатный доступ: 10 прокси среднего качества раз в 10 минут. Для неограниченного доступа купите подписку.";
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(CooldownSeconds);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _inMemoryLocks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<FreeExportAccess> AcquireAsync(
        ClaimsPrincipal principal,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        var userId = TryGetUserId(principal);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await HasPaidAccessAsync(db, principal, userId, cancellationToken))
            return new(true, true, int.MaxValue, null, "paid");

        var clientKey = userId.HasValue
            ? $"user:{userId.Value:D}"
            : $"ip:{NormalizeRemoteIp(remoteIp)}";
        var now = DateTimeOffset.UtcNow;
        var nextAllowedAt = now.Add(Cooldown);

        if (!db.Database.IsRelational())
            return await AcquireInMemoryProviderAsync(db, clientKey, now, nextAllowedAt, cancellationToken);

        var updated = await db.FreeProxyExportGrants
            .Where(x => x.ClientKey == clientKey && x.NextAllowedAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastGrantedAt, now)
                .SetProperty(x => x.NextAllowedAt, nextAllowedAt), cancellationToken);
        if (updated == 1) return AllowedFree(nextAllowedAt);

        var existing = await db.FreeProxyExportGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ClientKey == clientKey, cancellationToken);
        if (existing is not null) return DeniedFree(existing.NextAllowedAt);

        db.FreeProxyExportGrants.Add(new FreeProxyExportGrant
        {
            ClientKey = clientKey,
            LastGrantedAt = now,
            NextAllowedAt = nextAllowedAt
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return AllowedFree(nextAllowedAt);
        }
        catch (DbUpdateException)
        {
            // Другой replica успел вставить тот же ключ между SELECT и INSERT.
            db.ChangeTracker.Clear();
            var winner = await db.FreeProxyExportGrants.AsNoTracking()
                .SingleAsync(x => x.ClientKey == clientKey, cancellationToken);
            return DeniedFree(winner.NextAllowedAt);
        }
    }

    private async Task<FreeExportAccess> AcquireInMemoryProviderAsync(
        ProxyHarborDbContext db,
        string clientKey,
        DateTimeOffset now,
        DateTimeOffset nextAllowedAt,
        CancellationToken token)
    {
        var gate = _inMemoryLocks.GetOrAdd(clientKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var grant = await db.FreeProxyExportGrants.SingleOrDefaultAsync(x => x.ClientKey == clientKey, token);
            if (grant is not null && grant.NextAllowedAt > now) return DeniedFree(grant.NextAllowedAt);
            if (grant is null)
                db.FreeProxyExportGrants.Add(new FreeProxyExportGrant { ClientKey = clientKey });
            else
                db.Entry(grant).State = EntityState.Modified;
            grant ??= db.FreeProxyExportGrants.Local.Single(x => x.ClientKey == clientKey);
            grant.LastGrantedAt = now;
            grant.NextAllowedAt = nextAllowedAt;
            await db.SaveChangesAsync(token);
            return AllowedFree(nextAllowedAt);
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> HasPaidAccessAsync(
        ProxyHarborDbContext db,
        ClaimsPrincipal principal,
        Guid? userId,
        CancellationToken token)
    {
        if (principal.IsInRole(UserRoles.Administrator)) return true;
        if (!userId.HasValue) return false;
        var now = DateTimeOffset.UtcNow;
        return await db.Subscriptions.AsNoTracking().AnyAsync(x =>
            x.UserId == userId.Value &&
            (x.Plan == SubscriptionPlans.Pro || x.Plan == SubscriptionPlans.Unlimited) &&
            (x.Status == SubscriptionStatuses.Active || x.Status == SubscriptionStatuses.Trialing) &&
            (x.ExpiresAt == null || x.ExpiresAt > now), token);
    }

    private static Guid? TryGetUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static string NormalizeRemoteIp(string? remoteIp) =>
        string.IsNullOrWhiteSpace(remoteIp) ? "unknown" : remoteIp.Trim().ToLower(CultureInfo.InvariantCulture);

    private static FreeExportAccess AllowedFree(DateTimeOffset nextAllowedAt) =>
        new(true, false, FreeLimit, nextAllowedAt, "free");

    private static FreeExportAccess DeniedFree(DateTimeOffset nextAllowedAt) =>
        new(false, false, FreeLimit, nextAllowedAt, "free");
}
