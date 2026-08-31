using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>
/// Согласованный срез счётчиков большой таблицы proxy. Общие значения являются
/// суммой максимум двенадцати status/protocol-групп, а не результатом второго scan.
/// </summary>
internal sealed record ProxyMetricsSnapshot(
    IReadOnlyList<ProxyMetricsRow> Groups,
    long Due,
    long Leased,
    long NeverAttempted,
    long StaleUnseen,
    long Published,
    DateTimeOffset? LastAttemptAt);

/// <summary>Одна компактная status/protocol-строка PostgreSQL partial aggregate.</summary>
internal sealed record ProxyMetricsRow(
    ProxyStatus Status,
    ProxyProtocol Protocol,
    long Count,
    long Due,
    long Leased,
    long NeverAttempted,
    long StaleUnseen,
    long Published,
    DateTimeOffset? LastAttemptAt);

/// <summary>
/// Читает все proxy-derived Prometheus gauges одним параллельным проходом таблицы.
/// Отдельный reader нужен, чтобы не вернуть в controller три визуально безобидных,
/// но дорогих полного scan при последующих изменениях набора метрик.
/// </summary>
internal static class ProxyMetricsSnapshotReader
{
    internal const string PostgresSql = """
        SELECT "Status",
               "Protocol",
               count(*)::bigint AS "Count",
               count(*) FILTER (WHERE
                   ("NextCheckAt" IS NULL OR "NextCheckAt" <= @now) AND
                   ("CheckLeaseUntil" IS NULL OR "CheckLeaseUntil" < @now))::bigint AS "Due",
               count(*) FILTER (WHERE "CheckLeaseUntil" >= @now)::bigint AS "Leased",
               count(*) FILTER (WHERE "LastValidationAttemptAt" IS NULL)::bigint AS "NeverAttempted",
               count(*) FILTER (WHERE
                   "Status" IN (@pending_status, @dead_status) AND
                   "LastSeenAt" < @retention_cutoff AND
                   ("CheckLeaseUntil" IS NULL OR "CheckLeaseUntil" < @now))::bigint AS "StaleUnseen",
               count(*) FILTER (WHERE
                   "Status" = @alive_status AND "LastCheckedAt" >= @fresh_after)::bigint AS "Published",
               max("LastValidationAttemptAt") AS "LastAttemptAt"
        FROM "Proxies"
        GROUP BY "Status", "Protocol"
        ORDER BY "Status", "Protocol"
        """;

    internal static async Task<ProxyMetricsSnapshot> ReadAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        DateTimeOffset retentionCutoff,
        DateTimeOffset freshAfter,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.IsNpgsql()
            ? await ReadPostgresAsync(db, now, retentionCutoff, freshAfter, token)
            : await ReadEfFallbackAsync(db, now, retentionCutoff, freshAfter, token);
    }

    private static async Task<ProxyMetricsSnapshot> ReadPostgresAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        DateTimeOffset retentionCutoff,
        DateTimeOffset freshAfter,
        CancellationToken token)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);

        // MetricsController открывает REPEATABLE READ до вызова reader. Явная
        // привязка raw-команды сохраняет ту же эпоху, что и остальные EF-запросы.
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        await using var command = new NpgsqlCommand(PostgresSql, connection, transaction);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("retention_cutoff", NpgsqlDbType.TimestampTz, retentionCutoff);
        command.Parameters.AddWithValue("fresh_after", NpgsqlDbType.TimestampTz, freshAfter);
        command.Parameters.AddWithValue("pending_status", NpgsqlDbType.Integer, (int)ProxyStatus.Pending);
        command.Parameters.AddWithValue("dead_status", NpgsqlDbType.Integer, (int)ProxyStatus.Dead);
        command.Parameters.AddWithValue("alive_status", NpgsqlDbType.Integer, (int)ProxyStatus.Alive);

        var rows = new List<ProxyMetricsRow>(12);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new ProxyMetricsRow(
                (ProxyStatus)reader.GetInt32(0),
                (ProxyProtocol)reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return Aggregate(rows);
    }

    /// <summary>
    /// InMemory/SQLite остаются переносимым тестовым контуром. Production сюда не
    /// попадает, поэтому provider-neutral LINQ важнее числа SQL round-trip.
    /// </summary>
    private static async Task<ProxyMetricsSnapshot> ReadEfFallbackAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        DateTimeOffset retentionCutoff,
        DateTimeOffset freshAfter,
        CancellationToken token)
    {
        var rows = await db.Proxies.AsNoTracking()
            .GroupBy(proxy => new { proxy.Status, proxy.Protocol })
            .Select(group => new ProxyMetricsRow(
                group.Key.Status,
                group.Key.Protocol,
                group.LongCount(),
                group.LongCount(proxy =>
                    (proxy.NextCheckAt == null || proxy.NextCheckAt <= now) &&
                    (proxy.CheckLeaseUntil == null || proxy.CheckLeaseUntil < now)),
                group.LongCount(proxy => proxy.CheckLeaseUntil >= now),
                group.LongCount(proxy => proxy.LastValidationAttemptAt == null),
                group.LongCount(proxy =>
                    (proxy.Status == ProxyStatus.Pending || proxy.Status == ProxyStatus.Dead) &&
                    proxy.LastSeenAt < retentionCutoff &&
                    (proxy.CheckLeaseUntil == null || proxy.CheckLeaseUntil < now)),
                group.LongCount(proxy =>
                    proxy.Status == ProxyStatus.Alive && proxy.LastCheckedAt >= freshAfter),
                group.Max(proxy => proxy.LastValidationAttemptAt)))
            .ToArrayAsync(token);
        return Aggregate(rows);
    }

    private static ProxyMetricsSnapshot Aggregate(IReadOnlyList<ProxyMetricsRow> rows)
    {
        long due = 0;
        long leased = 0;
        long neverAttempted = 0;
        long staleUnseen = 0;
        long published = 0;
        DateTimeOffset? lastAttemptAt = null;
        foreach (var row in rows)
        {
            due += row.Due;
            leased += row.Leased;
            neverAttempted += row.NeverAttempted;
            staleUnseen += row.StaleUnseen;
            published += row.Published;
            if (row.LastAttemptAt is { } candidate &&
                (lastAttemptAt is null || candidate > lastAttemptAt.Value))
                lastAttemptAt = candidate;
        }

        return new ProxyMetricsSnapshot(
            rows, due, leased, neverAttempted, staleUnseen, published, lastAttemptAt);
    }
}
