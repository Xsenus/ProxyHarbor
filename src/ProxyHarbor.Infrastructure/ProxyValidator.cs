using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Арендует готовые к проверке записи, проверяет параллельно и сохраняет результат одним SQL-пакетом.</summary>
public sealed class ProxyValidator(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    ProxyProbeService probe,
    IOptions<CollectorOptions> options,
    ILogger<ProxyValidator> logger) : IDisposable
{
    private static readonly Action<ILogger, string, Exception?> UnexpectedProbeFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1301, "UnexpectedProbeFailure"),
            "Непредусмотренная ошибка проверки прокси {ProxyKey}");
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Проверяет приоритетный пакет и возвращает число фактически сохранённых результатов.</summary>
    public async Task<(int Checked, int Alive, int Deferred)> ValidateBatchAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new OperationAlreadyRunningException("проверка прокси");
        try
        {
            var settings = options.Value;
            // Health-gate выполняется до SELECT ... FOR UPDATE: при сбое control endpoint
            // очередь остаётся свободной, а рабочие прокси не получают ложный Dead.
            await probe.EnsureControlEndpointAvailableAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var concurrency = Math.Clamp(settings.ValidationConcurrency, 1, 1000);
            var batchSize = Math.Clamp(settings.ValidationBatchSize, 1, 100_000);
            var waves = (int)Math.Ceiling((double)batchSize / concurrency);
            var leaseDuration = TimeSpan.FromSeconds(Math.Max(120, waves * settings.ProbeTimeoutSeconds + 60));
            var leaseUntil = now.Add(leaseDuration);
            var leaseId = Guid.NewGuid();

            var proxies = await ClaimBatchAsync(batchSize, now, leaseUntil, leaseId, cancellationToken);
            if (proxies.Count == 0) return (0, 0, 0);

            var results = new System.Collections.Concurrent.ConcurrentBag<ProxyCheckResult>();
            await Parallel.ForEachAsync(proxies, new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            }, async (proxy, token) =>
            {
                try { results.Add(await probe.CheckAsync(proxy, token)); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (ProbeControlUnavailableException exception)
                {
                    results.Add(new ProxyCheckResult(proxy.Id, false, null, null, false,
                        exception.Message, IsDeferred: true));
                }
                catch (Exception exception)
                {
                    // Неизвестная ошибка реализации не доказывает неисправность внешнего прокси.
                    UnexpectedProbeFailure(logger, proxy.Key, exception);
                    results.Add(new ProxyCheckResult(proxy.Id, false, null, null, false,
                        "internal probe error", IsDeferred: true));
                }
            });

            now = DateTimeOffset.UtcNow;
            var proxiesById = proxies.ToDictionary(x => x.Id);
            var updates = results.Select(result => ProxyCheckScheduler.Create(
                result,
                proxiesById[result.ProxyId].ConsecutiveFailedChecks,
                leaseId,
                now,
                settings)).ToArray();
            return await PersistResultsAsync(updates, cancellationToken);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<List<ProxyEndpoint>> ClaimBatchAsync(
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        Guid leaseId,
        CancellationToken token)
    {
        var proxies = new List<ProxyEndpoint>();
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            proxies.Clear();
            await using var claimDb = await dbFactory.CreateDbContextAsync(token);
            await using var transaction = await claimDb.Database.BeginTransactionAsync(token);
            proxies.AddRange(await claimDb.Proxies.FromSqlInterpolated($"""
                SELECT * FROM "Proxies"
                WHERE ("NextCheckAt" IS NULL OR "NextCheckAt" <= {now})
                  AND ("CheckLeaseUntil" IS NULL OR "CheckLeaseUntil" < {now})
                ORDER BY
                    CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END,
                    "NextCheckAt" NULLS FIRST,
                    "LastCheckedAt" NULLS FIRST
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """).AsNoTracking().ToListAsync(token));
            var ids = proxies.Select(x => x.Id).ToArray();
            if (ids.Length > 0)
                await claimDb.Proxies.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CheckLeaseUntil, leaseUntil)
                    .SetProperty(x => x.CheckLeaseId, leaseId), token);
            await transaction.CommitAsync(token);
        });
        return proxies;
    }

    internal async Task<(int Checked, int Alive, int Deferred)> PersistResultsAsync(
        ScheduledProxyCheck[] updates,
        CancellationToken token)
    {
        if (updates.Length == 0) return (0, 0, 0);
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using (var create = new NpgsqlCommand("""
                CREATE TEMP TABLE proxy_check_update (
                    id uuid NOT NULL, lease_id uuid NOT NULL, outcome integer NOT NULL,
                    latency_ms integer NULL, exit_ip text NULL, is_anonymous boolean NOT NULL,
                    error text NULL, checked_at timestamptz NOT NULL, next_check_at timestamptz NOT NULL,
                    failure_streak integer NOT NULL
                ) ON COMMIT DROP
                """, connection, transaction))
                await create.ExecuteNonQueryAsync(token);

            await using (var writer = await connection.BeginBinaryImportAsync("""
                COPY proxy_check_update
                    (id, lease_id, outcome, latency_ms, exit_ip, is_anonymous, error, checked_at, next_check_at, failure_streak)
                FROM STDIN (FORMAT BINARY)
                """, token))
            {
                foreach (var update in updates)
                {
                    await writer.StartRowAsync(token);
                    await writer.WriteAsync(update.ProxyId, NpgsqlDbType.Uuid, token);
                    await writer.WriteAsync(update.LeaseId, NpgsqlDbType.Uuid, token);
                    await writer.WriteAsync((int)update.Outcome, NpgsqlDbType.Integer, token);
                    if (update.LatencyMs.HasValue) await writer.WriteAsync(update.LatencyMs.Value, NpgsqlDbType.Integer, token); else await writer.WriteNullAsync(token);
                    if (update.ExitIp is not null) await writer.WriteAsync(update.ExitIp, NpgsqlDbType.Text, token); else await writer.WriteNullAsync(token);
                    await writer.WriteAsync(update.IsAnonymous, NpgsqlDbType.Boolean, token);
                    if (update.Error is not null) await writer.WriteAsync(update.Error, NpgsqlDbType.Text, token); else await writer.WriteNullAsync(token);
                    await writer.WriteAsync(update.CheckedAt, NpgsqlDbType.TimestampTz, token);
                    await writer.WriteAsync(update.NextCheckAt, NpgsqlDbType.TimestampTz, token);
                    await writer.WriteAsync(update.FailureStreak, NpgsqlDbType.Integer, token);
                }
                await writer.CompleteAsync(token);
            }

            await using var merge = new NpgsqlCommand("""
                UPDATE "Proxies" AS proxy SET
                    "LastCheckedAt" = CASE WHEN incoming.outcome = 2 THEN proxy."LastCheckedAt" ELSE incoming.checked_at END,
                    "NextCheckAt" = incoming.next_check_at,
                    "CheckLeaseUntil" = NULL,
                    "CheckLeaseId" = NULL,
                    "Status" = CASE incoming.outcome WHEN 1 THEN 1 WHEN 0 THEN 2 ELSE proxy."Status" END,
                    "LatencyMs" = CASE WHEN incoming.outcome = 2 THEN proxy."LatencyMs" ELSE incoming.latency_ms END,
                    "ExitIp" = CASE WHEN incoming.outcome = 2 THEN proxy."ExitIp" ELSE incoming.exit_ip END,
                    "IsAnonymous" = CASE WHEN incoming.outcome = 2 THEN proxy."IsAnonymous" ELSE incoming.is_anonymous END,
                    "LastError" = incoming.error,
                    "SuccessfulChecks" = proxy."SuccessfulChecks" + CASE WHEN incoming.outcome = 1 THEN 1 ELSE 0 END,
                    "FailedChecks" = proxy."FailedChecks" + CASE WHEN incoming.outcome = 0 THEN 1 ELSE 0 END,
                    "ConsecutiveFailedChecks" = incoming.failure_streak
                FROM proxy_check_update AS incoming
                WHERE proxy."Id" = incoming.id AND proxy."CheckLeaseId" = incoming.lease_id
                RETURNING incoming.outcome
                """, connection, transaction);
            var checkedCount = 0;
            var aliveCount = 0;
            var deferredCount = 0;
            await using (var reader = await merge.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var outcome = (ProxyCheckOutcome)reader.GetInt32(0);
                    if (outcome == ProxyCheckOutcome.Deferred) deferredCount++;
                    else checkedCount++;
                    if (outcome == ProxyCheckOutcome.Alive) aliveCount++;
                }
            }
            await transaction.CommitAsync(token);
            return (checkedCount, aliveCount, deferredCount);
        });
    }

    /// <summary>Освобождает синхронизатор конкурентных ручных и фоновых запусков.</summary>
    public void Dispose() => _runGate.Dispose();
}

