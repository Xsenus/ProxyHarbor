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

/// <summary>Компактный общий срез административных счётчиков VPN-каталога.</summary>
internal sealed record VpnMetricsSnapshot(
    long Total,
    long Reachable,
    long Pending,
    long Unreachable,
    long Unsupported,
    long EverReachable,
    long ReachableLatencyTotal,
    long ReachableLatencySamples,
    DateTimeOffset? OldestReachableAt,
    long NeverChecked,
    long Due,
    long FreshReachable,
    long StaleReachable,
    long CheckedLastFiveMinutes,
    DateTimeOffset? LatestCheckedAt,
    IReadOnlyList<VpnCountryMetrics> Countries,
    IReadOnlyList<VpnFacetMetrics> Facets,
    DateTimeOffset CapturedAt);

internal sealed record VpnCountryMetrics(string Code, long Count);

/// <summary>
/// Точный компактный счётчик одного country/status/protocol/transport-сегмента.
/// Он строится тем же snapshot-проходом и устраняет отдельный count(*) при
/// переключении обычных фильтров административного VPN-каталога.
/// </summary>
internal sealed record VpnFacetMetrics(
    string? CountryCode,
    VpnEndpointStatus Status,
    VpnProtocol Protocol,
    string Transport,
    long Count);

internal sealed record VpnMetricsRow(
    string? CountryCode,
    VpnEndpointStatus Status,
    VpnProtocol Protocol,
    string Transport,
    long Count,
    long EverReachable,
    long ReachableLatencyTotal,
    long ReachableLatencySamples,
    DateTimeOffset? OldestReachableAt,
    long NeverChecked,
    long Due,
    long FreshReachable,
    long StaleReachable,
    long CheckedLastFiveMinutes,
    DateTimeOffset? LatestCheckedAt);

/// <summary>
/// Строит summary, список стран и facet-счётчики одним физическим проходом
/// VpnEndpoints вместо отдельных aggregate административной страницы.
/// </summary>
internal static class VpnMetricsSnapshotReader
{
    internal const string PostgresSql = """
        SELECT "CountryCode",
               "Status",
               "Protocol",
               "Transport",
               count(*)::bigint AS "Count",
               count(*) FILTER (WHERE "SuccessfulChecks" > 0)::bigint AS "EverReachable",
               coalesce(sum("LatencyMs"::bigint) FILTER (WHERE
                   "Status" = @reachable_status AND "LatencyMs" IS NOT NULL), 0)::bigint AS "ReachableLatencyTotal",
               count(*) FILTER (WHERE
                   "Status" = @reachable_status AND "LatencyMs" IS NOT NULL)::bigint AS "ReachableLatencySamples",
               min("FirstSeenAt") FILTER (WHERE "Status" = @reachable_status) AS "OldestReachableAt",
               count(*) FILTER (WHERE "LastCheckedAt" IS NULL)::bigint AS "NeverChecked",
               count(*) FILTER (WHERE "NextCheckAt" IS NULL OR "NextCheckAt" <= @captured_at)::bigint AS "Due",
               count(*) FILTER (WHERE "Status" = @reachable_status AND
                   "LastCheckedAt" >= @fresh_after)::bigint AS "FreshReachable",
               count(*) FILTER (WHERE "Status" = @reachable_status AND
                   ("LastCheckedAt" IS NULL OR "LastCheckedAt" < @fresh_after))::bigint AS "StaleReachable",
               count(*) FILTER (WHERE "LastCheckedAt" >= @recent_after)::bigint AS "CheckedLastFiveMinutes",
               max("LastCheckedAt") AS "LatestCheckedAt"
        FROM "VpnEndpoints"
        GROUP BY "CountryCode", "Status", "Protocol", "Transport"
        ORDER BY "CountryCode" NULLS FIRST, "Status", "Protocol", "Transport"
        """;

    internal static async Task<VpnMetricsSnapshot> ReadAsync(
        ProxyHarborDbContext db,
        DateTimeOffset capturedAt,
        int publicFreshnessMinutes,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = db.Database.IsNpgsql()
            ? await ReadPostgresAsync(db, capturedAt, publicFreshnessMinutes, token)
            : await ReadEfFallbackAsync(db, capturedAt, publicFreshnessMinutes, token);
        return Aggregate(rows, capturedAt);
    }

