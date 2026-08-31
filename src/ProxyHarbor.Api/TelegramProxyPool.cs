using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    IOptions<CollectorOptions> collector,
    TelegramProxyCandidateCache cache) : ITelegramProxyCandidateProvider
{
    private const int MaximumCandidates = 8;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TelegramProxyOptions>> GetCandidatesAsync(CancellationToken token) =>
        await cache.GetOrLoadAsync(LoadCandidatesAsync, token);

    private async Task<TelegramProxyOptions[]> LoadCandidatesAsync(CancellationToken token)
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
/// Короткий общий снимок резервных SOCKS5-маршрутов. Один Telegram API-вызов не
/// должен порождать отдельный одинаковый SELECT к большому каталогу прокси.
/// </summary>
public sealed class TelegramProxyCandidateCache : IDisposable
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly TimeSpan lifetime;
    private readonly Func<DateTimeOffset> utcNow;
    private TelegramProxyOptions[]? snapshot;
    private long expiresAtUnixMilliseconds;

    /// <summary>Создаёт production-cache с коротким окном актуальности.</summary>
    public TelegramProxyCandidateCache() : this(DefaultLifetime, static () => DateTimeOffset.UtcNow) { }

    internal TelegramProxyCandidateCache(TimeSpan lifetime, Func<DateTimeOffset> utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        this.lifetime = lifetime;
        this.utcNow = utcNow;
    }

    internal async Task<IReadOnlyList<TelegramProxyOptions>> GetOrLoadAsync(
        Func<CancellationToken, Task<TelegramProxyOptions[]>> loader,
        CancellationToken token)
    {
        var now = utcNow();
        var current = Volatile.Read(ref snapshot);
        if (current is not null && now.ToUnixTimeMilliseconds() < Volatile.Read(ref expiresAtUnixMilliseconds))
            return current;

        await refreshGate.WaitAsync(token);
        try
        {
            // Несколько одновременно начавшихся API-вызовов объединяются в один
            // refresh; ожидавшие semaphore используют уже опубликованный снимок.
            now = utcNow();
            current = Volatile.Read(ref snapshot);
            if (current is not null && now.ToUnixTimeMilliseconds() < Volatile.Read(ref expiresAtUnixMilliseconds))
                return current;

            var loaded = await loader(token);
            Volatile.Write(ref snapshot, loaded);
            Volatile.Write(ref expiresAtUnixMilliseconds, now.Add(lifetime).ToUnixTimeMilliseconds());
            return loaded;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => refreshGate.Dispose();
}

/// <summary>
/// Ограниченный пул SOCKS5 HTTP-клиентов Telegram. Повторно использует TCP/TLS-
/// соединения, но удаляет неактивные и старые маршруты вместе с их credentials.
/// </summary>
public sealed class TelegramProxyHttpClientPool : IDisposable
{
    private const int MaximumClients = 32;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);
    private readonly Lock sync = new();
    private readonly Dictionary<ProxyClientKey, PoolEntry> entries = [];
    private bool disposed;

    internal ClientLease Acquire(TelegramProxyOptions proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        var key = ProxyClientKey.Create(proxy);
        var now = DateTimeOffset.UtcNow;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RemoveIdleEntries(now);
            if (entries.TryGetValue(key, out var existing))
            {
                existing.LastUsedAt = now;
                existing.ActiveLeases++;
                return new ClientLease(this, existing);
            }

            // Каталожные кандидаты меняются со временем. Жёсткая граница не даёт
            // пулу удерживать handler для каждого когда-либо встреченного адреса.
            if (entries.Count >= MaximumClients)
            {
                var oldest = entries.MinBy(static pair => pair.Value.LastUsedAt);
                entries.Remove(oldest.Key);
                Retire(oldest.Value);
            }

            var client = CreateClient(proxy);
            var created = new PoolEntry(client, now) { ActiveLeases = 1 };
            entries.Add(key, created);
            return new ClientLease(this, created);
        }
    }

    internal int Count
    {
        get { lock (sync) return entries.Count; }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in entries.Values) Retire(entry);
            entries.Clear();
        }
    }

    private void RemoveIdleEntries(DateTimeOffset now)
    {
        foreach (var idle in entries.Where(pair => now - pair.Value.LastUsedAt >= IdleLifetime).ToArray())
        {
            entries.Remove(idle.Key);
            Retire(idle.Value);
        }
    }

    private void Release(PoolEntry entry)
    {
        lock (sync)
        {
            entry.ActiveLeases--;
            if (entry.Retired && entry.ActiveLeases == 0) entry.Client.Dispose();
        }
    }

    private static void Retire(PoolEntry entry)
    {
        entry.Retired = true;
        // Eviction не обрывает уже начатый Bot API request. Последний lease
        // освободит handler сразу после получения полного Telegram response.
        if (entry.ActiveLeases == 0) entry.Client.Dispose();
    }

    private static HttpClient CreateClient(TelegramProxyOptions proxy)
    {
        // UriBuilder корректно заключает IPv6 host в скобки; строковая склейка
        // превращала валидный каталожный IPv6 SOCKS5 в неоднозначный URI.
        var webProxy = new WebProxy(new UriBuilder("socks5", proxy.Host, proxy.Port).Uri)
        {
            Credentials = new NetworkCredential(proxy.Username, proxy.Password)
        };
        var handler = new SocketsHttpHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8
        };
        // getUpdates использует long polling до 30 секунд. Клиентский timeout обязан
        // быть больше server-side timeout, иначе спокойный чат выглядит как авария сети.
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(45) };
    }

    internal sealed class PoolEntry(HttpClient client, DateTimeOffset lastUsedAt)
    {
        internal HttpClient Client { get; } = client;
        internal DateTimeOffset LastUsedAt { get; set; } = lastUsedAt;
        internal int ActiveLeases { get; set; }
        internal bool Retired { get; set; }
    }

    internal sealed class ClientLease(TelegramProxyHttpClientPool owner, PoolEntry entry) : IDisposable
    {
        private TelegramProxyHttpClientPool? currentOwner = owner;
        internal HttpClient Client => entry.Client;

        public void Dispose() => Interlocked.Exchange(ref currentOwner, null)?.Release(entry);
    }

    private readonly record struct ProxyClientKey(
        string Host, int Port, string Username, string PasswordFingerprint)
    {
        internal static ProxyClientKey Create(TelegramProxyOptions proxy)
        {
            var passwordBytes = Encoding.UTF8.GetBytes(proxy.Password);
            try
            {
                return new ProxyClientKey(
                    proxy.Host.ToLowerInvariant(), proxy.Port, proxy.Username,
                    Convert.ToHexString(SHA256.HashData(passwordBytes)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
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
