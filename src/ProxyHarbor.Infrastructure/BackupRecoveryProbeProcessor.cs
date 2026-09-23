using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Bounded read-only provider probe of an existing verified S3 copy. It never creates,
/// overwrites or deletes provider objects; a proven missing/corrupt copy loses verified status.
/// </summary>
public sealed class BackupRecoveryProbeProcessor(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    BackupDestinationRegistry registry,
    BackupDestinationHealth health)
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Attempts at most one provider HEAD against a current allowlisted S3 copy.</summary>
    public async Task<int> TryProbeOneAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(token);
        var candidateIds = new List<Guid>();
        await using (var command = new NpgsqlCommand("""
            SELECT candidate."Id"
            FROM (
                SELECT DISTINCT ON (copy."BackupDestinationId")
                    copy."Id", copy."BackupDestinationId", copy."VerifiedAt",
                    route."Priority" AS "RoutePriority", destination."Priority" AS "DestinationPriority"
                FROM "BackupCopies" AS copy
                JOIN "BackupRuns" AS run ON run."Id" = copy."BackupRunId"
                JOIN "BackupPools" AS pool ON pool."Id" = run."BackupPoolId"
                JOIN "BackupDestinations" AS destination
                    ON destination."Id" = copy."BackupDestinationId"
                JOIN "BackupPoolDestinations" AS route
                    ON route."BackupPoolId" = pool."Id"
                    AND route."BackupDestinationId" = destination."Id"
                WHERE copy."State" = 'verified' AND copy."VerifiedAt" IS NOT NULL
                    AND copy."NativeLocator" IS NOT NULL
                    AND copy."PolicyVersion" = run."ProtectionPolicyVersion"
                    AND run."ProtectionPolicyVersion" = pool."PolicyVersion"
                    AND run."Status" = 'completed' AND run."FileName" IS NOT NULL
                    AND copy."ContentSha256" = run."ContentSha256"
                    AND copy."SizeBytes" = run."SizeBytes" AND copy."SizeBytes" > 0
                    AND destination."Enabled" AND destination."Kind" = 's3'
                    AND (route."Enabled" OR route."Draining")
                    AND route."AllowedOperations" IN
                        ('verify', 'put,verify', 'verify,read', 'put,verify,read')
                    AND NOT EXISTS (
                        SELECT 1 FROM "BackupDestinationHealthOutcomes" AS observation
                        WHERE observation."BackupDestinationId" = destination."Id"
                            AND observation."Operation" = 'verify'
                            AND observation."ObservedAt" >= @cutoff)
                ORDER BY copy."BackupDestinationId", copy."VerifiedAt" DESC, copy."Id"
            ) AS candidate
            ORDER BY candidate."RoutePriority", candidate."DestinationPriority",
                candidate."VerifiedAt" DESC, candidate."Id"
            LIMIT 32
            """, connection))
        {
            command.Parameters.AddWithValue("cutoff", DateTimeOffset.UtcNow.Subtract(ProbeInterval));
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                candidateIds.Add(reader.GetGuid(0));
        }

        foreach (var copyId in candidateIds)
        {
            var copy = await db.BackupCopies
                .Include(item => item.BackupRun)
                .Include(item => item.BackupDestination)
                .SingleAsync(item => item.Id == copyId, token);
            var route = await db.BackupPoolDestinations.AsNoTracking().SingleOrDefaultAsync(
                item => item.BackupPoolId == copy.BackupRun.BackupPoolId &&
                    item.BackupDestinationId == copy.BackupDestinationId, token);
            if (route is null || !IsCurrentVerifiedCopy(copy)) continue;
            IBackupDestinationAdapter adapter;
            try
            {
                adapter = registry.Resolve(
                    copy.BackupDestination, route, BackupDestinationOperation.Verify, copy.SizeBytes);
            }
            catch (BackupDestinationRouteException)
            {
                continue;
            }
            var decision = await health.TryEnterAsync(
                db, copy.BackupDestinationId, BackupDestinationOperation.Verify, token);
            if (!decision.Allowed) continue;

            var expectedLocator = copy.NativeLocator!;
            var expectedVerifiedAt = copy.VerifiedAt;
            var expectedHash = copy.ContentSha256;
            BackupDestinationProbeResult result;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ProbeDeadline);
            try
            {
                result = await adapter.ProbeWriteOutcomeAsync(
                    copy.BackupDestination, copy.BackupRun.FileName!, expectedHash,
                    copy.SizeBytes, deadline.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                health.ReleaseWithoutOutcome(copy.BackupDestinationId, BackupDestinationOperation.Verify);
                throw;
            }
            catch (OperationCanceledException)
            {
                result = new BackupDestinationProbeResult(
                    BackupDestinationProbeOutcome.Inconclusive,
                    FailureCode: BackupDestinationErrorCode.Timeout);
            }
            catch (BackupDestinationOperationException exception)
            {
                result = new BackupDestinationProbeResult(
                    BackupDestinationProbeOutcome.Inconclusive,
                    FailureCode: exception.Failure.Code);
            }
            catch (IOException)
            {
                result = new BackupDestinationProbeResult(
                    BackupDestinationProbeOutcome.Inconclusive,
                    FailureCode: BackupDestinationErrorCode.Unavailable);
            }
            token.ThrowIfCancellationRequested();
            var classification = ClassifyOutcome(result, expectedLocator);

            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await using (var command = new NpgsqlCommand("""
                SELECT "Id" FROM "BackupCopies" WHERE "Id" = @id FOR UPDATE
                """, connection, (NpgsqlTransaction)transaction.GetDbTransaction()))
            {
                command.Parameters.AddWithValue("id", copy.Id);
                if (await command.ExecuteScalarAsync(token) is not Guid)
                {
                    await transaction.CommitAsync(token);
                    health.ReleaseWithoutOutcome(copy.BackupDestinationId, BackupDestinationOperation.Verify);
                    return 0;
                }
            }
            await db.Entry(copy).ReloadAsync(token);
            if (!IsCurrentVerifiedCopy(copy) || copy.NativeLocator != expectedLocator ||
                copy.VerifiedAt != expectedVerifiedAt || copy.ContentSha256 != expectedHash)
            {
                await transaction.CommitAsync(token);
                health.ReleaseWithoutOutcome(copy.BackupDestinationId, BackupDestinationOperation.Verify);
                return 0;
            }
            if (classification.Conclusive && result.Outcome == BackupDestinationProbeOutcome.Missing)
            {
                copy.State = "missing";
                copy.VerifiedAt = null;
                copy.LastErrorCode = BackupDestinationErrorCode.NotFound.ToString();
            }
            else if (classification.Conclusive && result.Outcome == BackupDestinationProbeOutcome.Mismatching)
            {
                copy.State = "quarantined";
                copy.VerifiedAt = null;
                copy.LastErrorCode = BackupDestinationErrorCode.IntegrityMismatch.ToString();
            }
            db.BackupDestinationHealthOutcomes.Add(new BackupDestinationHealthOutcome
            {
                BackupDestinationId = copy.BackupDestinationId,
                Succeeded = classification.Conclusive,
                ErrorCode = classification.FailureCode?.ToString(),
                ProbeOutcome = classification.ExactOutcome,
                ObservedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            if (classification.Conclusive)
                health.RecordSuccess(copy.BackupDestinationId, BackupDestinationOperation.Verify);
            else
                health.RecordFailure(copy.BackupDestinationId, BackupDestinationOperation.Verify,
                    classification.FailureCode!.Value);
            return 1;
        }
        return 0;
    }

    internal static BackupRecoveryProbeClassification ClassifyOutcome(
        BackupDestinationProbeResult result, string expectedLocator)
    {
        var locatorMatches = string.Equals(
            result.NativeLocator, expectedLocator, StringComparison.Ordinal);
        var conclusive = locatorMatches && result.Outcome is
            BackupDestinationProbeOutcome.Matching or BackupDestinationProbeOutcome.Missing or
            BackupDestinationProbeOutcome.Mismatching;
        var exactOutcome = conclusive
            ? result.Outcome.ToString().ToLowerInvariant()
            : result.Outcome == BackupDestinationProbeOutcome.Inconclusive
                ? "inconclusive" : "invalid";
        BackupDestinationErrorCode? failureCode = conclusive ? null :
            result.FailureCode ?? (exactOutcome == "invalid"
                ? BackupDestinationErrorCode.InvalidConfiguration
                : BackupDestinationErrorCode.Unavailable);
        return new BackupRecoveryProbeClassification(conclusive, exactOutcome, failureCode);
    }

    internal static bool IsCurrentVerifiedCopy(BackupCopy copy) =>
        copy.State == "verified" && copy.VerifiedAt is not null &&
        !string.IsNullOrWhiteSpace(copy.NativeLocator) && copy.SizeBytes > 0 &&
        copy.BackupRun.Status == "completed" &&
        copy.PolicyVersion == copy.BackupRun.ProtectionPolicyVersion &&
        copy.ContentSha256 == copy.BackupRun.ContentSha256 &&
        copy.SizeBytes == copy.BackupRun.SizeBytes &&
        !string.IsNullOrWhiteSpace(copy.BackupRun.FileName);
}

internal sealed record BackupRecoveryProbeClassification(
    bool Conclusive, string ExactOutcome, BackupDestinationErrorCode? FailureCode);

/// <summary>Runs bounded read-only S3 probes without delaying backup delivery jobs.</summary>
public sealed class BackupRecoveryProbeWorker(
    BackupRecoveryProbeProcessor processor,
    IOptions<BackupRoutingOptions> routingOptions,
    ILogger<BackupRecoveryProbeWorker> logger) : BackgroundService
{
    private static readonly TimeSpan DisabledDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeDelay = TimeSpan.FromMinutes(1);
    private static readonly Action<ILogger, string, Exception?> ProbeCycleFailed =
        LoggerMessage.Define<string>(LogLevel.Warning,
            new EventId(1511, "BackupRecoveryProbeCycleFailed"),
            "Backup recovery probe cycle failed with {ExceptionType}.");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!routingOptions.Value.Enabled)
            {
                try { await Task.Delay(DisabledDelay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                continue;
            }
            try
            {
                _ = await processor.TryProbeOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never log provider response text, endpoints or protected configuration.
                ProbeCycleFailed(logger, exception.GetType().Name, null);
            }
            try { await Task.Delay(ProbeDelay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
