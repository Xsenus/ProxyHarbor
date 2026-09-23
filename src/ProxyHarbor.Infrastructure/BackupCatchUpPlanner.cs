using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Bounded catch-up for missing routes of immutable backups. Existing copies/jobs, including
/// UNKNOWN outcomes, are never rearmed: they require their own reconciliation decision.
/// </summary>
public sealed class BackupCatchUpPlanner(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    BackupDestinationRegistry registry,
    BackupDeliveryPlanner deliveryPlanner)
{
    private const int MaximumOutstandingJobs = 32;
    private const int CandidateWindow = 64;

    /// <summary>Plans at most one copy under a row lock shared by worker replicas.</summary>
    public async Task<int> TryPlanAsync(bool oldestFirst, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await db.BackupDeliveryJobs.CountAsync(
                job => job.State == "pending" || job.State == "processing", token) >=
            MaximumOutstandingJobs)
        {
            await transaction.CommitAsync(token);
            return 0;
        }

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(token);
        var ordering = oldestFirst
            ? "run.\"StartedAt\" ASC, run.\"Id\" ASC"
            : "run.\"StartedAt\" DESC, run.\"Id\" DESC";
        var candidateIds = new List<Guid>();
        await using (var command = new NpgsqlCommand($"""
            SELECT run."Id"
            FROM "BackupRuns" AS run
            JOIN "BackupPools" AS pool ON pool."Id" = run."BackupPoolId"
            WHERE run."Status" = 'completed'
              AND run."ProtectionPolicyVersion" = pool."PolicyVersion"
              AND run."FileName" IS NOT NULL
              AND run."ContentSha256" IS NOT NULL AND run."SizeBytes" > 0
              AND EXISTS (
                SELECT 1 FROM "BackupCopies" AS source
                JOIN "BackupPoolDestinations" AS source_route
                  ON source_route."BackupPoolId" = pool."Id"
                  AND source_route."BackupDestinationId" = source."BackupDestinationId"
                JOIN "BackupDestinations" AS source_destination
                  ON source_destination."Id" = source."BackupDestinationId"
                WHERE source."BackupRunId" = run."Id"
                  AND source."State" = 'verified' AND source."VerifiedAt" IS NOT NULL
                  AND source."NativeLocator" IS NOT NULL
                  AND source."PolicyVersion" = run."ProtectionPolicyVersion"
                  AND source."ContentSha256" = run."ContentSha256"
                  AND source."SizeBytes" = run."SizeBytes"
                  AND source_destination."Enabled"
                  AND source_route."AllowedOperations" IN
                    ('read', 'verify,read', 'put,verify,read'))
              AND EXISTS (
                SELECT 1 FROM "BackupPoolDestinations" AS route
                JOIN "BackupDestinations" AS destination
                  ON destination."Id" = route."BackupDestinationId"
                WHERE route."BackupPoolId" = pool."Id"
                  AND route."Enabled" AND NOT route."Draining" AND destination."Enabled"
                  AND route."AllowedOperations" IN ('put', 'put,verify', 'put,verify,read')
                  AND NOT EXISTS (
                    SELECT 1 FROM "BackupCopies" AS existing
                    WHERE existing."BackupRunId" = run."Id"
                      AND existing."BackupDestinationId" = destination."Id"))
            ORDER BY
              CASE WHEN (
                SELECT COUNT(DISTINCT verified_destination."FailureDomain")
                FROM "BackupCopies" AS verified
                JOIN "BackupDestinations" AS verified_destination
                  ON verified_destination."Id" = verified."BackupDestinationId"
                WHERE verified."BackupRunId" = run."Id"
                  AND verified."State" = 'verified' AND verified."VerifiedAt" IS NOT NULL
                  AND verified."PolicyVersion" = run."ProtectionPolicyVersion"
                  AND verified."ContentSha256" = run."ContentSha256"
                  AND verified."SizeBytes" = run."SizeBytes"
              ) < COALESCE(run."RequiredVerifiedCopies", pool."RequiredVerifiedCopies")
                THEN 0 ELSE 1 END,
              {ordering}
            LIMIT {CandidateWindow}
            FOR UPDATE OF run SKIP LOCKED
            """, connection, (NpgsqlTransaction)transaction.GetDbTransaction()))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                candidateIds.Add(reader.GetGuid(0));
        }

        foreach (var runId in candidateIds)
        {
            var run = await db.BackupRuns.AsNoTracking().SingleAsync(item => item.Id == runId, token);
            var routes = await db.BackupPoolDestinations.AsNoTracking()
                .Where(item => item.BackupPoolId == run.BackupPoolId)
                .ToDictionaryAsync(item => item.BackupDestinationId, token);
            var copies = await db.BackupCopies.AsNoTracking()
                .Include(item => item.BackupDestination)
                .Where(item => item.BackupRunId == runId && item.State == "verified" &&
                    item.VerifiedAt != null && item.NativeLocator != null &&
                    item.PolicyVersion == run.ProtectionPolicyVersion &&
                    item.ContentSha256 == run.ContentSha256 && item.SizeBytes == run.SizeBytes)
                .ToArrayAsync(token);
            var readable = copies.Any(copy =>
            {
                if (!routes.TryGetValue(copy.BackupDestinationId, out var route)) return false;
                try
                {
                    _ = registry.Resolve(copy.BackupDestination, route,
                        BackupDestinationOperation.Materialize, copy.SizeBytes);
                    return true;
                }
                catch (BackupDestinationRouteException) { return false; }
            });
            if (!readable) continue;

            var planned = await deliveryPlanner.PlanAsync(
                db, runId, run.ContentSha256!, run.SizeBytes, token, maxNewCopies: 1);
            if (planned == 0) continue;
            await transaction.CommitAsync(token);
            return planned;
        }

        await transaction.CommitAsync(token);
        return 0;
    }
}
