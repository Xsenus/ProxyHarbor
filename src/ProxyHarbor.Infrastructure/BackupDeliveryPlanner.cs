using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Создаёт idempotent copy/job graph для одного immutable routed backup.</summary>
public sealed class BackupDeliveryPlanner(BackupDestinationRegistry registry)
{
    /// <summary>
    /// Планирует только explicitly enabled PUT routes текущего pool. Метод не выполняет
    /// provider I/O и вызывается в той же транзакции, что completed transition run.
    /// </summary>
    public async Task<int> PlanAsync(
        ProxyHarborDbContext db,
        Guid backupRunId,
        string contentSha256,
        long sizeBytes,
        CancellationToken token,
        int maxNewCopies = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNewCopies);
        var run = await db.BackupRuns.SingleAsync(item => item.Id == backupRunId, token);
        if (run.BackupPoolId is not { } poolId || run.ProtectionPolicyVersion is not { } policyVersion)
            throw new InvalidOperationException("Backup run не содержит protection policy snapshot.");
        if (!string.Equals(run.ContentSha256, contentSha256, StringComparison.Ordinal) ||
            run.SizeBytes != sizeBytes)
            throw new InvalidOperationException("Backup run content identity не совпадает с planner input.");
        var pool = await db.BackupPools.AsNoTracking().SingleOrDefaultAsync(item => item.Id == poolId, token);
        if (run.Status != "completed" || pool is null || pool.PolicyVersion != policyVersion)
            throw new InvalidOperationException("Backup run не соответствует текущей версии policy.");

        var routes = await db.BackupPoolDestinations
            .Include(route => route.BackupDestination)
            .Where(route => route.BackupPoolId == poolId && route.Enabled && !route.Draining)
            .OrderBy(route => route.Priority)
            .ThenBy(route => route.BackupDestination.Priority)
            .ThenBy(route => route.BackupDestinationId)
            .ToArrayAsync(token);
        var existingDestinationIds = await db.BackupCopies
            .Where(copy => copy.BackupRunId == backupRunId)
            .Select(copy => copy.BackupDestinationId)
            .ToHashSetAsync(token);
        var planned = 0;
        foreach (var route in routes)
        {
            if (planned >= maxNewCopies) break;
            if (existingDestinationIds.Contains(route.BackupDestinationId)) continue;
            try
            {
                _ = registry.Resolve(
                    route.BackupDestination,
                    route,
                    BackupDestinationOperation.Put,
                    sizeBytes);
            }
            catch (BackupDestinationRouteException)
            {
                // Capability/route mismatch is fail-closed and never searches outside the pool.
                continue;
            }

            var copy = new BackupCopy
            {
                BackupRunId = backupRunId,
                BackupDestinationId = route.BackupDestinationId,
                ContentSha256 = contentSha256,
                SizeBytes = sizeBytes,
                State = "planned",
                PolicyVersion = policyVersion
            };
            copy.Jobs.Add(new BackupDeliveryJob
            {
                IdempotencyKey = $"{backupRunId:N}:{route.BackupDestinationId:N}:put:v{policyVersion}",
                State = "pending",
                NotBefore = DateTimeOffset.UtcNow
            });
            db.BackupCopies.Add(copy);
            existingDestinationIds.Add(route.BackupDestinationId);
            planned++;
        }
        await db.SaveChangesAsync(token);
        return planned;
    }
}
