using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Общий bounded claim очереди прокси. Свободные строки и просроченные lease
/// выбираются раздельно: горячий путь использует частичный индекс без активно
/// занятых строк, а failover — компактный индекс только непустых сроков аренды.
/// Каждый диапазон статуса и NULL/non-NULL NextCheckAt остаётся отдельным seek.
/// Выборка и назначение lease выполняются одним UPDATE ... RETURNING; вызывающий
/// код обязан удерживать транзакцию до фиксации сопутствующего аудита.
/// </summary>
internal static class ValidationQueueClaim
{
    private const string ClaimSqlPrefix = """
        WITH candidate AS MATERIALIZED (
            SELECT proxy."Id", proxy."CheckLeaseId", proxy."NextCheckAt", proxy."LastCheckedAt"
            FROM "Proxies" AS proxy
        """;

    private const string ClaimSqlSuffix = """
            LIMIT @limit
            FOR UPDATE OF proxy SKIP LOCKED
        ), claimed AS (
            UPDATE "Proxies" AS proxy
            SET "CheckLeaseUntil" = @lease_until,
                "CheckLeaseId" = @lease_id
            FROM candidate
            WHERE proxy."Id" = candidate."Id"
            RETURNING proxy."Id", proxy."Host", proxy."Port", proxy."Protocol",
                      proxy."ConsecutiveFailedChecks",
                      candidate."CheckLeaseId" AS "PreviousLeaseId",
                      candidate."NextCheckAt" AS "QueueNextCheckAt",
                      candidate."LastCheckedAt" AS "QueueLastCheckedAt"
        )
        SELECT "Id", "Host", "Port", "Protocol", "ConsecutiveFailedChecks", "PreviousLeaseId"
        FROM claimed
        ORDER BY "QueueNextCheckAt" NULLS FIRST, "QueueLastCheckedAt" NULLS FIRST
        """;

    private const string ExpiredClaimSql = ClaimSqlPrefix + """
            WHERE CASE proxy."Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END = @priority
              AND (proxy."NextCheckAt" IS NULL OR proxy."NextCheckAt" <= @now)
              AND proxy."CheckLeaseUntil" < @now
            ORDER BY proxy."NextCheckAt" NULLS FIRST, proxy."LastCheckedAt" NULLS FIRST
        """ + ClaimSqlSuffix;

    private const string NeverCheckedClaimSql = ClaimSqlPrefix + """
            WHERE CASE proxy."Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END = @priority
              AND proxy."NextCheckAt" IS NULL
              AND proxy."CheckLeaseUntil" IS NULL
            ORDER BY proxy."LastCheckedAt" NULLS FIRST
        """ + ClaimSqlSuffix;

    private const string DueClaimSql = ClaimSqlPrefix + """
            WHERE CASE proxy."Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END = @priority
              AND proxy."NextCheckAt" <= @now
              AND proxy."CheckLeaseUntil" IS NULL
            ORDER BY proxy."NextCheckAt", proxy."LastCheckedAt" NULLS FIRST
        """ + ClaimSqlSuffix;

    internal static async Task<List<ValidationClaimCandidate>> ClaimAndLeaseAsync(
        ProxyHarborDbContext db,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        Guid leaseId,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        if (leaseUntil <= now)
            throw new ArgumentOutOfRangeException(nameof(leaseUntil), "Lease должен завершаться после момента claim.");
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Validation claim требует явной транзакции.");

        var claimed = new List<ValidationClaimCandidate>(batchSize);
        var hasExpiredLeases = await db.Proxies.AsNoTracking().AnyAsync(
            proxy => proxy.CheckLeaseUntil < now &&
                     (proxy.NextCheckAt == null || proxy.NextCheckAt <= now), token);
        for (var priority = 0; priority <= 2 && claimed.Count < batchSize; priority++)
        {
            if (hasExpiredLeases)
            {
                var remainingExpired = batchSize - claimed.Count;
                claimed.AddRange(await ClaimRangeAsync(
                    db, ExpiredClaimSql, priority, remainingExpired, now, leaseUntil, leaseId, token));
            }

            if (claimed.Count >= batchSize) continue;
            var remaining = batchSize - claimed.Count;
            claimed.AddRange(await ClaimRangeAsync(
                db, NeverCheckedClaimSql, priority, remaining, now, leaseUntil, leaseId, token));

            if (claimed.Count >= batchSize) continue;
            remaining = batchSize - claimed.Count;
            claimed.AddRange(await ClaimRangeAsync(
                db, DueClaimSql, priority, remaining, now, leaseUntil, leaseId, token));
        }

        return claimed;
    }

