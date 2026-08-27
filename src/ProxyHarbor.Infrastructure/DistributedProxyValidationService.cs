using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Центральный диспетчер внешних checker-узлов. PostgreSQL является единственным
/// источником истины: короткая аренда автоматически возвращает пакет после потери VPS.
/// </summary>
public sealed class DistributedProxyValidationService(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    ProxyValidator validator,
    IOptions<CollectorOptions> options)
{
    /// <summary>Атомарно арендует очередной непересекающийся пакет для включённого узла.</summary>
    public async Task<CheckerLeaseResponse?> ClaimAsync(Guid nodeId, CancellationToken token)
    {
        var settings = options.Value;
        var now = DateTimeOffset.UtcNow;
        var leaseDuration = ValidationLeasePolicy.Duration(settings.ProbeTimeoutSeconds);
        var leaseUntil = now.Add(leaseDuration);
        var leaseId = Guid.NewGuid();
        var claimed = new List<ProxyEndpoint>();
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

            if (node.CurrentLeaseId.HasValue && node.CurrentLeaseUntil >= now)
            {
                node.LastHeartbeatAt = now;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                nodeSnapshot = node;
                return;
            }

            if (node.CurrentLeaseId is { } expiredLeaseId)
            {
                // Истёкшая аренда не считается завершённой: сохраняем в аудите причину,
                // а сами proxy-строки автоматически доступны следующему узлу по lease TTL.
                await db.ValidationRuns
                    .Where(x => x.LeaseId == expiredLeaseId && x.CheckerNodeId == node.Id && x.Status == "running")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.FinishedAt, now)
                        .SetProperty(x => x.Status, "failed")
                        .SetProperty(x => x.Error,
                            "Checker-узел не завершил партию до истечения аренды; пакет возвращён в очередь."), token);
            }
            node.CurrentLeaseId = null;
            node.CurrentLeaseUntil = null;
            var batchSize = Math.Clamp(node.BatchSize, 1, 10_000);
            claimed.AddRange(await db.Proxies.FromSqlInterpolated($"""
                SELECT * FROM "Proxies"
                WHERE ("NextCheckAt" IS NULL OR "NextCheckAt" <= {now})
                  AND ("CheckLeaseUntil" IS NULL OR "CheckLeaseUntil" < {now})
                ORDER BY CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END,
                         "NextCheckAt" NULLS FIRST, "LastCheckedAt" NULLS FIRST
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """).AsNoTracking().ToListAsync(token));

            if (claimed.Count > 0)
            {
                var ids = claimed.Select(x => x.Id).ToArray();
                var reclaimedLeaseIds = claimed
                    .Where(x => x.CheckLeaseId.HasValue)
                    .Select(x => x.CheckLeaseId!.Value)
                    .Distinct()
                    .ToArray();
                if (reclaimedLeaseIds.Length > 0)
                {
                    // Аудит закрывается именно в момент фактического повторного назначения.
                    // Так краткий разрыв heartbeat не создаёт ложный failed, а отобранная
                    // новым узлом просроченная партия никогда не остаётся вечным running.
                    await db.ValidationRuns
                        .Where(x => reclaimedLeaseIds.Contains(x.LeaseId) && x.Status == "running")
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.FinishedAt, now)
                            .SetProperty(x => x.Status, "failed")
                            .SetProperty(x => x.Error,
                                "Checker-узел не завершил партию до истечения аренды; пакет передан другому узлу."), token);
                }
                await db.Proxies.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CheckLeaseUntil, leaseUntil)
                    .SetProperty(x => x.CheckLeaseId, leaseId), token);
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
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var nodeUpdated = await db.CheckerNodes
            .Where(x => x.Id == nodeId && x.Enabled && x.CurrentLeaseId == leaseId && x.CurrentLeaseUntil >= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastHeartbeatAt, now)
                .SetProperty(x => x.CurrentLeaseUntil, until)
                .SetProperty(x => x.AgentVersion, Bounded(heartbeat.Version, 80))
                .SetProperty(x => x.LastError, Bounded(heartbeat.Error, 1000)), token);
        if (nodeUpdated != 1) return false;
        var proxiesUpdated = await db.Proxies.Where(x => x.CheckLeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CheckLeaseUntil, until), token);
        return proxiesUpdated > 0;
    }

    /// <summary>Проверяет полноту и ownership партии, затем атомарно применяет её результаты.</summary>
    public async Task<CheckerLeaseCompletion> CompleteAsync(
        Guid nodeId, Guid leaseId, CheckerLeaseResultRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        if (node.CurrentLeaseId != leaseId || node.CurrentLeaseUntil < now)
            throw new InvalidOperationException("Аренда истекла или больше не принадлежит checker-узлу.");

        var leasedIds = await db.Proxies.AsNoTracking().Where(x => x.CheckLeaseId == leaseId)
            .Select(x => new { x.Id, x.ConsecutiveFailedChecks }).ToListAsync(token);
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
        var persisted = await validator.PersistResultsAsync(updates, token);

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
            .SetProperty(x => x.LastError, Bounded(heartbeat.Error, 1000)), token);
    }

    private static string? Bounded(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(maximum, value.Trim().Length)];
}