/// <summary>Чистая функция адаптивного расписания, удобная для строгих unit-тестов.</summary>
internal static class ProxyCheckScheduler
{
    internal static ScheduledProxyCheck Create(
        ProxyCheckResult result,
        int previousFailureStreak,
        Guid leaseId,
        DateTimeOffset now,
        CollectorOptions options)
    {
        var outcome = result.IsDeferred
            ? ProxyCheckOutcome.Deferred
            : result.IsAlive ? ProxyCheckOutcome.Alive : ProxyCheckOutcome.Dead;
        var failureStreak = outcome switch
        {
            ProxyCheckOutcome.Alive => 0,
            ProxyCheckOutcome.Dead => checked(previousFailureStreak + 1),
            _ => previousFailureStreak
        };
        TimeSpan delay;
        if (outcome == ProxyCheckOutcome.Deferred)
        {
            delay = TimeSpan.FromMinutes(1);
        }
        else if (outcome == ProxyCheckOutcome.Alive)
        {
            delay = TimeSpan.FromMinutes(options.ValidationIntervalMinutes);
        }
        else
        {
            var exponent = Math.Min(Math.Max(0, failureStreak - 1), 20);
            var minutes = Math.Min(
                (long)options.DeadRetryMaxHours * 60,
                (long)options.DeadRetryBaseMinutes * (1L << exponent));
            delay = TimeSpan.FromMinutes(minutes);
        }

        return new ScheduledProxyCheck(
            result.ProxyId,
            leaseId,
            outcome,
            result.LatencyMs,
            result.ExitIp?[..Math.Min(64, result.ExitIp.Length)],
            result.IsAnonymous,
            result.Error?[..Math.Min(500, result.Error.Length)],
            now,
            now.Add(delay),
            failureStreak);
    }
}

/// <summary>Нормализованный результат, готовый для PostgreSQL binary COPY.</summary>
internal sealed record ScheduledProxyCheck(
    Guid ProxyId,
    Guid LeaseId,
    ProxyCheckOutcome Outcome,
    int? LatencyMs,
    string? ExitIp,
    bool IsAnonymous,
    string? Error,
    DateTimeOffset CheckedAt,
    DateTimeOffset NextCheckAt,
    int FailureStreak);

internal enum ProxyCheckOutcome { Dead, Alive, Deferred }
