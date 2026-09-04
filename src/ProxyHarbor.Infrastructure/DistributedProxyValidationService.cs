using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Центральный диспетчер внешних checker-узлов. PostgreSQL является единственным
/// источником истины: короткая аренда автоматически возвращает пакет после потери VPS.
/// </summary>
public sealed class DistributedProxyValidationService(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<CollectorOptions> options,
    ValidationClaimIdleGate idleGate)
{
    /// <summary>Атомарно арендует очередной непересекающийся пакет для включённого узла.</summary>
    public async Task<CheckerLeaseResponse?> ClaimAsync(Guid nodeId, CancellationToken token)
    {
        var idleDecision = idleGate.TryCoalesce(nodeId);
        if (idleDecision.Coalesced)
        {
            if (idleDecision.PersistHeartbeat)
                await PersistIdleHeartbeatAsync(nodeId, token);
            return null;
        }

        var settings = options.Value;
        var now = DateTimeOffset.UtcNow;
        var leaseDuration = ValidationLeasePolicy.Duration(settings.ProbeTimeoutSeconds);
        var leaseUntil = now.Add(leaseDuration);
        var leaseId = Guid.NewGuid();
        var claimed = new List<ValidationClaimCandidate>();
        CheckerNode? nodeSnapshot = null;

        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            claimed.Clear();
            await using var db = await dbFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var node = await db.CheckerNodes.FromSqlInterpolated($"""
                SELECT * FROM "CheckerNodes" WHERE "Id" = {nodeId} FOR UPDATE
                """).SingleOrDefaultAsync(token);
            if (node is null || !node.Enabled)
            {
                await transaction.RollbackAsync(token);
                return;
            }

            if (node.CurrentLeaseId.HasValue && node.CurrentLeaseUntil >= now &&
                await db.ProxyValidationLeases.AsNoTracking().AnyAsync(
                    lease => lease.LeaseId == node.CurrentLeaseId, token))
            {
                node.LastHeartbeatAt = now;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                nodeSnapshot = node;
                return;
            }

            var expiredNodeLeaseId = node.CurrentLeaseId;
            node.CurrentLeaseId = null;
            node.CurrentLeaseUntil = null;
            var batchSize = Math.Clamp(node.BatchSize, 1, 10_000);
            claimed.AddRange(await ValidationQueueClaim.ClaimAndLeaseAsync(
                db, batchSize, now, leaseUntil, leaseId, idleGate, token));

            var expiredLeaseIds = claimed
                .Where(x => x.PreviousLeaseId.HasValue)
                .Select(x => x.PreviousLeaseId)
                .Append(expiredNodeLeaseId)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .ToArray();
            if (expiredLeaseIds.Length > 0)
            {
                // И claim, и completion блокируют узкие lease-строки раньше audit rows.
                // Стабильный порядок исключает взаимную блокировку разных партий.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    WITH locked AS MATERIALIZED (
                        SELECT "Id"
                        FROM "ValidationRuns"
                        WHERE "LeaseId" = ANY ({expiredLeaseIds}) AND "Status" = 'running'
                        ORDER BY "Id"
                        FOR UPDATE
                    )
                    UPDATE "ValidationRuns" AS run
                    SET "FinishedAt" = {now},
                        "Status" = 'failed',
                        "Error" = 'Checker-узел не завершил партию до истечения аренды; пакет возвращён в очередь.'
                    FROM locked
                    WHERE run."Id" = locked."Id"
                    """, token);
            }

            if (claimed.Count > 0)
            {
                db.ValidationRuns.Add(new ValidationRun
                {
                    Id = Guid.NewGuid(),
                    LeaseId = leaseId,
                    CheckerNodeId = node.Id,
                    StartedAt = now,
                    Claimed = claimed.Count
                });
                node.CurrentLeaseId = leaseId;
                node.CurrentLeaseUntil = leaseUntil;
                node.LastLeaseAt = now;
            }
            node.LastHeartbeatAt = now;
            node.LastError = null;
            nodeSnapshot = node;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });

        if (nodeSnapshot is not null)
            idleGate.MarkHeartbeat(nodeId);
        if (nodeSnapshot is null || claimed.Count == 0) return null;
        return new CheckerLeaseResponse(
            leaseId, leaseUntil, Math.Clamp(nodeSnapshot.Concurrency, 1, 1_000),
            settings.ProbeTimeoutSeconds, settings.ProbeHost, settings.ProbePort, settings.ProbePath,
            claimed.Select(x => new CheckerProxyItem(x.Id, x.Host, x.Port, x.Protocol)).ToArray());
    }

    /// <summary>Продлевает только аренду, которой по-прежнему владеет этот узел.</summary>
    public async Task<bool> RenewAsync(Guid nodeId, Guid leaseId, CheckerHeartbeatRequest heartbeat, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var until = now.Add(ValidationLeasePolicy.Duration(options.Value.ProbeTimeoutSeconds));
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        var renewed = false;
        await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var node = await db.CheckerNodes.FromSqlInterpolated($"""
                SELECT * FROM "CheckerNodes" WHERE "Id" = {nodeId} FOR UPDATE
                """).SingleOrDefaultAsync(token);
            if (node is null || !node.Enabled || node.CurrentLeaseId != leaseId ||
                node.CurrentLeaseUntil is null || node.CurrentLeaseUntil < now)
            {
                await transaction.RollbackAsync(token);
                return;
            }

            var leasesUpdated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH locked AS MATERIALIZED (
                    SELECT "ProxyId"
                    FROM "ProxyValidationLeases"
                    WHERE "LeaseId" = {leaseId} AND "LeaseUntil" >= {now}
                    ORDER BY "ProxyId"
                    FOR UPDATE
                )
                UPDATE "ProxyValidationLeases" AS lease
                SET "LeaseUntil" = {until}
                FROM locked
                WHERE lease."ProxyId" = locked."ProxyId"
                  AND lease."LeaseId" = {leaseId}
                """, token);
            if (leasesUpdated == 0)
            {
                await transaction.RollbackAsync(token);
                return;
            }

            node.LastHeartbeatAt = now;
            node.CurrentLeaseUntil = until;
            node.AgentVersion = Bounded(heartbeat.Version, 80);
            node.LastError = Bounded(heartbeat.Error, 1000);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            renewed = true;
        });
        return renewed;
    }

    /// <summary>Проверяет полноту и ownership партии, затем атомарно применяет её результаты.</summary>
    public async Task<CheckerLeaseCompletion> CompleteAsync(
        Guid nodeId, Guid leaseId, CheckerLeaseResultRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => CompleteCoreAsync(nodeId, leaseId, request, token));
    }

    private async Task<CheckerLeaseCompletion> CompleteCoreAsync(
        Guid nodeId, Guid leaseId, CheckerLeaseResultRequest request, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // ClaimAsync блокирует ту же строку. Пока результат проверяется и сохраняется,
        // просрочившийся на границе TTL пакет нельзя одновременно отдать другой ноде.
        var node = await db.CheckerNodes.FromSqlInterpolated($"""
            SELECT * FROM "CheckerNodes" WHERE "Id" = {nodeId} FOR UPDATE
            """).SingleOrDefaultAsync(token);
        if (node is null || !node.Enabled)
            throw new InvalidOperationException("Checker-узел отключён или удалён.");
        if (node.CurrentLeaseId != leaseId || node.CurrentLeaseUntil is null || node.CurrentLeaseUntil < now)
        {
            // A commit can succeed while its HTTP/database acknowledgement is lost.
            // LeaseId is an immutable completion key: acknowledge the original
            // persisted result, never merge a replay or clear a newer node lease.
            // The unique lease index is queried only on this recovery path; normal
            // completions keep the same number of database round trips.
            var completed = await db.ValidationRuns.AsNoTracking()
                .Where(run => run.LeaseId == leaseId && run.CheckerNodeId == nodeId && run.Status == "completed")
                .Select(run => new CheckerLeaseCompletion(run.Checked, run.Alive, run.Deferred))
                .SingleOrDefaultAsync(token);
            if (completed is not null)
            {
                await transaction.CommitAsync(token);
                return completed;
            }
            throw new InvalidOperationException("Аренда истекла или больше не принадлежит checker-узлу.");
        }

        var leasedIds = await (
                from lease in db.ProxyValidationLeases.AsNoTracking()
                join proxy in db.Proxies.AsNoTracking() on lease.ProxyId equals proxy.Id
                where lease.LeaseId == leaseId && lease.LeaseUntil >= now
                select new { proxy.Id, proxy.ConsecutiveFailedChecks })
            .ToListAsync(token);
        var distinctResults = request.Results.GroupBy(x => x.ProxyId).ToArray();
        if (leasedIds.Count == 0 || distinctResults.Length != leasedIds.Count ||
            distinctResults.Any(group => group.Count() != 1) ||
            !leasedIds.Select(x => x.Id).Order().SequenceEqual(distinctResults.Select(x => x.Key).Order()))
            throw new InvalidOperationException("Результат должен содержать ровно один ответ для каждого прокси аренды.");

        var streaks = leasedIds.ToDictionary(x => x.Id, x => x.ConsecutiveFailedChecks);
        var updates = request.Results.Select(result => ProxyCheckScheduler.Create(
            new ProxyCheckResult(result.ProxyId, result.IsAlive, result.LatencyMs, result.ExitIp,
                result.IsAnonymous, result.Error, result.IsDeferred),
            streaks[result.ProxyId], leaseId, now, options.Value)).ToArray();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        var persisted = await ProxyValidator.PersistResultsInTransactionAsync(
            updates, connection, npgsqlTransaction, token);

        var runUpdated = await db.ValidationRuns
            .Where(x => x.LeaseId == leaseId && x.CheckerNodeId == nodeId && x.Status == "running")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.Checked, persisted.Checked)
                .SetProperty(x => x.Alive, persisted.Alive)
                .SetProperty(x => x.Deferred, persisted.Deferred)
                .SetProperty(x => x.Status, "completed"), token);
        var nodeUpdated = await db.CheckerNodes
            .Where(x => x.Id == nodeId && x.CurrentLeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CurrentLeaseId, (Guid?)null)
                .SetProperty(x => x.CurrentLeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.LastHeartbeatAt, now)
                .SetProperty(x => x.LastCompletedAt, now)
                .SetProperty(x => x.LastError, (string?)null)
                // Deferred означает корректно обработанную, но не засчитанную как
                // сетевой успех/ошибка попытку; для загрузки узла это всё равно
                // завершённая работа и она должна попадать в общий счётчик.
                .SetProperty(x => x.CompletedChecks, x => x.CompletedChecks + persisted.Checked + persisted.Deferred)
                .SetProperty(x => x.AliveChecks, x => x.AliveChecks + persisted.Alive), token);
        if (runUpdated != 1 || nodeUpdated != 1)
            throw new InvalidOperationException("Checker-узел потерял ownership при завершении партии.");
        await transaction.CommitAsync(token);
        return new CheckerLeaseCompletion(persisted.Checked, persisted.Alive, persisted.Deferred);
    }

    /// <summary>Обновляет наблюдаемое состояние простаивающего агента.</summary>
    public async Task TouchAsync(Guid nodeId, CheckerHeartbeatRequest heartbeat, string? remoteAddress, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await db.CheckerNodes.Where(x => x.Id == nodeId && x.Enabled).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.LastHeartbeatAt, now)
            .SetProperty(x => x.AgentVersion, Bounded(heartbeat.Version, 80))
            .SetProperty(x => x.RemoteAddress, Bounded(remoteAddress, 64))
            .SetProperty(x => x.DeploymentStatus, "online")
            .SetProperty(x => x.LastError, Bounded(heartbeat.Error, 1000)), token);
        idleGate.MarkHeartbeat(nodeId);
    }

    private async Task PersistIdleHeartbeatAsync(Guid nodeId, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await db.CheckerNodes.Where(x => x.Id == nodeId && x.Enabled).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.LastHeartbeatAt, now)
            .SetProperty(x => x.LastError, (string?)null), token);
    }

    private static string? Bounded(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(maximum, value.Trim().Length)];
}
