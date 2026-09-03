using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Общий bounded claim очереди прокси. Короткая cluster-wide xact-lock сериализует
/// только выбор партии, а эфемерная узкая таблица lease принимает все ownership-
/// записи. Основная строка прокси при claim/heartbeat больше не переписывается.
/// Каждый диапазон статуса и NULL/non-NULL NextCheckAt остаётся отдельным seek;
/// просроченная аренда заменяется условным UPSERT и не может победить heartbeat.
/// </summary>
internal static class ValidationQueueClaim
{
    private const string ExpiredClaimSql = """
        WITH candidate AS MATERIALIZED (
            SELECT proxy."Id", proxy."Host", proxy."Port", proxy."Protocol",
                   proxy."ConsecutiveFailedChecks",
                   lease."LeaseId" AS "PreviousLeaseId",
                   proxy."NextCheckAt", proxy."LastCheckedAt"
            FROM "Proxies" AS proxy
            JOIN "ProxyValidationLeases" AS lease ON lease."ProxyId" = proxy."Id"
            WHERE CASE proxy."Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END = @priority
              AND (proxy."NextCheckAt" IS NULL OR proxy."NextCheckAt" <= @now)
              AND lease."LeaseUntil" < @now
            ORDER BY proxy."NextCheckAt" NULLS FIRST, proxy."LastCheckedAt" NULLS FIRST
            LIMIT @limit
            FOR UPDATE OF lease SKIP LOCKED
        ), claimed AS (
            INSERT INTO "ProxyValidationLeases" ("ProxyId", "LeaseId", "LeaseUntil")
            SELECT candidate."Id", @lease_id, @lease_until
            FROM candidate
            ON CONFLICT ("ProxyId") DO UPDATE
            SET "LeaseId" = EXCLUDED."LeaseId",
                "LeaseUntil" = EXCLUDED."LeaseUntil"
            WHERE "ProxyValidationLeases"."LeaseUntil" < @now
            RETURNING "ProxyId"
        )
        SELECT candidate."Id", candidate."Host", candidate."Port", candidate."Protocol",
               candidate."ConsecutiveFailedChecks", candidate."PreviousLeaseId"
        FROM claimed
        JOIN candidate ON candidate."Id" = claimed."ProxyId"
        ORDER BY candidate."NextCheckAt" NULLS FIRST, candidate."LastCheckedAt" NULLS FIRST
        """;

    private const string NeverCheckedClaimSql = """
        WITH candidate AS MATERIALIZED (
            SELECT proxy."Id", proxy."Host", proxy."Port", proxy."Protocol",
                   proxy."ConsecutiveFailedChecks",
                   NULL::uuid AS "PreviousLeaseId",
                   proxy."NextCheckAt", proxy."LastCheckedAt"
            FROM "Proxies" AS proxy
            WHERE CASE proxy."Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END = @priority
              AND proxy."NextCheckAt" IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "ProxyValidationLeases" AS lease
                  WHERE lease."ProxyId" = proxy."Id")
            ORDER BY proxy."LastCheckedAt" NULLS FIRST
            LIMIT @limit
        ), claimed AS (
            INSERT INTO "ProxyValidationLeases" ("ProxyId", "LeaseId", "LeaseUntil")
            SELECT candidate."Id", @lease_id, @lease_until
            FROM candidate
            ON CONFLICT ("ProxyId") DO NOTHING
            RETURNING "ProxyId"
        )
        SELECT candidate."Id", candidate."Host", candidate."Port", candidate."Protocol",
               candidate."ConsecutiveFailedChecks", candidate."PreviousLeaseId"
        FROM claimed
        JOIN candidate ON candidate."Id" = claimed."ProxyId"
        ORDER BY candidate."NextCheckAt" NULLS FIRST, candidate."LastCheckedAt" NULLS FIRST
        """;

