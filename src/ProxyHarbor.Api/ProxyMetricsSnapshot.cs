using System.Data;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
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
    IReadOnlyList<ProxyCountryMetrics> Countries,
    long Due,
    long Leased,
    long NeverAttempted,
    long StaleUnseen,
    long Published,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? OldestActiveAt,
    DateTimeOffset CapturedAt);

internal sealed record ProxyCountryMetrics(string Code, long Count);

/// <summary>Одна компактная status/protocol-строка PostgreSQL partial aggregate.</summary>
internal sealed record ProxyMetricsRow(
    ProxyStatus Status,
    ProxyProtocol Protocol,
    long Count,
    long EverAlive,
    long HistoricalDead,
    long Due,
    long Leased,
    long NeverChecked,
    long NeverAttempted,
    long RepeatedlyFailing,
    long StaleUnseen,
    long Published,
    long StaleAlive,
    long Scheduled,
    long FreshLatencyTotal,
    long FreshLatencySamples,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? OldestActiveAt);

/// <summary>Одна физическая country/status/protocol строка PostgreSQL aggregate.</summary>
internal sealed record ProxyMetricsRawRow(
    string? CountryCode,
    ProxyStatus Status,
    ProxyProtocol Protocol,
    long Count,
    long EverAlive,
    long HistoricalDead,
    long Due,
    long Leased,
    long NeverChecked,
    long NeverAttempted,
    long RepeatedlyFailing,
    long StaleUnseen,
    long Published,
    long StaleAlive,
    long Scheduled,
    long FreshLatencyTotal,
    long FreshLatencySamples,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? OldestActiveAt);

