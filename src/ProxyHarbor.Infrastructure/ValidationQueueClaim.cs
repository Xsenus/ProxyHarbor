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