    private static Task<List<ValidationClaimCandidate>> ClaimRangeAsync(
        ProxyHarborDbContext db,
        string sql,
        int priority,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        Guid leaseId,
        CancellationToken token) =>
        db.Database.SqlQueryRaw<ValidationClaimCandidate>(
            sql,
            new NpgsqlParameter<int>("priority", priority),
            new NpgsqlParameter<int>("limit", limit),
            new NpgsqlParameter<DateTimeOffset>("now", now),
            new NpgsqlParameter<DateTimeOffset>("lease_until", leaseUntil),
            new NpgsqlParameter<Guid>("lease_id", leaseId))
            .ToListAsync(token);
}

/// <summary>Узкая detached-проекция арендованного proxy endpoint.</summary>
internal sealed class ValidationClaimCandidate
{
    public Guid Id { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public ProxyProtocol Protocol { get; set; }
    public int ConsecutiveFailedChecks { get; set; }
    public Guid? PreviousLeaseId { get; set; }

    public string Key =>
        $"{Protocol.ToString().ToLowerInvariant()}://{(Host.Contains(':') ? $"[{Host}]" : Host).ToLowerInvariant()}:{Port}";
}

/// <summary>
/// Bounded dequeue VPN-каталога. Глобальная advisory-lock сериализует validator,
/// поэтому здесь не нужны row leases. Половина партии резервируется для ещё ни разу
/// не проверенных endpoint: непрерывный поток повторных due-проверок не может навсегда
/// вытеснить новый каталог. Обе ветви используют отдельный operational index.
/// </summary>
internal static class VpnValidationQueue
{
    internal static async Task<VpnValidationCandidate[]> SelectAsync(
        ProxyHarborDbContext db,
        int batchSize,
        DateTimeOffset now,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        // Загружаем только четыре поля, реально нужные сетевому probe. ConnectionUri и
        // остальные широкие catalog-поля больше не материализуются в каждом цикле.
        var neverChecked = await db.VpnEndpoints.AsNoTracking()
            .Where(endpoint => endpoint.NextCheckAt == null)
            .OrderBy(endpoint => endpoint.LastCheckedAt)
            .ThenBy(endpoint => endpoint.Id)
            .Select(endpoint => new VpnValidationCandidate(
                endpoint.Id, endpoint.Host, endpoint.Port, endpoint.Transport))
            .Take(batchSize)
            .ToArrayAsync(token);

        var reservedForNeverChecked = Math.Min(neverChecked.Length, NeverCheckedQuota(batchSize));
        var dueCapacity = batchSize - reservedForNeverChecked;
        var due = dueCapacity == 0
            ? []
            : await db.VpnEndpoints.AsNoTracking()
                .Where(endpoint => endpoint.NextCheckAt <= now)
                .OrderBy(endpoint => endpoint.NextCheckAt)
                .ThenBy(endpoint => endpoint.LastCheckedAt)
                .ThenBy(endpoint => endpoint.Id)
                .Select(endpoint => new VpnValidationCandidate(
                    endpoint.Id, endpoint.Host, endpoint.Port, endpoint.Transport))
                .Take(dueCapacity)
                .ToArrayAsync(token);

        var selected = new List<VpnValidationCandidate>(batchSize);
        selected.AddRange(due);
        var neverCheckedToTake = Math.Min(batchSize - selected.Count, neverChecked.Length);
        for (var index = 0; index < neverCheckedToTake; index++)
            selected.Add(neverChecked[index]);

        return selected.ToArray();
    }

    /// <summary>Гарантирует обеим очередям прогресс при production batch size не меньше двух.</summary>
    internal static int NeverCheckedQuota(int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return Math.Max(1, batchSize / 2);
    }
}

/// <summary>Узкая detached-проекция VPN endpoint для сетевой проверки.</summary>
internal readonly record struct VpnValidationCandidate(Guid Id, string Host, int Port, string Transport);