/// <summary>
/// Читает все proxy-derived Prometheus gauges одним параллельным проходом таблицы.
/// Отдельный reader нужен, чтобы не вернуть в controller три визуально безобидных,
/// но дорогих полного scan при последующих изменениях набора метрик.
/// </summary>
internal static class ProxyMetricsSnapshotReader
{
    internal const string PostgresSql = """
        SELECT proxy."CountryCode",
               proxy."Status",
               proxy."Protocol",
               count(*)::bigint AS "Count",
               count(*) FILTER (WHERE
                   proxy."FirstAliveAt" IS NOT NULL OR proxy."SuccessfulChecks" > 0)::bigint AS "EverAlive",
               count(*) FILTER (WHERE
                   proxy."Status" = @dead_status AND
                   (proxy."FirstAliveAt" IS NOT NULL OR proxy."SuccessfulChecks" > 0))::bigint AS "HistoricalDead",
               count(*) FILTER (WHERE
                   (proxy."NextCheckAt" IS NULL OR proxy."NextCheckAt" <= @now) AND
                   (lease."LeaseUntil" IS NULL OR lease."LeaseUntil" < @now))::bigint AS "Due",
               count(*) FILTER (WHERE lease."LeaseUntil" >= @now)::bigint AS "Leased",
               count(*) FILTER (WHERE proxy."LastCheckedAt" IS NULL)::bigint AS "NeverChecked",
               count(*) FILTER (WHERE proxy."LastValidationAttemptAt" IS NULL)::bigint AS "NeverAttempted",
               count(*) FILTER (WHERE proxy."ConsecutiveFailedChecks" >= 3)::bigint AS "RepeatedlyFailing",
               count(*) FILTER (WHERE
                   proxy."Status" IN (@pending_status, @dead_status) AND
                   proxy."LastSeenAt" < @retention_cutoff AND
                   (lease."LeaseUntil" IS NULL OR lease."LeaseUntil" < @now))::bigint AS "StaleUnseen",
               count(*) FILTER (WHERE
                   proxy."Status" = @alive_status AND proxy."LastCheckedAt" >= @fresh_after)::bigint AS "Published",
               count(*) FILTER (WHERE proxy."Status" = @alive_status AND
                   (proxy."LastCheckedAt" IS NULL OR proxy."LastCheckedAt" < @fresh_after))::bigint AS "StaleAlive",
               count(*) FILTER (WHERE proxy."NextCheckAt" > @now)::bigint AS "Scheduled",
               coalesce(sum(proxy."LatencyMs"::bigint) FILTER (WHERE
                   proxy."Status" = @alive_status AND proxy."LastCheckedAt" >= @fresh_after AND
                   proxy."LatencyMs" IS NOT NULL), 0)::bigint AS "FreshLatencyTotal",
               count(*) FILTER (WHERE
                   proxy."Status" = @alive_status AND proxy."LastCheckedAt" >= @fresh_after AND
                   proxy."LatencyMs" IS NOT NULL)::bigint AS "FreshLatencySamples",
               max(proxy."LastValidationAttemptAt") AS "LastAttemptAt",
               min(proxy."CurrentAliveSince") FILTER (WHERE
                   proxy."Status" = @alive_status AND proxy."CurrentAliveSince" IS NOT NULL) AS "OldestActiveAt"
        FROM "Proxies" AS proxy
        LEFT JOIN "ProxyValidationLeases" AS lease ON lease."ProxyId" = proxy."Id"
        GROUP BY proxy."CountryCode", proxy."Status", proxy."Protocol"
        ORDER BY proxy."CountryCode" NULLS FIRST, proxy."Status", proxy."Protocol"
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

        var rows = new List<ProxyMetricsRawRow>(1_024);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new ProxyMetricsRawRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                (ProxyStatus)reader.GetInt32(1),
                (ProxyProtocol)reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetInt64(14),
                reader.GetInt64(15),
                reader.GetInt64(16),
                reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
                reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18)));
        }

        return Aggregate(rows, now);
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
        var query =
            from proxy in db.Proxies.AsNoTracking()
            join lease in db.ProxyValidationLeases.AsNoTracking()
                on proxy.Id equals lease.ProxyId into proxyLeases
            from lease in proxyLeases.DefaultIfEmpty()
            select new { Proxy = proxy, Lease = lease };
        var rows = await query
            .GroupBy(item => new
            {
                item.Proxy.CountryCode,
                item.Proxy.Status,
                item.Proxy.Protocol
            })
            .Select(group => new ProxyMetricsRawRow(
                group.Key.CountryCode,
                group.Key.Status,
                group.Key.Protocol,
                group.LongCount(),
                group.LongCount(item => item.Proxy.FirstAliveAt != null || item.Proxy.SuccessfulChecks > 0),
                group.LongCount(item => item.Proxy.Status == ProxyStatus.Dead &&
                    (item.Proxy.FirstAliveAt != null || item.Proxy.SuccessfulChecks > 0)),
                group.LongCount(item =>
                    (item.Proxy.NextCheckAt == null || item.Proxy.NextCheckAt <= now) &&
                    (item.Lease == null || item.Lease.LeaseUntil < now)),
                group.LongCount(item => item.Lease != null && item.Lease.LeaseUntil >= now),
                group.LongCount(item => item.Proxy.LastCheckedAt == null),
                group.LongCount(item => item.Proxy.LastValidationAttemptAt == null),
                group.LongCount(item => item.Proxy.ConsecutiveFailedChecks >= 3),
                group.LongCount(item =>
                    (item.Proxy.Status == ProxyStatus.Pending || item.Proxy.Status == ProxyStatus.Dead) &&
                    item.Proxy.LastSeenAt < retentionCutoff &&
                    (item.Lease == null || item.Lease.LeaseUntil < now)),
                group.LongCount(item => item.Proxy.Status == ProxyStatus.Alive &&
                    item.Proxy.LastCheckedAt >= freshAfter),
                group.LongCount(item => item.Proxy.Status == ProxyStatus.Alive &&
                    (item.Proxy.LastCheckedAt == null || item.Proxy.LastCheckedAt < freshAfter)),
                group.LongCount(item => item.Proxy.NextCheckAt > now),
                group.Where(item => item.Proxy.Status == ProxyStatus.Alive &&
                    item.Proxy.LastCheckedAt >= freshAfter && item.Proxy.LatencyMs != null)
                    .Sum(item => (long?)item.Proxy.LatencyMs) ?? 0,
                group.LongCount(item => item.Proxy.Status == ProxyStatus.Alive &&
                    item.Proxy.LastCheckedAt >= freshAfter && item.Proxy.LatencyMs != null),
                group.Max(item => item.Proxy.LastValidationAttemptAt),
                group.Where(item => item.Proxy.Status == ProxyStatus.Alive &&
                    item.Proxy.CurrentAliveSince != null)
                    .Min(item => (DateTimeOffset?)item.Proxy.CurrentAliveSince)))
            .ToArrayAsync(token);
        return Aggregate(rows, now);
    }

    private static ProxyMetricsSnapshot Aggregate(IReadOnlyList<ProxyMetricsRawRow> rows, DateTimeOffset capturedAt)
    {
        var groups = rows
            .GroupBy(row => (row.Status, row.Protocol))
            .Select(group => new ProxyMetricsRow(
                group.Key.Status,
                group.Key.Protocol,
                group.Sum(row => row.Count),
                group.Sum(row => row.EverAlive),
                group.Sum(row => row.HistoricalDead),
                group.Sum(row => row.Due),
                group.Sum(row => row.Leased),
                group.Sum(row => row.NeverChecked),
                group.Sum(row => row.NeverAttempted),
                group.Sum(row => row.RepeatedlyFailing),
                group.Sum(row => row.StaleUnseen),
                group.Sum(row => row.Published),
                group.Sum(row => row.StaleAlive),
                group.Sum(row => row.Scheduled),
                group.Sum(row => row.FreshLatencyTotal),
                group.Sum(row => row.FreshLatencySamples),
                group.Max(row => row.LastAttemptAt),
                group.Min(row => row.OldestActiveAt)))
            .OrderBy(row => row.Status)
            .ThenBy(row => row.Protocol)
            .ToArray();
        var countries = rows
            .Where(row => !string.IsNullOrEmpty(row.CountryCode))
            .GroupBy(row => row.CountryCode!, StringComparer.Ordinal)
            .Select(group => new ProxyCountryMetrics(group.Key, group.Sum(row => row.Count)))
            .OrderByDescending(country => country.Count)
            .ThenBy(country => country.Code, StringComparer.Ordinal)
            .ToArray();
        long due = 0;
        long leased = 0;
        long neverAttempted = 0;
        long staleUnseen = 0;
        long published = 0;
        DateTimeOffset? lastAttemptAt = null;
        DateTimeOffset? oldestActiveAt = null;
        foreach (var row in groups)
        {
            due += row.Due;
            leased += row.Leased;
            neverAttempted += row.NeverAttempted;
            staleUnseen += row.StaleUnseen;
            published += row.Published;
            if (row.LastAttemptAt is { } candidate &&
                (lastAttemptAt is null || candidate > lastAttemptAt.Value))
                lastAttemptAt = candidate;
            if (row.OldestActiveAt is { } oldestCandidate &&
                (oldestActiveAt is null || oldestCandidate < oldestActiveAt.Value))
                oldestActiveAt = oldestCandidate;
        }

        return new ProxyMetricsSnapshot(
            groups, countries, due, leased, neverAttempted, staleUnseen, published,
            lastAttemptAt, oldestActiveAt, capturedAt);
    }
}

/// <summary>
/// Разделяет один дорогой proxy aggregate между публичной сводкой и Prometheus.
/// Одновременные cache misses объединяются, а при кратком сбое БД возвращается
/// последний согласованный снимок вместо задержки/ошибки пользовательского запроса.
/// </summary>
public sealed class ProxyMetricsSnapshotCache(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<CollectorOptions> collectorOptions,
    ILogger<ProxyMetricsSnapshotCache> logger,
    TimeProvider timeProvider) : IDisposable
{
    private static readonly Action<ILogger, DateTimeOffset, Exception?> RefreshFailed =
        LoggerMessage.Define<DateTimeOffset>(
            LogLevel.Warning,
            new EventId(1501, "ProxyMetricsSnapshotRefreshFailed"),
            "Не удалось обновить proxy metrics snapshot; используется снимок {CapturedAt}.");
    // Exact aggregation currently reads the entire large proxy table. A one-minute
    // soft TTL keeps dashboards current without spending database CPU when nobody
    // consumes the snapshot. Five minutes is the fail-safe maximum: a missing
    // background consumer then makes the request refresh synchronously.
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumStaleAge = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Channel<byte> _refreshRequests = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private CacheEntry? _current;
    private long _databaseReads;
    private long _refreshRequestsQueued;
    private long _refreshRequestsCoalesced;

    internal Task<ProxyMetricsSnapshot> GetAsync(CancellationToken token)
    {
        var observed = Volatile.Read(ref _current);
        var now = timeProvider.GetUtcNow();
        if (observed is null || now - observed.StoredAt >= MaximumStaleAge)
            return GetOrRefreshAsync(force: false, token);
        if (IsFresh(observed, now)) return Task.FromResult(observed.Snapshot);

        // Serve the last internally consistent snapshot immediately. The bounded
        // signal means any number of simultaneous /stats and /metrics consumers
        // causes at most one background full-table aggregate.
        if (_refreshRequests.Writer.TryWrite(0))
            Interlocked.Increment(ref _refreshRequestsQueued);
        else
            Interlocked.Increment(ref _refreshRequestsCoalesced);
        return Task.FromResult(observed.Snapshot);
    }

    internal Task<ProxyMetricsSnapshot> RefreshAsync(CancellationToken token) =>
        GetOrRefreshAsync(force: true, token);

    internal Task<ProxyMetricsSnapshot> WarmAsync(CancellationToken token) =>
        GetOrRefreshAsync(force: false, token);

    internal ValueTask<bool> WaitForRefreshRequestAsync(CancellationToken token) =>
        _refreshRequests.Reader.WaitToReadAsync(token);

    internal void DrainRefreshRequests()
    {
        while (_refreshRequests.Reader.TryRead(out _)) { }
    }

    internal long DatabaseReads => Interlocked.Read(ref _databaseReads);
    internal long RefreshRequestsQueued => Interlocked.Read(ref _refreshRequestsQueued);
    internal long RefreshRequestsCoalesced => Interlocked.Read(ref _refreshRequestsCoalesced);

    private async Task<ProxyMetricsSnapshot> GetOrRefreshAsync(bool force, CancellationToken token)
    {
        var observed = Volatile.Read(ref _current);
        if (!force && IsFresh(observed, timeProvider.GetUtcNow())) return observed!.Snapshot;

        await _refreshGate.WaitAsync(token);
        try
        {
            var latest = Volatile.Read(ref _current);
            if (IsFresh(latest, timeProvider.GetUtcNow()) &&
                (!force || !ReferenceEquals(latest, observed)))
                return latest!.Snapshot;

            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(token);
                var now = timeProvider.GetUtcNow();
                var options = collectorOptions.Value;
                Interlocked.Increment(ref _databaseReads);
                var snapshot = await BufferedReadSnapshot.ExecuteAsync(db,
                    innerToken => ProxyMetricsSnapshotReader.ReadAsync(
                        db,
                        now,
                        now.AddDays(-Math.Max(1, options.DeadRetentionDays)),
                        now.AddMinutes(-options.PublicFreshnessMinutes),
                        innerToken),
                    token);
                Volatile.Write(ref _current, new CacheEntry(snapshot, timeProvider.GetUtcNow()));
                return snapshot;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (latest is not null)
            {
                RefreshFailed(logger, latest.Snapshot.CapturedAt, exception);
                return latest.Snapshot;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static bool IsFresh(CacheEntry? entry, DateTimeOffset now) =>
        entry is not null && now - entry.StoredAt < MaximumAge;

    /// <summary>Завершает ожидающий demand-worker и освобождает gate singleton-кэша.</summary>
    public void Dispose()
    {
        _refreshRequests.Writer.TryComplete();
        _refreshGate.Dispose();
    }

    private sealed record CacheEntry(ProxyMetricsSnapshot Snapshot, DateTimeOffset StoredAt);
}

/// <summary>
/// Один раз прогревает aggregate, затем обновляет его только по объединённому
/// demand-сигналу. В простое большая таблица больше не сканируется по таймеру.
/// </summary>
internal sealed class ProxyMetricsSnapshotRefreshWorker(
    ProxyMetricsSnapshotCache cache,
    ILogger<ProxyMetricsSnapshotRefreshWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> BackgroundRefreshFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1502, "ProxyMetricsSnapshotBackgroundRefreshFailed"),
        "Фоновое обновление proxy metrics snapshot не удалось.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            cache.DrainRefreshRequests();
            await cache.WarmAsync(stoppingToken);
            cache.DrainRefreshRequests();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // Startup failure must not terminate the demand consumer. A later
            // successful cold request can populate the cache and wake this worker.
            BackgroundRefreshFailed(logger, exception);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await cache.WaitForRefreshRequestAsync(stoppingToken)) break;
                // Сигналы, накопленные до и во время текущего refresh, относятся
                // к одному устаревшему snapshot и не должны запускать второй scan.
                cache.DrainRefreshRequests();
                await cache.RefreshAsync(stoppingToken);
                cache.DrainRefreshRequests();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                BackgroundRefreshFailed(logger, exception);
                cache.DrainRefreshRequests();
            }
        }
    }
}
