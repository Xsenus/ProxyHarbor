using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Возвращает свежие SOCKS5-маршруты из уже проверенного каталога.</summary>
public interface ITelegramProxyCandidateProvider
{
    /// <summary>Получает небольшой упорядоченный набор резервных маршрутов.</summary>
    Task<IReadOnlyList<TelegramProxyOptions>> GetCandidatesAsync(CancellationToken token);
}

/// <summary>
/// Использует только недавно проверенные SOCKS5 endpoint. TLS до Telegram остаётся
/// сквозным, поэтому промежуточный маршрут не получает bot token или содержимое запроса.
/// </summary>
public sealed class TelegramProxyCandidateProvider(
    ProxyHarborDbContext db,
    IOptions<CollectorOptions> collector) : ITelegramProxyCandidateProvider
{
    private const int MaximumCandidates = 8;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TelegramProxyOptions>> GetCandidatesAsync(CancellationToken token)
    {
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collector.Value.PublicFreshnessMinutes);
        return await db.Proxies.AsNoTracking()
            .Where(proxy => proxy.Protocol == ProxyProtocol.Socks5 &&
                proxy.Status == ProxyStatus.Alive && proxy.LastCheckedAt >= freshAfter)
            .OrderBy(proxy => proxy.LatencyMs)
            .ThenByDescending(proxy => proxy.SuccessfulChecks)
            .ThenBy(proxy => proxy.Id)
            .Take(MaximumCandidates)
            .Select(proxy => new TelegramProxyOptions
            {
                Id = proxy.Id,
                Host = proxy.Host,
                Port = proxy.Port
            })
            .ToArrayAsync(token);
    }
}

/// <summary>
/// Общий circuit breaker транспортов Telegram. Неисправный маршрут временно
/// исключается для всех worker scope, а после cooldown автоматически пробуется снова.
/// </summary>
public sealed class TelegramTransportHealth
{
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<string, DateTimeOffset> unavailableUntil = new(StringComparer.Ordinal);

    internal bool IsAvailable(TelegramProxyOptions? proxy, DateTimeOffset now) =>
        !unavailableUntil.TryGetValue(Key(proxy), out var until) || until <= now;

    internal void MarkFailed(TelegramProxyOptions? proxy, DateTimeOffset now) =>
        unavailableUntil[Key(proxy)] = now.Add(FailureCooldown);

    internal void MarkSucceeded(TelegramProxyOptions? proxy) => unavailableUntil.TryRemove(Key(proxy), out _);

    private static string Key(TelegramProxyOptions? proxy) => proxy is null
        ? "direct"
        : $"{proxy.Host.ToLowerInvariant()}:{proxy.Port}";
}