    private const string DueClaimSql = """
        WITH candidate AS MATERIALIZED (
            SELECT proxy."Id", proxy."Host", proxy."Port", proxy."Protocol",
                   proxy."ConsecutiveFailedChecks",
                   NULL::uuid AS "PreviousLeaseId",
                   proxy."NextCheckAt", proxy."LastCheckedAt"
            FROM "Proxies" AS proxy
            WHERE CASE proxy."Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END = @priority
              AND proxy."NextCheckAt" <= @now
              AND NOT EXISTS (
                  SELECT 1 FROM "ProxyValidationLeases" AS lease
                  WHERE lease."ProxyId" = proxy."Id")
            ORDER BY proxy."NextCheckAt" NULLS FIRST, proxy."LastCheckedAt" NULLS FIRST
            LIMIT @limit
        ), claimed AS (
            INSERT INTO "ProxyValidationLeases" ("ProxyId", "LeaseId", "LeaseUntil")
            SELECT candidate."Id", @lease_id, @lease_until
            FROM candidate
            ON CONFLICT ("ProxyId") DO NOTHING
            RETURNING "ProxyId"
        )
        SELECT candidate."Id", candidate."Host", candidate."Port", candidate."Protocol",
               candidate."ConsecutiveFailedChecks", candidate."PreviousLeaseId"
        FROM claimed
        JOIN candidate ON candidate."Id" = claimed."ProxyId"
        ORDER BY candidate."NextCheckAt" NULLS FIRST, candidate."LastCheckedAt" NULLS FIRST
        """;

    internal static async Task<List<ValidationClaimCandidate>> ClaimAndLeaseAsync(
        ProxyHarborDbContext db,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        Guid leaseId,
        ValidationClaimIdleGate? idleGate,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        if (leaseUntil <= now)
            throw new ArgumentOutOfRangeException(nameof(leaseUntil), "Lease должен завершаться после момента claim.");
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Validation claim требует явной транзакции.");

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(token);
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction();
        await PostgresAdvisoryLock.AcquireTransactionAsync(
            connection, transaction, PostgresAdvisoryLock.ProxyValidationClaimKey, token);

        var claimed = new List<ValidationClaimCandidate>(batchSize);
        // Внешняя process-local проверка могла быть пройдена одновременно несколькими
        // VPS. После cluster-wide lock первый запрос уже успевает подтвердить пустую
        // очередь; остальные завершаются без повторных index seek по 900k+ строк.
        if (idleGate?.TryCoalesceSerializedProbe() == true) return claimed;

        var hasExpiredLeases = await db.ProxyValidationLeases.AsNoTracking()
            .AnyAsync(lease => lease.LeaseUntil < now, token);
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

        // Любая недозаполненная партия уже исчерпала все status/null/due ranges
        // текущего snapshot. Не заставляем остальные VPS немедленно повторять те же
        // пустые seek: новый due/import всё равно будет замечен максимум через
        // существующий двухсекундный bounded cooldown.
        idleGate?.MarkClaimResult(claimed.Count, batchSize);
        return claimed;
    }

    internal static Task<List<ValidationClaimCandidate>> ClaimAndLeaseAsync(
        ProxyHarborDbContext db,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        Guid leaseId,
        CancellationToken token) =>
        ClaimAndLeaseAsync(db, batchSize, now, leaseUntil, leaseId, idleGate: null, token);

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
        var neverCheckedQuota = NeverCheckedQuota(batchSize);
        var neverChecked = await db.VpnEndpoints.AsNoTracking()
            .Where(endpoint => endpoint.NextCheckAt == null)
            .OrderBy(endpoint => endpoint.LastCheckedAt)
            .ThenBy(endpoint => endpoint.Id)
            .Select(endpoint => new VpnValidationCandidate(
                endpoint.Id, endpoint.Host, endpoint.Port, endpoint.Transport))
            .Take(neverCheckedQuota)
            .ToArrayAsync(token);

        var reservedForNeverChecked = neverChecked.Length;
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
