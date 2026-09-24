using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Bounded catch-up for missing routes of immutable backups. Only a failed copy whose durable
/// LastAttemptAt is null can be rearmed: UNKNOWN and any possibly-started PUT stay untouched.
/// </summary>
public sealed class BackupCatchUpPlanner(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    BackupDestinationRegistry registry,
    BackupDeliveryPlanner deliveryPlanner)
{
    private const int MaximumOutstandingJobs = 32;
    private const int CandidateWindow = 64;
    private const int MaximumJobsPerPrePutCopy = 3;
    private static readonly TimeSpan PrePutRearmCooldown = TimeSpan.FromMinutes(15);

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
                SELECT COUNT(DISTINCT lower(btrim(verified_destination."FailureDomain")))
                FROM "BackupCopies" AS verified
                JOIN "BackupDestinations" AS verified_destination
                  ON verified_destination."Id" = verified."BackupDestinationId"
                JOIN "BackupPoolDestinations" AS verified_route
                  ON verified_route."BackupPoolId" = pool."Id"
                  AND verified_route."BackupDestinationId" = verified_destination."Id"
                WHERE verified."BackupRunId" = run."Id"
                  AND verified."State" = 'verified' AND verified."VerifiedAt" IS NOT NULL
                  AND verified."NativeLocator" IS NOT NULL
                  AND verified."PolicyVersion" = run."ProtectionPolicyVersion"
                  AND verified."ContentSha256" = run."ContentSha256"
                  AND verified."SizeBytes" = run."SizeBytes"
                  AND verified_destination."Enabled"
                  AND (verified_route."Enabled" OR verified_route."Draining")
                  AND verified_route."AllowedOperations" IN
                    ('verify', 'verify,read', 'put,verify', 'put,verify,read')
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
                .Where(item => item.BackupRunId == runId)
                .ToArrayAsync(token);
            if (!HasReadableSource(run, copies, routes, registry)) continue;

            var planned = await deliveryPlanner.PlanAsync(
                db, runId, run.ContentSha256!, run.SizeBytes, token, maxNewCopies: 1);
            if (planned == 0) continue;
            await transaction.CommitAsync(token);
            return planned;
        }

        await transaction.CommitAsync(token);
        return 0;
    }

    /// <summary>
    /// Rearms at most one failed copy that never reached provider PUT. Each round gets a new
    /// immutable job and deadline; old failed jobs remain as audit. This is not UNKNOWN retry.
    /// </summary>
    public async Task<int> TryRearmPrePutFailureAsync(CancellationToken token)
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

        var now = DateTimeOffset.UtcNow;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(token);
        var candidateIds = new List<Guid>();
        await using (var command = new NpgsqlCommand($"""
            SELECT copy."Id"
            FROM "BackupCopies" AS copy
            JOIN "BackupRuns" AS run ON run."Id" = copy."BackupRunId"
            JOIN "BackupPools" AS pool ON pool."Id" = run."BackupPoolId"
            JOIN "BackupDestinations" AS destination ON destination."Id" = copy."BackupDestinationId"
            JOIN "BackupPoolDestinations" AS route
              ON route."BackupPoolId" = pool."Id"
              AND route."BackupDestinationId" = destination."Id"
            WHERE run."Status" = 'completed'
              AND run."ProtectionPolicyVersion" = pool."PolicyVersion"
              AND copy."PolicyVersion" = pool."PolicyVersion"
              AND copy."ContentSha256" = run."ContentSha256"
              AND copy."SizeBytes" = run."SizeBytes" AND copy."SizeBytes" > 0
              AND run."FileName" IS NOT NULL
              AND copy."State" = 'permanent_failed'
              AND copy."LastAttemptAt" IS NULL
              AND copy."UnknownSince" IS NULL
              AND copy."NativeLocator" IS NULL AND copy."VerifiedAt" IS NULL
              AND copy."CatalogState" IS NULL
              AND route."Enabled" AND NOT route."Draining" AND destination."Enabled"
              AND route."AllowedOperations" IN ('put', 'put,verify', 'put,verify,read')
              AND (SELECT COUNT(*) FROM "BackupDeliveryJobs" AS job
                   WHERE job."BackupCopyId" = copy."Id") BETWEEN 1 AND {MaximumJobsPerPrePutCopy - 1}
              AND NOT EXISTS (
                  SELECT 1 FROM "BackupDeliveryJobs" AS job
                  WHERE job."BackupCopyId" = copy."Id" AND job."State" <> 'failed')
              AND (SELECT MAX(job."UpdatedAt") FROM "BackupDeliveryJobs" AS job
                   WHERE job."BackupCopyId" = copy."Id") <= @cooldown
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
            ORDER BY
              CASE WHEN (
                SELECT COUNT(DISTINCT lower(btrim(verified_destination."FailureDomain")))
                FROM "BackupCopies" AS verified
                JOIN "BackupDestinations" AS verified_destination
                  ON verified_destination."Id" = verified."BackupDestinationId"
                JOIN "BackupPoolDestinations" AS verified_route
                  ON verified_route."BackupPoolId" = pool."Id"
                  AND verified_route."BackupDestinationId" = verified_destination."Id"
                WHERE verified."BackupRunId" = run."Id"
                  AND verified."State" = 'verified' AND verified."VerifiedAt" IS NOT NULL
                  AND verified."NativeLocator" IS NOT NULL
                  AND verified."PolicyVersion" = run."ProtectionPolicyVersion"
                  AND verified."ContentSha256" = run."ContentSha256"
                  AND verified."SizeBytes" = run."SizeBytes"
                  AND verified_destination."Enabled"
                  AND (verified_route."Enabled" OR verified_route."Draining")
                  AND verified_route."AllowedOperations" IN
                    ('verify', 'verify,read', 'put,verify', 'put,verify,read')
              ) < COALESCE(run."RequiredVerifiedCopies", pool."RequiredVerifiedCopies")
                THEN 0 ELSE 1 END,
              run."StartedAt" DESC, route."Priority", copy."Id"
            LIMIT {CandidateWindow}
            FOR UPDATE OF copy SKIP LOCKED
            """, connection, (NpgsqlTransaction)transaction.GetDbTransaction()))
        {
            command.Parameters.AddWithValue("cooldown", now.Subtract(PrePutRearmCooldown));
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                candidateIds.Add(reader.GetGuid(0));
        }

        foreach (var copyId in candidateIds)
        {
            var copy = await db.BackupCopies
                .Include(item => item.BackupRun)
                .Include(item => item.BackupDestination)
                .Include(item => item.Jobs)
                .SingleAsync(item => item.Id == copyId, token);
            var run = copy.BackupRun;
            var pool = await db.BackupPools.AsNoTracking()
                .SingleAsync(item => item.Id == run.BackupPoolId, token);
            var routes = await db.BackupPoolDestinations.AsNoTracking()
                .Where(item => item.BackupPoolId == pool.Id)
                .ToDictionaryAsync(item => item.BackupDestinationId, token);
            var copies = await db.BackupCopies.AsNoTracking()
                .Include(item => item.BackupDestination)
                .Where(item => item.BackupRunId == run.Id)
                .ToArrayAsync(token);
            if (!CanRearmPrePutFailure(copy, run, pool, routes, now) ||
                !HasReadableSource(run, copies, routes, registry))
                continue;
            try
            {
                _ = registry.Resolve(copy.BackupDestination,
                    routes[copy.BackupDestinationId], BackupDestinationOperation.Put, copy.SizeBytes);
            }
            catch (BackupDestinationRouteException)
            {
                continue;
            }

            var round = copy.Jobs.Count;
            copy.State = "planned";
            copy.LastErrorCode = null;
            db.BackupDeliveryJobs.Add(new BackupDeliveryJob
            {
                BackupCopyId = copy.Id,
                IdempotencyKey = $"{run.Id:N}:{copy.BackupDestinationId:N}:put:v{copy.PolicyVersion}:rearm:{round}",
                State = "pending",
                NotBefore = now,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return 1;
        }

        await transaction.CommitAsync(token);
        return 0;
    }

    internal static bool CanRearmPrePutFailure(
        BackupCopy copy,
        BackupRun run,
        BackupPool pool,
        IReadOnlyDictionary<Guid, BackupPoolDestination> routes,
        DateTimeOffset now)
    {
        if (run.Status != "completed" || run.BackupPoolId != pool.Id ||
            copy.BackupRunId != run.Id || string.IsNullOrWhiteSpace(run.FileName) ||
            run.ProtectionPolicyVersion != pool.PolicyVersion ||
            copy.PolicyVersion != pool.PolicyVersion ||
            copy.State != "permanent_failed" || copy.LastAttemptAt is not null ||
            copy.UnknownSince is not null || copy.NativeLocator is not null ||
            copy.NativeVersion is not null || copy.NativeChecksum is not null ||
            copy.VerifiedAt is not null || copy.CatalogState is not null ||
            copy.CatalogObjectKey is not null || copy.AttemptCount != 0 ||
            copy.SizeBytes <= 0 || copy.SizeBytes != run.SizeBytes ||
            !string.Equals(copy.ContentSha256, run.ContentSha256, StringComparison.Ordinal) ||
            !routes.TryGetValue(copy.BackupDestinationId, out var route) ||
            route.BackupPoolId != pool.Id || route.BackupDestinationId != copy.BackupDestinationId ||
            !route.Enabled || route.Draining || !copy.BackupDestination.Enabled ||
            copy.Jobs.Count is < 1 or >= MaximumJobsPerPrePutCopy ||
            copy.Jobs.Any(job => job.State != "failed" || !IsSafePrePutFailureCode(job.LastErrorCode)))
            return false;
        var latest = copy.Jobs.OrderByDescending(job => job.UpdatedAt)
            .ThenByDescending(job => job.Id).First();
        return latest.UpdatedAt <= now.Subtract(PrePutRearmCooldown) &&
            string.Equals(copy.LastErrorCode, latest.LastErrorCode, StringComparison.Ordinal);
    }

    private static bool IsSafePrePutFailureCode(string? code) => code is
        nameof(BackupDestinationErrorCode.InvalidConfiguration) or
        nameof(BackupDestinationErrorCode.IntegrityMismatch) or
        nameof(BackupDestinationErrorCode.Timeout) or
        nameof(BackupDestinationErrorCode.UnsupportedOperation);

    internal static bool HasReadableSource(
        BackupRun run,
        IEnumerable<BackupCopy> copies,
        IReadOnlyDictionary<Guid, BackupPoolDestination> routes,
        BackupDestinationRegistry registry)
    {
        foreach (var copy in copies)
        {
            if (copy.State != "verified" || copy.VerifiedAt is null ||
                string.IsNullOrWhiteSpace(copy.NativeLocator) ||
                copy.PolicyVersion != run.ProtectionPolicyVersion ||
                copy.SizeBytes != run.SizeBytes ||
                !string.Equals(copy.ContentSha256, run.ContentSha256, StringComparison.Ordinal) ||
                !routes.TryGetValue(copy.BackupDestinationId, out var route))
                continue;
            try
            {
                _ = registry.Resolve(copy.BackupDestination, route,
                    BackupDestinationOperation.Materialize, copy.SizeBytes);
                return true;
            }
            catch (BackupDestinationRouteException)
            {
                // Try another verified source without leaving the immutable pool.
            }
        }
        return false;
    }
}