    internal static Task<VpnMetricsSnapshot> ReadAsync(
        ProxyHarborDbContext db,
        DateTimeOffset capturedAt,
        CancellationToken token) =>
        ReadAsync(db, capturedAt, new CollectorOptions().VpnPublicFreshnessMinutes, token);

    private static async Task<VpnMetricsRow[]> ReadPostgresAsync(
        ProxyHarborDbContext db,
        DateTimeOffset capturedAt,
        int publicFreshnessMinutes,
        CancellationToken token)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        await using var command = new NpgsqlCommand(PostgresSql, connection, transaction);
        command.Parameters.AddWithValue(
            "reachable_status", NpgsqlDbType.Integer, (int)VpnEndpointStatus.Reachable);
        command.Parameters.AddWithValue("captured_at", NpgsqlDbType.TimestampTz, capturedAt);
        command.Parameters.AddWithValue(
            "fresh_after", NpgsqlDbType.TimestampTz, capturedAt.AddMinutes(-Math.Max(1, publicFreshnessMinutes)));
        command.Parameters.AddWithValue("recent_after", NpgsqlDbType.TimestampTz, capturedAt.AddMinutes(-5));

        var rows = new List<VpnMetricsRow>(1_024);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new VpnMetricsRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                (VpnEndpointStatus)reader.GetInt32(1),
                (VpnProtocol)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14)));
        }
        return rows.ToArray();
    }

    private static Task<VpnMetricsRow[]> ReadEfFallbackAsync(
        ProxyHarborDbContext db,
        DateTimeOffset capturedAt,
        int publicFreshnessMinutes,
        CancellationToken token) =>
        db.VpnEndpoints.AsNoTracking()
            .GroupBy(endpoint => new
            {
                endpoint.CountryCode,
                endpoint.Status,
                endpoint.Protocol,
                endpoint.Transport
            })
            .Select(group => new VpnMetricsRow(
                group.Key.CountryCode,
                group.Key.Status,
                group.Key.Protocol,
                group.Key.Transport,
                group.LongCount(),
                group.LongCount(endpoint => endpoint.SuccessfulChecks > 0),
                group.Where(endpoint =>
                        endpoint.Status == VpnEndpointStatus.Reachable && endpoint.LatencyMs != null)
                    .Sum(endpoint => (long?)endpoint.LatencyMs) ?? 0,
                group.LongCount(endpoint =>
                    endpoint.Status == VpnEndpointStatus.Reachable && endpoint.LatencyMs != null),
                group.Where(endpoint => endpoint.Status == VpnEndpointStatus.Reachable)
                    .Min(endpoint => (DateTimeOffset?)endpoint.FirstSeenAt),
                group.LongCount(endpoint => endpoint.LastCheckedAt == null),
                group.LongCount(endpoint => endpoint.NextCheckAt == null || endpoint.NextCheckAt <= capturedAt),
                group.LongCount(endpoint => endpoint.Status == VpnEndpointStatus.Reachable &&
                    endpoint.LastCheckedAt >= capturedAt.AddMinutes(-Math.Max(1, publicFreshnessMinutes))),
                group.LongCount(endpoint => endpoint.Status == VpnEndpointStatus.Reachable &&
                    (endpoint.LastCheckedAt == null ||
                     endpoint.LastCheckedAt < capturedAt.AddMinutes(-Math.Max(1, publicFreshnessMinutes)))),
                group.LongCount(endpoint => endpoint.LastCheckedAt >= capturedAt.AddMinutes(-5)),
                group.Max(endpoint => endpoint.LastCheckedAt)))
            .ToArrayAsync(token);

    private static VpnMetricsSnapshot Aggregate(IReadOnlyList<VpnMetricsRow> rows, DateTimeOffset capturedAt)
    {
        var facets = rows
            .Select(row => new VpnFacetMetrics(
                row.CountryCode, row.Status, row.Protocol, row.Transport, row.Count))
            .ToArray();
        long total = 0;
        long reachable = 0;
        long pending = 0;
        long unreachable = 0;
        long unsupported = 0;
        long everReachable = 0;
        long latencyTotal = 0;
        long latencySamples = 0;
        DateTimeOffset? oldestReachableAt = null;
        long neverChecked = 0;
        long due = 0;
        long freshReachable = 0;
        long staleReachable = 0;
        long checkedLastFiveMinutes = 0;
        DateTimeOffset? latestCheckedAt = null;
        var countryCounts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            total += row.Count;
            everReachable += row.EverReachable;
            latencyTotal += row.ReachableLatencyTotal;
            latencySamples += row.ReachableLatencySamples;
            neverChecked += row.NeverChecked;
            due += row.Due;
            freshReachable += row.FreshReachable;
            staleReachable += row.StaleReachable;
            checkedLastFiveMinutes += row.CheckedLastFiveMinutes;
            switch (row.Status)
            {
                case VpnEndpointStatus.Reachable: reachable += row.Count; break;
                case VpnEndpointStatus.Pending: pending += row.Count; break;
                case VpnEndpointStatus.Unreachable: unreachable += row.Count; break;
                case VpnEndpointStatus.UnsupportedTransport: unsupported += row.Count; break;
                default: throw new InvalidOperationException($"Unknown VPN status {row.Status}.");
            }

            if (row.OldestReachableAt is { } candidate &&
                (oldestReachableAt is null || candidate < oldestReachableAt.Value))
                oldestReachableAt = candidate;
            if (row.LatestCheckedAt is { } latest &&
                (latestCheckedAt is null || latest > latestCheckedAt.Value))
                latestCheckedAt = latest;
            if (row.CountryCode is { Length: > 0 } code)
                countryCounts[code] = countryCounts.GetValueOrDefault(code) + row.Count;
        }

        var countries = countryCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new VpnCountryMetrics(pair.Key, pair.Value))
            .ToArray();
        return new VpnMetricsSnapshot(
            total, reachable, pending, unreachable, unsupported, everReachable,
            latencyTotal, latencySamples, oldestReachableAt, neverChecked, due,
            freshReachable, staleReachable, checkedLastFiveMinutes, latestCheckedAt,
            countries, facets, capturedAt);
    }
}

