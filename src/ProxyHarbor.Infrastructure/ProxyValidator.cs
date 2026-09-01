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
    private static readonly TimeSpan AuditWriteTimeout = TimeSpan.FromSeconds(15);
    private static readonly Action<ILogger, string, Exception?> UnexpectedProbeFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1301, "UnexpectedProbeFailure"),
            "Непредусмотренная ошибка проверки прокси {ProxyKey}");
    private static readonly Action<ILogger, Guid, Exception?> LeaseRenewalFailed =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(1302, "LeaseRenewalFailed"),
            "Не удалось продлить аренду validation-пакета {LeaseId}; heartbeat повторит попытку, а утратившие ownership результаты будут отклонены PostgreSQL");
    private static readonly Action<ILogger, Guid, Exception?> LeaseReleaseFailed =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(1303, "LeaseReleaseFailed"),
            "Не удалось досрочно освободить аренду validation-пакета {LeaseId}; она истечёт автоматически");
    private static readonly Action<ILogger, Guid, Exception?> ValidationAuditFailed =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(1304, "ValidationAuditFailed"),
            "Не удалось сохранить итоговый аудит validation-партии {ValidationRunId}");
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Проверяет приоритетный пакет и возвращает число фактически сохранённых результатов.</summary>
    public async Task<(int Checked, int Alive, int Deferred)> ValidateBatchAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new OperationAlreadyRunningException("проверка прокси");
        try
        {
            var settings = options.Value;
            var startedAt = DateTimeOffset.UtcNow;
            // Health-gate выполняется до SELECT ... FOR UPDATE: при сбое control endpoint
            // очередь остаётся свободной, а рабочие прокси не получают ложный Dead.
            await probe.EnsureControlEndpointAvailableAsync(cancellationToken);
            await using var databaseLease = await DatabaseRuntimeGate.TryAcquireOperationLeaseAsync(
                dbFactory, cancellationToken)
                ?? throw new OperationAlreadyRunningException("восстановление базы данных");
            var now = DateTimeOffset.UtcNow;
            var concurrency = Math.Clamp(settings.ValidationConcurrency, 1, 1000);
            var batchSize = Math.Clamp(settings.ValidationBatchSize, 1, 100_000);
            // Статическая аренда на worst-case длительность всего пакета при допустимых
            // настройках могла достигать 139 дней. Короткая heartbeat-аренда ограничивает
            // восстановление после аварии несколькими минутами независимо от batch size.
            var leaseDuration = ValidationLeasePolicy.Duration(settings.ProbeTimeoutSeconds);
            var leaseUntil = now.Add(leaseDuration);
            var leaseId = Guid.NewGuid();

            var proxies = await ClaimBatchAsync(batchSize, now, leaseUntil, leaseId, cancellationToken);
            if (proxies.Count == 0) return (0, 0, 0);

            var validationRunId = Guid.NewGuid();
            var auditStarted = false;
            try
            {
                await StartRunAuditAsync(
                    validationRunId, leaseId, startedAt, proxies.Count, leaseDuration, cancellationToken);
                auditStarted = true;
                using var heartbeatStop = new CancellationTokenSource();
                var heartbeat = MaintainLeaseAsync(leaseId, leaseDuration, heartbeatStop.Token);
                try
                {
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
                            OperationalLogBoundary.Write(() =>
                                UnexpectedProbeFailure(logger, proxy.Key, exception));
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
                    var persisted = await PersistResultsAsync(updates, cancellationToken);
                    await CompleteRunAuditAsync(validationRunId, persisted);
                    return persisted;
                }
                finally
                {
                    await heartbeatStop.CancelAsync();
                    await heartbeat;
                }
            }
            catch (Exception exception)
            {
                await ReleaseLeaseBestEffortAsync(leaseId);
                if (auditStarted) await FailRunAuditAsync(validationRunId, exception);
                throw;
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task StartRunAuditAsync(
        Guid id,
        Guid leaseId,
        DateTimeOffset startedAt,
        int claimed,
        TimeSpan leaseDuration,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.Subtract(leaseDuration);
        // Running-аудит восстанавливается только после истечения связанной proxy lease:
        // активную партию другой реплики этот запрос никогда не пометит failed.
        await db.ValidationRuns.Where(run => run.Status == "running" && run.StartedAt < staleBefore &&
                !db.Proxies.Any(proxy => proxy.CheckLeaseId == run.LeaseId && proxy.CheckLeaseUntil >= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.FinishedAt, now)
                .SetProperty(run => run.Status, "failed")
                .SetProperty(run => run.Error,
                    "Validation-партия была прервана аварийным завершением предыдущего процесса."), token);
        db.ValidationRuns.Add(new ValidationRun
        {
            Id = id,
            LeaseId = leaseId,
            StartedAt = startedAt,
            Claimed = claimed
        });
        await db.SaveChangesAsync(token);
    }

    private async Task CompleteRunAuditAsync(
        Guid id,
        (int Checked, int Alive, int Deferred) result)
    {
        using var timeout = new CancellationTokenSource(AuditWriteTimeout);
        await using var db = await dbFactory.CreateDbContextAsync(timeout.Token);
        var updated = await db.ValidationRuns.Where(run => run.Id == id && run.Status == "running")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(run => run.Checked, result.Checked)
                .SetProperty(run => run.Alive, result.Alive)
                .SetProperty(run => run.Deferred, result.Deferred)
                .SetProperty(run => run.Status, "completed"), timeout.Token);
        if (updated != 1)
            throw new InvalidOperationException("Validation-аудит потерял ownership своей running-строки.");
    }

    private async Task FailRunAuditAsync(Guid id, Exception exception)
    {
        try
        {
            using var timeout = new CancellationTokenSource(AuditWriteTimeout);
            await using var db = await dbFactory.CreateDbContextAsync(timeout.Token);
            var error = exception.ToString();
            await db.ValidationRuns.Where(run => run.Id == id && run.Status == "running")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.FinishedAt, DateTimeOffset.UtcNow)
                    .SetProperty(run => run.Status, "failed")
                    .SetProperty(run => run.Error, error[..Math.Min(2000, error.Length)]), timeout.Token);
        }
        catch (Exception auditException)
        {
            // После истечения proxy lease следующая реплика восстановит running-аудит.
            OperationalLogBoundary.Write(() => ValidationAuditFailed(logger, id, auditException));
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
            proxies.AddRange(await ValidationQueueClaim.ClaimAsync(
                claimDb, batchSize, now, token));
            var ids = proxies.Select(x => x.Id).ToArray();
            if (ids.Length > 0)
                await claimDb.Proxies.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CheckLeaseUntil, leaseUntil)
                    .SetProperty(x => x.CheckLeaseId, leaseId), token);
            await transaction.CommitAsync(token);
        });
        return proxies;
    }

    /// <summary>Продлевает только ещё принадлежащие этому экземпляру строки validation-пакета.</summary>
    internal async Task<int> RenewLeaseAsync(Guid leaseId, DateTimeOffset leaseUntil, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH locked AS MATERIALIZED (
                SELECT proxy."Id"
                FROM "Proxies" AS proxy
                WHERE proxy."CheckLeaseId" = {leaseId}
                ORDER BY proxy."Id"
                FOR UPDATE OF proxy
            )
            UPDATE "Proxies" AS proxy
            SET "CheckLeaseUntil" = {leaseUntil}
            FROM locked
            WHERE proxy."Id" = locked."Id"
            """, token);
    }

    /// <summary>Освобождает пакет по точному lease token, не затрагивая аренду другой реплики.</summary>
    internal async Task<int> ReleaseLeaseAsync(Guid leaseId, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH locked AS MATERIALIZED (
                SELECT proxy."Id"
                FROM "Proxies" AS proxy
                WHERE proxy."CheckLeaseId" = {leaseId}
                ORDER BY proxy."Id"
                FOR UPDATE OF proxy
            )
            UPDATE "Proxies" AS proxy
            SET "CheckLeaseUntil" = NULL,
                "CheckLeaseId" = NULL
            FROM locked
            WHERE proxy."Id" = locked."Id"
            """, token);
    }

    private Task MaintainLeaseAsync(Guid leaseId, TimeSpan duration, CancellationToken token) =>
        MaintainLeaseWithRetryAsync(
            duration,
            ValidationLeasePolicy.RenewalInterval(duration),
            (leaseUntil, cancellationToken) =>
                RenewLeaseAsync(leaseId, leaseUntil, cancellationToken),
            exception => LeaseRenewalFailed(logger, leaseId, exception),
            token);

    /// <summary>
    /// Изолированное heartbeat-ядро принимает clock interval и renewal delegate, чтобы
    /// transient-retry проверялся детерминированно без реальной БД и минутного ожидания.
    /// </summary>
    internal static async Task MaintainLeaseWithRetryAsync(
        TimeSpan duration,
        TimeSpan renewalInterval,
        Func<DateTimeOffset, CancellationToken, Task<int>> renewAsync,
        Action<Exception> renewalFailed,
        CancellationToken token)
    {
        using var timer = new PeriodicTimer(renewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    await renewAsync(DateTimeOffset.UtcNow.Add(duration), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // Один transient-сбой не отключает heartbeat навсегда: следующая
                    // периодическая попытка ещё может сохранить ownership до expiry.
                    OperationalLogBoundary.Write(() => renewalFailed(exception));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ReleaseLeaseBestEffortAsync(Guid leaseId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ReleaseLeaseAsync(leaseId, timeout.Token);
        }
        catch (Exception exception)
        {
            // Исходная ошибка или отмена важнее вторичного cleanup-сбоя. Короткая
            // bounded-аренда остаётся последним механизмом автоматического восстановления.
            OperationalLogBoundary.Write(() => LeaseReleaseFailed(logger, leaseId, exception));
        }
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
                WITH locked AS MATERIALIZED (
                    SELECT proxy."Id"
                    FROM "Proxies" AS proxy
                    JOIN proxy_check_update AS incoming
                      ON proxy."Id" = incoming.id AND proxy."CheckLeaseId" = incoming.lease_id
                    ORDER BY proxy."Id"
                    FOR UPDATE OF proxy
                )
                UPDATE "Proxies" AS proxy SET
                    "LastCheckedAt" = CASE WHEN incoming.outcome = 2 THEN proxy."LastCheckedAt" ELSE incoming.checked_at END,
                    "LastValidationAttemptAt" = incoming.checked_at,
                    "LastValidationDeferred" = incoming.outcome = 2,
                    "NextCheckAt" = incoming.next_check_at,
                    "CheckLeaseUntil" = NULL,
                    "CheckLeaseId" = NULL,
                    "FirstAliveAt" = CASE
                        WHEN incoming.outcome = 1 THEN COALESCE(proxy."FirstAliveAt", GREATEST(proxy."FirstSeenAt", incoming.checked_at))
                        ELSE proxy."FirstAliveAt"
                    END,
                    "LastAliveAt" = CASE
                        WHEN incoming.outcome = 1 THEN GREATEST(
                            COALESCE(proxy."LastAliveAt", proxy."FirstSeenAt"),
                            proxy."FirstSeenAt",
                            incoming.checked_at)
                        ELSE proxy."LastAliveAt"
                    END,
                    "CurrentAliveSince" = CASE
                        WHEN incoming.outcome = 1 THEN CASE
                            WHEN proxy."Status" = 1 AND proxy."CurrentAliveSince" IS NOT NULL THEN proxy."CurrentAliveSince"
                            ELSE GREATEST(proxy."FirstSeenAt", incoming.checked_at)
                        END
                        WHEN incoming.outcome = 0 THEN NULL
                        ELSE proxy."CurrentAliveSince"
                    END,
                    "Status" = CASE incoming.outcome WHEN 1 THEN 1 WHEN 0 THEN 2 ELSE proxy."Status" END,
                    "LatencyMs" = CASE WHEN incoming.outcome = 2 THEN proxy."LatencyMs" ELSE incoming.latency_ms END,
                    "ExitIp" = CASE WHEN incoming.outcome = 2 THEN proxy."ExitIp" ELSE incoming.exit_ip END,
                    "IsAnonymous" = CASE WHEN incoming.outcome = 2 THEN proxy."IsAnonymous" ELSE incoming.is_anonymous END,
                    "LastError" = incoming.error,
                    "SuccessfulChecks" = proxy."SuccessfulChecks" + CASE WHEN incoming.outcome = 1 THEN 1 ELSE 0 END,
                    "FailedChecks" = proxy."FailedChecks" + CASE WHEN incoming.outcome = 0 THEN 1 ELSE 0 END,
                    "ConsecutiveFailedChecks" = incoming.failure_streak
                FROM proxy_check_update AS incoming, locked
                WHERE proxy."Id" = locked."Id"
                  AND incoming.id = locked."Id"
                  AND proxy."CheckLeaseId" = incoming.lease_id
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
            var persistedCount = checkedCount + deferredCount;
            // Сохраняем объективные результаты ещё принадлежащих строк, но не выдаём
            // частичную persistence за completed batch: вызывающий catch запишет failed audit.
            if (persistedCount != updates.Length)
                throw new InvalidOperationException(
                    $"Validation-партия потеряла ownership lease: сохранено {persistedCount} из {updates.Length} результатов.");

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

/// <summary>Ограничивает crash-recovery аренды независимо от размера validation-пакета.</summary>
internal static class ValidationLeasePolicy
{
    internal static TimeSpan Duration(int probeTimeoutSeconds)
    {
        var timeout = Math.Clamp(probeTimeoutSeconds, 1, 120);
        return TimeSpan.FromSeconds(Math.Max(120, checked(timeout * 2 + 60)));
    }

    internal static TimeSpan RenewalInterval(TimeSpan duration) => TimeSpan.FromTicks(duration.Ticks / 3);
}
