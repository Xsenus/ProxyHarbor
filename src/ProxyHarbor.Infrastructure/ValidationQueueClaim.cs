using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Общий bounded claim очереди прокси. Свободные строки и просроченные lease
/// выбираются раздельно: горячий путь использует частичный индекс без активно
/// занятых строк, а failover — компактный индекс только непустых сроков аренды.
/// Каждый диапазон статуса и NULL/non-NULL NextCheckAt остаётся отдельным seek.
/// Вызывающий код обязан удерживать транзакцию до сохранения lease.
/// </summary>
internal static class ValidationQueueClaim
{
    internal static async Task<List<ProxyEndpoint>> ClaimAsync(
        ProxyHarborDbContext db,
        int batchSize,
        DateTimeOffset now,
        CancellationToken token)
    {
        var claimed = new List<ProxyEndpoint>(batchSize);
        var hasExpiredLeases = await db.Proxies.AsNoTracking().AnyAsync(
            proxy => proxy.CheckLeaseUntil < now &&
                     (proxy.NextCheckAt == null || proxy.NextCheckAt <= now), token);
        for (var priority = 0; priority <= 2 && claimed.Count < batchSize; priority++)
        {
            if (hasExpiredLeases)
            {
                var remainingExpired = batchSize - claimed.Count;
                claimed.AddRange(await db.Proxies.FromSqlInterpolated($"""
                    SELECT * FROM "Proxies"
                    WHERE CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END = {priority}
                      AND ("NextCheckAt" IS NULL OR "NextCheckAt" <= {now})
                      AND "CheckLeaseUntil" < {now}
                    ORDER BY "NextCheckAt" NULLS FIRST, "LastCheckedAt" NULLS FIRST
                    LIMIT {remainingExpired}
                    FOR UPDATE SKIP LOCKED
                    """).AsNoTracking().ToListAsync(token));
            }

            if (claimed.Count >= batchSize) continue;
            var remaining = batchSize - claimed.Count;
            claimed.AddRange(await db.Proxies.FromSqlInterpolated($"""
                SELECT * FROM "Proxies"
                WHERE CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END = {priority}
                  AND "NextCheckAt" IS NULL
                  AND "CheckLeaseUntil" IS NULL
                ORDER BY "LastCheckedAt" NULLS FIRST
                LIMIT {remaining}
                FOR UPDATE SKIP LOCKED
                """).AsNoTracking().ToListAsync(token));

            if (claimed.Count >= batchSize) continue;
            remaining = batchSize - claimed.Count;
            claimed.AddRange(await db.Proxies.FromSqlInterpolated($"""
                SELECT * FROM "Proxies"
                WHERE CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END = {priority}
                  AND "NextCheckAt" <= {now}
                  AND "CheckLeaseUntil" IS NULL
                ORDER BY "NextCheckAt", "LastCheckedAt" NULLS FIRST
                LIMIT {remaining}
                FOR UPDATE SKIP LOCKED
                """).AsNoTracking().ToListAsync(token));
        }

        return claimed;
    }
}

/// <summary>
/// Bounded dequeue VPN-каталога. Глобальная advisory-lock сериализует validator,
/// поэтому здесь не нужны row leases; разделение due/NULL превращает OR и полную
/// сортировку каталога в два коротких диапазона operational index.
/// </summary>
internal static class VpnValidationQueue
{
    internal static async Task<VpnEndpoint[]> SelectAsync(
        ProxyHarborDbContext db,
        int batchSize,
        DateTimeOffset now,
        CancellationToken token)
    {
        var selected = new List<VpnEndpoint>(batchSize);
        selected.AddRange(await db.VpnEndpoints.FromSqlInterpolated($"""
            SELECT * FROM "VpnEndpoints"
            WHERE "NextCheckAt" <= {now}
            ORDER BY "NextCheckAt", "LastCheckedAt" NULLS FIRST, "Id"
            LIMIT {batchSize}
            """).AsNoTracking().ToListAsync(token));

        if (selected.Count < batchSize)
        {
            var remaining = batchSize - selected.Count;
            selected.AddRange(await db.VpnEndpoints.FromSqlInterpolated($"""
                SELECT * FROM "VpnEndpoints"
                WHERE "NextCheckAt" IS NULL
                ORDER BY "LastCheckedAt" NULLS FIRST, "Id"
                LIMIT {remaining}
                """).AsNoTracking().ToListAsync(token));
        }

        return selected.ToArray();
    }
}