/// <summary>
/// Объединяет конкурентные admin-запросы и сохраняет последний успешный VPN snapshot
/// при кратком сбое PostgreSQL. Устаревший снимок отдаётся без задержки, refresh выполняет worker.
/// </summary>
public sealed class VpnMetricsSnapshotCache(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    ILogger<VpnMetricsSnapshotCache> logger,
    TimeProvider timeProvider,
    IOptions<CollectorOptions> collectorOptions,
    IMetricsSnapshotStore? snapshotStore = null) : IDisposable
{
    private static readonly Action<ILogger, DateTimeOffset, Exception?> RefreshFailed = LoggerMessage.Define<DateTimeOffset>(
        LogLevel.Warning,
        new EventId(1510, "VpnMetricsSnapshotRefreshFailed"),
        "Не удалось обновить VPN metrics snapshot; используется снимок {CapturedAt}.");
    private static readonly Action<ILogger, Exception?> RestoreFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1512, "VpnMetricsSnapshotRestoreFailed"),
        "Не удалось восстановить сохранённый VPN metrics snapshot; будет выполнен полный расчёт.");
    private static readonly Action<ILogger, Exception?> PersistenceFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1513, "VpnMetricsSnapshotPersistenceFailed"),
        "Новый VPN metrics snapshot рассчитан, но не сохранён для следующего restart.");

    // Admin lists remain minute-fresh. Public monitoring receives the same exact
    // snapshot with a five-minute passive TTL, avoiding needless full-table scans.
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan PassiveMaximumAge = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumStaleAge = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan MaximumRestorableAge = TimeSpan.FromHours(24);
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

    internal Task<VpnMetricsSnapshot> GetAsync(CancellationToken token) =>
        GetAsync(MaximumAge, token);

    internal Task<VpnMetricsSnapshot> GetPassiveAsync(CancellationToken token) =>
        GetAsync(PassiveMaximumAge, token);

    private Task<VpnMetricsSnapshot> GetAsync(TimeSpan maximumAge, CancellationToken token)
    {
        var observed = Volatile.Read(ref _current);
        var now = timeProvider.GetUtcNow();
        if (observed is null || now - observed.StoredAt >= MaximumStaleAge)
            return GetOrRefreshAsync(force: false, token);
        if (IsFresh(observed, now, maximumAge)) return Task.FromResult(observed.Snapshot);
        if (_refreshRequests.Writer.TryWrite(0)) Interlocked.Increment(ref _refreshRequestsQueued);
        else Interlocked.Increment(ref _refreshRequestsCoalesced);
        return Task.FromResult(observed.Snapshot);
    }

    internal Task<VpnMetricsSnapshot> RefreshAsync(CancellationToken token) =>
        GetOrRefreshAsync(force: true, token);
    internal Task<VpnMetricsSnapshot> WarmAsync(CancellationToken token) =>
        GetOrRefreshAsync(force: false, token);
    internal async Task<bool> RestoreAsync(CancellationToken token)
    {
        if (snapshotStore is null || Volatile.Read(ref _current) is not null) return false;
        try
        {
            var payload = await snapshotStore.LoadAsync(MetricsSnapshotStore.VpnKey, token);
            var snapshot = payload is null ? null : MetricsSnapshotStore.DeserializeVpn(payload);
            var now = timeProvider.GetUtcNow();
            if (snapshot is null || snapshot.CapturedAt == default || snapshot.CapturedAt > now ||
                now - snapshot.CapturedAt > MaximumRestorableAge)
                return false;
            Volatile.Write(ref _current, new CacheEntry(snapshot, now));
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RestoreFailed(logger, exception);
            return false;
        }
    }
    internal ValueTask<bool> WaitForRefreshRequestAsync(CancellationToken token) =>
        _refreshRequests.Reader.WaitToReadAsync(token);
    internal void DrainRefreshRequests()
    {
        while (_refreshRequests.Reader.TryRead(out _)) { }
    }

    internal long DatabaseReads => Interlocked.Read(ref _databaseReads);
    internal long RefreshRequestsQueued => Interlocked.Read(ref _refreshRequestsQueued);
    internal long RefreshRequestsCoalesced => Interlocked.Read(ref _refreshRequestsCoalesced);

    private async Task<VpnMetricsSnapshot> GetOrRefreshAsync(bool force, CancellationToken token)
    {
        var observed = Volatile.Read(ref _current);
        if (!force && IsFresh(observed, timeProvider.GetUtcNow(), MaximumAge)) return observed!.Snapshot;
        await _refreshGate.WaitAsync(token);
        try
        {
            var latest = Volatile.Read(ref _current);
            if (IsFresh(latest, timeProvider.GetUtcNow(), MaximumAge) &&
                (!force || !ReferenceEquals(latest, observed)))
                return latest!.Snapshot;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(token);
                var now = timeProvider.GetUtcNow();
                Interlocked.Increment(ref _databaseReads);
                var snapshot = await BufferedReadSnapshot.ExecuteAsync(
                    db, innerToken => VpnMetricsSnapshotReader.ReadAsync(
                        db, now, collectorOptions.Value.VpnPublicFreshnessMinutes, innerToken), token);
                Volatile.Write(ref _current, new CacheEntry(snapshot, timeProvider.GetUtcNow()));
                await PersistAsync(snapshot, token);
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

    private async Task PersistAsync(VpnMetricsSnapshot snapshot, CancellationToken token)
    {
        if (snapshotStore is null) return;
        try
        {
            await snapshotStore.SaveAsync(
                MetricsSnapshotStore.VpnKey,
                MetricsSnapshotStore.SerializeVpn(snapshot),
                snapshot.CapturedAt,
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Точный in-memory snapshot уже доступен; persistence остаётся best effort.
        }
        catch (Exception exception)
        {
            PersistenceFailed(logger, exception);
        }
    }

    private static bool IsFresh(CacheEntry? entry, DateTimeOffset now, TimeSpan maximumAge) =>
        entry is not null && now - entry.StoredAt < maximumAge;

    /// <summary>Завершает ожидающий demand-worker и освобождает gate singleton-кэша.</summary>
    public void Dispose()
    {
        _refreshRequests.Writer.TryComplete();
        _refreshGate.Dispose();
    }

    private sealed record CacheEntry(VpnMetricsSnapshot Snapshot, DateTimeOffset StoredAt);
}

internal sealed class VpnMetricsSnapshotRefreshWorker(
    VpnMetricsSnapshotCache cache,
    ILogger<VpnMetricsSnapshotRefreshWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> BackgroundRefreshFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1511, "VpnMetricsSnapshotBackgroundRefreshFailed"),
        "Фоновое обновление VPN metrics snapshot не удалось.");

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await cache.RestoreAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            cache.DrainRefreshRequests();
            await cache.RefreshAsync(stoppingToken);
            cache.DrainRefreshRequests();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            BackgroundRefreshFailed(logger, exception);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await cache.WaitForRefreshRequestAsync(stoppingToken)) break;
                cache.DrainRefreshRequests();
                await cache.RefreshAsync(stoppingToken);
                cache.DrainRefreshRequests();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                BackgroundRefreshFailed(logger, exception);
                cache.DrainRefreshRequests();
            }
        }
    }
}
