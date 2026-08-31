using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Неблокирующий приём метрик выдачи и посещений с периодическим агрегированным flush.</summary>
public sealed class ProxyAccessMonitor(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    ILogger<ProxyAccessMonitor> logger) : BackgroundService
{
    /// <summary>Префикс отделяет посещения сайта от запросов каталога и экспорта.</summary>
    public const string SitePagePrefix = "page:";
    private static readonly Action<ILogger, Exception?> FlushFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(1501, "ProxyAccessFlushFailed"),
        "Не удалось сохранить агрегат доступа к proxy API.");
    private static readonly Action<ILogger, Exception?> MaintenanceFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(1502, "ProxyAccessMaintenanceFailed"),
        "Не удалось выполнить обслуживание правил/retention proxy API; worker продолжит работу.");
    private readonly ConcurrentDictionary<AccessKey, AccessCounter> counters = new();
    private readonly ConcurrentQueue<SiteVisitLog> siteVisits = new();
    private volatile AccessRuleSnapshot rules = AccessRuleSnapshot.Empty;

    /// <summary>Добавляет запрос к текущему агрегату без обращения к БД.</summary>
    public void Record(HttpContext context, string endpoint, bool blocked)
    {
        var ip = NormalizeAddress(context.Connection.RemoteIpAddress);
        Guid? userId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : null;
        var bucket = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / TimeSpan.TicksPerMinute / 5 * TimeSpan.TicksPerMinute * 5, TimeSpan.Zero);
        var key = new AccessKey(bucket, ip, userId, endpoint);
        var increment = new AccessCounter
        {
            Requests = 1,
            BlockedRequests = blocked ? 1 : 0,
            BytesSent = context.Response.ContentLength is > 0 ? context.Response.ContentLength.Value : 0,
            ProxyItems = context.Items.TryGetValue("ProxyHarbor.ProxyItems", out var itemCount) && itemCount is int count ? count : 0
        };
        MergeCounter(key, increment);
    }

    /// <summary>Учитывает один просмотр нормализованной страницы без сохранения URL-параметров.</summary>
    public void RecordSiteVisit(HttpContext context, string normalizedPage)
    {
        Record(context, SitePagePrefix + normalizedPage, blocked: false);
        Guid? userId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : null;
        siteVisits.Enqueue(new SiteVisitLog
        {
            IpAddress = NormalizeAddress(context.Connection.RemoteIpAddress),
            UserId = userId,
            Page = normalizedPage,
            VisitedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>Сворачивает IPv4-mapped IPv6 к привычному каноническому виду.</summary>
    internal static string NormalizeAddress(IPAddress? address) => address is null
        ? "unknown"
        : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    /// <summary>Проверяет локальный неизменяемый снимок активных правил.</summary>
    public bool IsBlocked(IPAddress? address, Guid? userId)
    {
        var snapshot = rules;
        if (userId.HasValue && snapshot.Users.Contains(userId.Value)) return true;
        if (address is null) return false;
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return snapshot.Addresses.Contains(normalized) || snapshot.Networks.Any(x => x.Contains(normalized));
    }

    /// <summary>Атомарно перечитывает правила после административного изменения.</summary>
    public async Task ReloadRulesAsync(CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var now = DateTimeOffset.UtcNow;
        var rows = await db.AccessBlockRules.AsNoTracking()
            .Where(x => x.Enabled && (x.ExpiresAt == null || x.ExpiresAt > now)).ToListAsync(token);
        var addresses = new HashSet<IPAddress>();
        var networks = new List<IPNetwork>();
        var users = new HashSet<Guid>();
        foreach (var row in rows)
        {
            if (row.Kind == AccessBlockKinds.User && row.UserId.HasValue) users.Add(row.UserId.Value);
            else if (row.Kind == AccessBlockKinds.Ip && IPAddress.TryParse(row.Value, out var ip)) addresses.Add(ip);
            else if (row.Kind == AccessBlockKinds.Cidr && IPNetwork.TryParse(row.Value, out var network)) networks.Add(network);
        }
        rules = new AccessRuleSnapshot(addresses, networks, users);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await ReloadRulesAsync(stoppingToken); }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { MaintenanceFailed(logger, exception); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var ticks = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await FlushOnceAsync(stoppingToken); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { MaintenanceFailed(logger, exception); }
            ticks++;
            // Каждая реплика видит изменения правил максимум через минуту, даже если
            // правило было создано через другую реплику.
            if (ticks % 4 == 0)
                try { await ReloadRulesAsync(stoppingToken); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { MaintenanceFailed(logger, exception); }
            // Raw IP — чувствительные эксплуатационные данные. Bounded retention
            // предотвращает бесконечный рост таблицы и ограничивает окно хранения.
            if (ticks % 240 == 0)
                try { await PruneAsync(stoppingToken); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { MaintenanceFailed(logger, exception); }
        }
    }

    private async Task PruneAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
        await db.ProxyAccessBuckets.Where(x => x.LastSeenAt < cutoff)
            .ExecuteDeleteAsync(token);
        await db.SiteVisitLogs.Where(x => x.VisitedAt < cutoff)
            .ExecuteDeleteAsync(token);
        await db.UserApiTokenRequests.Where(x => x.RequestedAt < cutoff)
            .ExecuteDeleteAsync(token);
        await db.FreeProxyExportGrants.Where(x => x.NextAllowedAt < cutoff)
            .ExecuteDeleteAsync(token);
    }

    internal async Task FlushOnceAsync(CancellationToken token)
    {
        var snapshot = counters.ToArray();
        if (snapshot.Length == 0 && siteVisits.IsEmpty) return;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var drained = new List<(AccessKey Key, AccessCounter Value)>(snapshot.Length);
        foreach (var pair in snapshot)
        {
            lock (pair.Value)
            {
                if (!counters.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value) ||
                    !counters.TryRemove(pair.Key, out _)) continue;
                drained.Add((pair.Key, pair.Value.Copy()));
            }
        }
        if (drained.Count > 0)
            try { await BulkUpsertCountersAsync(db, drained, DateTimeOffset.UtcNow, token); }
            catch (Exception exception)
            {
                // Вся import-транзакция атомарна: при её откате возвращаем каждый
                // отсоединённый increment, включая запись, пришедшую во время flush.
                foreach (var item in drained) MergeCounter(item.Key, item.Value);
                FlushFailed(logger, exception);
            }
        var visits = new List<SiteVisitLog>(Math.Min(siteVisits.Count, 5_000));
        while (visits.Count < 5_000 && siteVisits.TryDequeue(out var visit)) visits.Add(visit);
        if (visits.Count == 0) return;
        try
        {
            db.SiteVisitLogs.AddRange(visits);
            await db.SaveChangesAsync(token);
        }
        catch
        {
            foreach (var visit in visits) siteVisits.Enqueue(visit);
            throw;
        }
    }

    /// <summary>
    /// Сохраняет произвольное число access-bucket'ов за три обращения к PostgreSQL
    /// (TEMP TABLE, binary COPY, UPSERT) вместо отдельного round-trip на каждый IP.
    /// ORDER BY задаёт одинаковый порядок conflict-lock между несколькими API-репликами.
    /// </summary>
    private static async Task BulkUpsertCountersAsync(
        ProxyHarborDbContext db,
        IReadOnlyCollection<(AccessKey Key, AccessCounter Value)> increments,
        DateTimeOffset flushedAt,
        CancellationToken token)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE access_counter_flush (
                bucket_started_at timestamptz NOT NULL,
                ip_address text NOT NULL,
                user_id uuid NULL,
                endpoint text NOT NULL,
                requests integer NOT NULL,
                blocked_requests integer NOT NULL,
                proxy_items bigint NOT NULL,
                bytes_sent bigint NOT NULL
            ) ON COMMIT DROP
            """, connection, transaction))
            await create.ExecuteNonQueryAsync(token);

        await using (var writer = await connection.BeginBinaryImportAsync("""
            COPY access_counter_flush
                (bucket_started_at, ip_address, user_id, endpoint, requests, blocked_requests, proxy_items, bytes_sent)
            FROM STDIN (FORMAT BINARY)
            """, token))
        {
            foreach (var increment in increments)
            {
                await writer.StartRowAsync(token);
                await writer.WriteAsync(increment.Key.Bucket, NpgsqlDbType.TimestampTz, token);
                await writer.WriteAsync(increment.Key.Ip, NpgsqlDbType.Text, token);
                if (increment.Key.UserId.HasValue)
                    await writer.WriteAsync(increment.Key.UserId.Value, NpgsqlDbType.Uuid, token);
                else
                    await writer.WriteNullAsync(token);
                await writer.WriteAsync(increment.Key.Endpoint, NpgsqlDbType.Text, token);
                await writer.WriteAsync(increment.Value.Requests, NpgsqlDbType.Integer, token);
                await writer.WriteAsync(increment.Value.BlockedRequests, NpgsqlDbType.Integer, token);
                await writer.WriteAsync(increment.Value.ProxyItems, NpgsqlDbType.Bigint, token);
                await writer.WriteAsync(increment.Value.BytesSent, NpgsqlDbType.Bigint, token);
            }
            await writer.CompleteAsync(token);
        }

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO "ProxyAccessBuckets"
                ("BucketStartedAt", "IpAddress", "UserId", "Endpoint", "Requests",
                 "BlockedRequests", "ProxyItems", "BytesSent", "LastSeenAt")
            SELECT bucket_started_at, ip_address, user_id, endpoint, requests,
                   blocked_requests, proxy_items, bytes_sent, @flushed_at
            FROM access_counter_flush
            ORDER BY bucket_started_at, ip_address, user_id NULLS FIRST, endpoint
            ON CONFLICT ("BucketStartedAt", "IpAddress", "UserId", "Endpoint") DO UPDATE SET
                "Requests" = "ProxyAccessBuckets"."Requests" + EXCLUDED."Requests",
                "BlockedRequests" = "ProxyAccessBuckets"."BlockedRequests" + EXCLUDED."BlockedRequests",
                "ProxyItems" = "ProxyAccessBuckets"."ProxyItems" + EXCLUDED."ProxyItems",
                "BytesSent" = "ProxyAccessBuckets"."BytesSent" + EXCLUDED."BytesSent",
                "LastSeenAt" = EXCLUDED."LastSeenAt"
            """, connection, transaction);
        upsert.Parameters.AddWithValue("flushed_at", NpgsqlDbType.TimestampTz, flushedAt);
        await upsert.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    /// <summary>
    /// Повторяет получение bucket, если flush успел удалить его до захвата lock.
    /// Так request никогда не инкрементирует уже отсоединённый объект счётчика.
    /// </summary>
    private void MergeCounter(AccessKey key, AccessCounter increment)
    {
        while (true)
        {
            var current = counters.GetOrAdd(key, _ => new AccessCounter());
            lock (current)
            {
                if (!counters.TryGetValue(key, out var attached) || !ReferenceEquals(attached, current)) continue;
                current.Add(increment);
                return;
            }
        }
    }

    private sealed record AccessKey(DateTimeOffset Bucket, string Ip, Guid? UserId, string Endpoint);
    private sealed class AccessCounter
    {
        public int Requests; public int BlockedRequests; public long ProxyItems; public long BytesSent;
        public void Add(AccessCounter other) { Requests += other.Requests; BlockedRequests += other.BlockedRequests; ProxyItems += other.ProxyItems; BytesSent += other.BytesSent; }
        public AccessCounter Copy() => new() { Requests = Requests, BlockedRequests = BlockedRequests, ProxyItems = ProxyItems, BytesSent = BytesSent };
    }
    private sealed record AccessRuleSnapshot(HashSet<IPAddress> Addresses, List<IPNetwork> Networks, HashSet<Guid> Users)
    {
        public static AccessRuleSnapshot Empty { get; } = new([], [], []);
    }
}

/// <summary>Применяет административные блокировки только к выдаче адресов.</summary>
public sealed class ProxyAccessMiddleware(RequestDelegate next)
{
    /// <summary>Проверяет блокировку, выполняет endpoint и учитывает результат.</summary>
    public async Task InvokeAsync(HttpContext context, ProxyAccessMonitor monitor)
    {
        if (!TryEndpoint(context.Request.Path, out var endpoint)) { await next(context); return; }
        Guid? userId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : null;
        if (monitor.IsBlocked(context.Connection.RemoteIpAddress, userId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Title = "Доступ к выдаче прокси заблокирован", Status = 403 });
            monitor.Record(context, endpoint, true);
            return;
        }
        await next(context);
        monitor.Record(context, endpoint, false);
    }

    private static bool TryEndpoint(PathString path, out string endpoint)
    {
        var value = path.Value ?? string.Empty;
        endpoint = value.StartsWith("/api/v1/export/", StringComparison.OrdinalIgnoreCase) ? "export" :
            value is "/api/v1/proxies" or "/api/v1/proxies/seek" ? "catalog" : string.Empty;
        return endpoint.Length > 0;
    }
}
