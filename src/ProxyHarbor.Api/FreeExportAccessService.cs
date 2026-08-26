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

    /// <summary>Проверяет платный доступ без расходования бесплатного cooldown.</summary>
    Task<bool> HasPaidAccessAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        Task.FromResult(false);
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
    /// <summary>Число готовых VPN-конфигураций в бесплатном каталоге.</summary>
    public const int FreeVpnLimit = 10;
    /// <summary>Минимальный интервал между бесплатными выгрузками.</summary>
    public const int CooldownSeconds = 600;
    /// <summary>Единый текст ограничения и перехода к подписке.</summary>
    public const string UpgradeMessage =
        "Бесплатный доступ: 10 прокси среднего качества из разных стран раз в 10 минут. Для неограниченного доступа купите подписку.";
    /// <summary>Возвращает ограничение бесплатного тарифа на языке текущего клиента.</summary>
    public static string GetUpgradeMessage(string? language) => SupportedLanguages.Normalize(language) switch
    {
        "en" => "Free access: 10 medium-quality proxies from different countries once every 10 minutes. Buy a subscription for unlimited access.",
        "de" => "Kostenloser Zugang: 10 Proxys mittlerer Qualität aus verschiedenen Ländern alle 10 Minuten. Für unbegrenzten Zugriff ist ein Abonnement erforderlich.",
        "fr" => "Accès gratuit : 10 proxys de qualité moyenne de pays différents toutes les 10 minutes. Achetez un abonnement pour un accès illimité.",
        "zh" => "免费访问：每 10 分钟可获取来自不同国家/地区的 10 个中等质量代理。购买订阅即可无限制访问。",
        _ => UpgradeMessage
    };
    /// <summary>Локализованное объяснение бесплатного VPN-каталога.</summary>
    public static string GetVpnUpgradeMessage(string? language, int total) => SupportedLanguages.Normalize(language) switch
    {
        "en" => $"Free access includes 10 medium-quality VPN configurations. All {total:N0} available configurations require a subscription.",
        "de" => $"Der kostenlose Zugang umfasst 10 VPN-Konfigurationen mittlerer Qualität. Alle {total:N0} verfügbaren Konfigurationen erfordern ein Abonnement.",
        "fr" => $"L’accès gratuit comprend 10 configurations VPN de qualité moyenne. Les {total:N0} configurations disponibles nécessitent un abonnement.",
        "zh" => $"免费版可使用 10 个中等质量 VPN 配置。订阅后可访问全部 {total:N0} 个可用配置。",
        _ => $"В бесплатной версии доступны 10 VPN среднего качества. Все доступные конфигурации ({total:N0}) открываются по подписке."
    };
    /// <summary>Локализованное объяснение ограниченного публичного proxy-каталога.</summary>
    public static string GetProxyCatalogUpgradeMessage(string? language, int total) => SupportedLanguages.Normalize(language) switch
    {
        "en" => $"Free access shows 10 medium-quality proxies from different countries. A subscription unlocks all {total:N0} available proxies.",
        "de" => $"Der kostenlose Zugang zeigt 10 Proxys mittlerer Qualität aus verschiedenen Ländern. Ein Abonnement schaltet alle {total:N0} verfügbaren Proxys frei.",
        "fr" => $"L’accès gratuit affiche 10 proxys de qualité moyenne de pays différents. Un abonnement débloque les {total:N0} proxys disponibles.",
        "zh" => $"免费版显示来自不同国家/地区的 10 个中等质量代理。订阅后可访问全部 {total:N0} 个可用代理。",
        _ => $"В бесплатной версии показаны 10 прокси среднего качества из разных стран. По подписке доступны все рабочие прокси: {total:N0}."
    };
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

    /// <inheritdoc />
    public async Task<bool> HasPaidAccessAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await HasPaidAccessAsync(db, principal, TryGetUserId(principal), cancellationToken);
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
