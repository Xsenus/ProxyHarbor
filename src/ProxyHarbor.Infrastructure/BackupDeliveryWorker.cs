using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Один bounded durable delivery attempt без фонового scheduling loop.</summary>
public sealed class BackupDeliveryProcessor(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    BackupDestinationRegistry registry,
    IOptions<BackupOptions> backupOptions,
    IOptions<BackupRoutingOptions> routingOptions,
    IBackupConfigurationStore? configurationStore = null)
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReconciliationWindow = TimeSpan.FromHours(1);

    /// <summary>Атомарно арендует одну due job; SKIP LOCKED допускает несколько replicas.</summary>
    public async Task<BackupDeliveryLease?> TryClaimAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(token);
        var now = DateTimeOffset.UtcNow;
        var leaseId = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            SELECT job."Id"
            FROM "BackupDeliveryJobs" AS job
            JOIN "BackupCopies" AS copy ON copy."Id" = job."BackupCopyId"
            JOIN "BackupRuns" AS run ON run."Id" = copy."BackupRunId"
            WHERE job."State" = 'pending' AND job."NotBefore" <= @now
            ORDER BY
                CASE WHEN job."CreatedAt" <= @starvation THEN 0 ELSE 1 END,
                run."StartedAt" DESC,
                job."CreatedAt",
                job."Id"
            FOR UPDATE OF job SKIP LOCKED
            LIMIT 1
            """, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("starvation", now.AddHours(-1));
        var scalar = await command.ExecuteScalarAsync(token);
        if (scalar is not Guid jobId)
        {
            await transaction.CommitAsync(token);
            return null;
        }

        var updated = await db.BackupDeliveryJobs
            .Where(job => job.Id == jobId && job.State == "pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.State, "processing")
                .SetProperty(job => job.LeaseId, leaseId)
                .SetProperty(job => job.LeaseUntil, now.Add(LeaseDuration))
                .SetProperty(job => job.Attempt, job => job.Attempt + 1)
                .SetProperty(job => job.UpdatedAt, now), token);
        if (updated != 1) throw new InvalidOperationException("Backup delivery job lease потерян до commit.");
        await transaction.CommitAsync(token);
        return new BackupDeliveryLease(jobId, leaseId);
    }

    /// <summary>Арендует проверку UNKNOWN без повторного provider PUT.</summary>
    public async Task<BackupDeliveryLease?> TryClaimReconciliationAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(token);
        var now = DateTimeOffset.UtcNow;
        await using var command = new NpgsqlCommand("""
            SELECT "Id" FROM "BackupDeliveryJobs"
            WHERE "State" = 'reconciling' AND "NotBefore" <= @now
            ORDER BY "NotBefore", "CreatedAt", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
        command.Parameters.AddWithValue("now", now);
        var scalar = await command.ExecuteScalarAsync(token);
        if (scalar is not Guid jobId)
        {
            await transaction.CommitAsync(token);
            return null;
        }
        var leaseId = Guid.NewGuid();
        var updated = await db.BackupDeliveryJobs
            .Where(job => job.Id == jobId && job.State == "reconciling")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.State, "processing")
                .SetProperty(job => job.LeaseId, leaseId)
                .SetProperty(job => job.LeaseUntil, now.Add(LeaseDuration))
                .SetProperty(job => job.UpdatedAt, now), token);
        if (updated != 1) throw new InvalidOperationException("Backup reconciliation lease потерян до commit.");
        await transaction.CommitAsync(token);
        return new BackupDeliveryLease(jobId, leaseId);
    }

    /// <summary>Не повторяет вслепую PUT после crash: просроченный outcome становится UNKNOWN.</summary>
    public async Task<int> ReconcileExpiredLeasesAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var now = DateTimeOffset.UtcNow;
        var expiredIds = await db.BackupDeliveryJobs.AsNoTracking()
            .Where(job => job.State == "processing" && job.LeaseUntil < now)
            .Select(job => job.Id)
            .ToArrayAsync(token);
        var reconciled = 0;
        foreach (var jobId in expiredIds)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand("""
                SELECT "LeaseId" FROM "BackupDeliveryJobs"
                WHERE "Id" = @id AND "State" = 'processing' AND "LeaseUntil" < @now
                FOR UPDATE
                """, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
            command.Parameters.AddWithValue("id", jobId);
            command.Parameters.AddWithValue("now", now);
            if (await command.ExecuteScalarAsync(token) is not Guid)
            {
                await transaction.CommitAsync(token);
                continue;
            }
            var job = await db.BackupDeliveryJobs.Include(item => item.BackupCopy)
                .SingleAsync(item => item.Id == jobId, token);
            job.State = "reconciling";
            job.NotBefore = now.Add(MinimumRetryDelay);
            job.LeaseId = null;
            job.LeaseUntil = null;
            job.LastErrorCode = BackupDestinationErrorCode.UnknownOutcome.ToString();
            job.UpdatedAt = now;
            job.BackupCopy.State = "unknown";
            job.BackupCopy.UnknownSince ??= now;
            job.BackupCopy.LastErrorCode = BackupDestinationErrorCode.UnknownOutcome.ToString();
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            db.ChangeTracker.Clear();
            reconciled++;
        }
        return reconciled;
    }

    /// <summary>Выполняет арендованную job и сохраняет typed outcome.</summary>
    public async Task ProcessAsync(BackupDeliveryLease lease, CancellationToken hostToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(hostToken);
        var job = await db.BackupDeliveryJobs
            .Include(item => item.BackupCopy).ThenInclude(copy => copy.BackupRun)
            .Include(item => item.BackupCopy).ThenInclude(copy => copy.BackupDestination)
            .SingleOrDefaultAsync(item => item.Id == lease.JobId, hostToken);
        if (job is null || job.State != "processing" || job.LeaseId != lease.LeaseId ||
            job.LeaseUntil <= DateTimeOffset.UtcNow)
            return;
        var copy = job.BackupCopy;
        var run = copy.BackupRun;
        var destination = copy.BackupDestination;
        if (DateTimeOffset.UtcNow - run.StartedAt >
            TimeSpan.FromHours(routingOptions.Value.StagingTtlHours))
        {
            await FinishPermanentAsync(db, job, BackupDestinationErrorCode.NotFound, hostToken);
            return;
        }
        if (run.BackupPoolId is not { } poolId || run.ProtectionPolicyVersion != copy.PolicyVersion)
        {
            await FinishPermanentAsync(db, job, BackupDestinationErrorCode.InvalidConfiguration, hostToken);
            return;
        }
        var pool = await db.BackupPools.AsNoTracking().SingleOrDefaultAsync(item => item.Id == poolId, hostToken);
        var route = await db.BackupPoolDestinations.AsNoTracking().SingleOrDefaultAsync(
            item => item.BackupPoolId == poolId && item.BackupDestinationId == destination.Id,
            hostToken);
        if (pool is null || pool.PolicyVersion != copy.PolicyVersion || route is null)
        {
            await FinishPermanentAsync(db, job, BackupDestinationErrorCode.InvalidConfiguration, hostToken);
            return;
        }

        BackupOptions current;
        try
        {
            current = configurationStore is null
                ? backupOptions.Value
                : await configurationStore.GetAsync(hostToken);
        }
        catch (InvalidOperationException)
        {
            await FinishPermanentAsync(db, job, BackupDestinationErrorCode.InvalidConfiguration, hostToken);
            return;
        }
        var path = string.Empty;
        var contentVerified = false;
        try
        {
            path = ResolveStagingPath(current.Directory, run.FileName);
            contentVerified = await MatchesContentAsync(
                path, copy.SizeBytes, copy.ContentSha256, hostToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // Ошибка staging классифицируется ниже как permanent integrity failure.
        }
        if (!contentVerified)
        {
            await FinishPermanentAsync(db, job, BackupDestinationErrorCode.IntegrityMismatch, hostToken);
            return;
        }
        if (job.LeaseUntil <= DateTimeOffset.UtcNow)
        {
            await FinishUnknownAsync(db, job, hostToken);
            return;
        }

        IBackupDestinationAdapter adapter;
        try
        {
            adapter = registry.Resolve(destination, route, BackupDestinationOperation.Put, copy.SizeBytes);
        }
        catch (BackupDestinationRouteException)
        {
            await FinishPermanentAsync(db, job, BackupDestinationErrorCode.InvalidConfiguration, hostToken);
            return;
        }

        copy.State = "uploading";
        copy.AttemptCount = job.Attempt;
        copy.LastAttemptAt = DateTimeOffset.UtcNow;
        copy.LastErrorCode = null;
        if (!await SaveLeaseOutcomeAsync(db, job, hostToken)) return;
        var remainingLease = job.LeaseUntil!.Value - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
        if (remainingLease <= TimeSpan.Zero)
        {
            await FinishUnknownAsync(db, job, hostToken);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        var operationBudget = TimeSpan.FromSeconds(pool.OverallDeadlineSeconds);
        deadline.CancelAfter(remainingLease < operationBudget ? remainingLease : operationBudget);
        try
        {
            var result = await adapter.PutAsync(
                destination,
                path,
                copy.ContentSha256,
                copy.SizeBytes,
                deadline.Token);
            var finishedAt = DateTimeOffset.UtcNow;
            copy.NativeLocator = result.NativeLocator;
            copy.NativeVersion = result.NativeVersion;
            copy.NativeChecksum = result.NativeChecksum;
            copy.State = result.IndependentlyVerified && !string.IsNullOrWhiteSpace(result.NativeLocator)
                ? "verified"
                : "manual_review";
            copy.VerifiedAt = copy.State == "verified" ? finishedAt : null;
            copy.LastErrorCode = copy.State == "verified"
                ? null
                : BackupDestinationErrorCode.UnsupportedOperation.ToString();
            job.State = "completed";
            ClearLease(job, finishedAt);
            if (string.Equals(destination.Kind, "s3", StringComparison.Ordinal))
            {
                run.SentToObjectStorage = true;
                run.ObjectStorageKey = result.NativeLocator;
            }
            else if (string.Equals(destination.Kind, "telegram", StringComparison.Ordinal))
            {
                run.SentToTelegram = true;
            }
            await SaveLeaseOutcomeAsync(db, job, CancellationToken.None);
        }
        catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
        {
            // Host shutdown сохраняет replayable job; provider outcome мог стать UNKNOWN.
            await FinishUnknownAsync(db, job, CancellationToken.None);
        }
        catch (BackupDestinationOperationException exception)
        {
            if (exception.Failure.Code == BackupDestinationErrorCode.Collision)
                await FinishUnknownAsync(db, job, CancellationToken.None);
            else
                await FinishFailureAsync(db, job, pool.MaxAttemptsPerCycle, exception.Failure, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await FinishFailureAsync(
                db,
                job,
                pool.MaxAttemptsPerCycle,
                new BackupDestinationFailure(
                    BackupDestinationErrorCode.Timeout,
                    BackupDestinationFailureDisposition.Retryable),
                CancellationToken.None);
        }
    }

    /// <summary>Разрешает UNKNOWN только независимой проверкой locator; PUT здесь запрещён.</summary>
    public async Task ProcessReconciliationAsync(BackupDeliveryLease lease, CancellationToken hostToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(hostToken);
        var job = await db.BackupDeliveryJobs
            .Include(item => item.BackupCopy).ThenInclude(copy => copy.BackupRun)
            .Include(item => item.BackupCopy).ThenInclude(copy => copy.BackupDestination)
            .SingleOrDefaultAsync(item => item.Id == lease.JobId, hostToken);
        if (job is null || job.State != "processing" || job.LeaseId != lease.LeaseId ||
            job.LeaseUntil <= DateTimeOffset.UtcNow || job.BackupCopy.UnknownSince is null)
            return;
        var copy = job.BackupCopy;
        var destination = copy.BackupDestination;
        BackupDestinationProbeResult result;
        try
        {
            var route = await db.BackupPoolDestinations.AsNoTracking().SingleOrDefaultAsync(
                item => item.BackupPoolId == copy.BackupRun.BackupPoolId &&
                    item.BackupDestinationId == destination.Id, hostToken);
            if (route is null) throw new BackupDestinationRouteException(
                BackupDestinationRouteRejection.RouteForbidden, "Backup route отсутствует.");
            var adapter = registry.Resolve(destination, route, BackupDestinationOperation.Verify, copy.SizeBytes);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            var remaining = job.LeaseUntil.GetValueOrDefault() - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
            if (remaining <= TimeSpan.Zero)
                result = new BackupDestinationProbeResult(BackupDestinationProbeOutcome.Inconclusive);
            else
            {
                deadline.CancelAfter(remaining);
                result = await adapter.ProbeWriteOutcomeAsync(
                    destination, copy.BackupRun.FileName ?? string.Empty,
                    copy.ContentSha256, copy.SizeBytes, deadline.Token);
            }
        }
        catch (BackupDestinationRouteException)
        {
            result = new BackupDestinationProbeResult(BackupDestinationProbeOutcome.Unsupported);
        }
        catch (BackupDestinationOperationException)
        {
            result = new BackupDestinationProbeResult(BackupDestinationProbeOutcome.Inconclusive);
        }
        catch (OperationCanceledException) when (!hostToken.IsCancellationRequested)
        {
            result = new BackupDestinationProbeResult(BackupDestinationProbeOutcome.Inconclusive);
        }

        var now = DateTimeOffset.UtcNow;
        switch (result.Outcome)
        {
            case BackupDestinationProbeOutcome.Matching when !string.IsNullOrWhiteSpace(result.NativeLocator):
                copy.State = "verified";
                copy.NativeLocator = result.NativeLocator;
                copy.NativeVersion = result.NativeVersion;
                copy.NativeChecksum = result.NativeChecksum;
                copy.VerifiedAt = now;
                copy.UnknownSince = null;
                copy.LastErrorCode = null;
                job.State = "completed";
                job.LastErrorCode = null;
                if (string.Equals(destination.Kind, "s3", StringComparison.Ordinal))
                {
                    copy.BackupRun.SentToObjectStorage = true;
                    copy.BackupRun.ObjectStorageKey = result.NativeLocator;
                }
                break;
            case BackupDestinationProbeOutcome.Mismatching:
                copy.State = "quarantined";
                copy.UnknownSince = null;
                copy.LastErrorCode = BackupDestinationErrorCode.IntegrityMismatch.ToString();
                job.State = "failed";
                job.LastErrorCode = BackupDestinationErrorCode.IntegrityMismatch.ToString();
                break;
            case BackupDestinationProbeOutcome.Inconclusive
                when now - copy.UnknownSince < ReconciliationWindow:
                copy.State = "unknown";
                job.State = "reconciling";
                job.NotBefore = now.Add(RetryDelay(job.Id, Math.Max(1, job.Attempt)));
                break;
            default:
                // Missing даже после HEAD не доказывает безопасность повторного PUT на
                // произвольном S3-compatible backend. Оператор решает дальнейший retry.
                copy.State = "manual_review";
                copy.LastErrorCode = result.Outcome == BackupDestinationProbeOutcome.Missing
                    ? BackupDestinationErrorCode.NotFound.ToString()
                    : BackupDestinationErrorCode.UnknownOutcome.ToString();
                job.State = "manual_review";
                job.LastErrorCode = copy.LastErrorCode;
                break;
        }
        ClearLease(job, now);
        await SaveLeaseOutcomeAsync(db, job, CancellationToken.None);
    }

    internal static TimeSpan RetryDelay(Guid jobId, int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 6);
        var baseSeconds = Math.Min(
            MaximumRetryDelay.TotalSeconds,
            MinimumRetryDelay.TotalSeconds * Math.Pow(2, exponent));
        var jitter = BitConverter.ToUInt32(jobId.ToByteArray(), 0) % 1_001 / 10_000d;
        return TimeSpan.FromSeconds(baseSeconds * (0.95d + jitter));
    }

    private static async Task FinishFailureAsync(
        ProxyHarborDbContext db,
        BackupDeliveryJob job,
        int maxAttempts,
        BackupDestinationFailure failure,
        CancellationToken token)
    {
        if (failure.Disposition == BackupDestinationFailureDisposition.UnknownOutcome)
        {
            await FinishUnknownAsync(db, job, token);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var retry = failure.Disposition == BackupDestinationFailureDisposition.Retryable &&
            job.Attempt < maxAttempts;
        job.State = retry ? "pending" : "failed";
        job.NotBefore = retry ? now.Add(RetryDelay(job.Id, job.Attempt)) : job.NotBefore;
        job.LastErrorCode = failure.Code.ToString();
        job.BackupCopy.State = retry ? "retryable_failed" : "permanent_failed";
        job.BackupCopy.LastErrorCode = failure.Code.ToString();
        ClearLease(job, now);
        await SaveLeaseOutcomeAsync(db, job, token);
    }

    private static async Task FinishUnknownAsync(
        ProxyHarborDbContext db,
        BackupDeliveryJob job,
        CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        job.State = "reconciling";
        job.NotBefore = now.Add(MinimumRetryDelay);
        job.LastErrorCode = BackupDestinationErrorCode.UnknownOutcome.ToString();
        job.BackupCopy.State = "unknown";
        job.BackupCopy.UnknownSince ??= now;
        job.BackupCopy.LastErrorCode = BackupDestinationErrorCode.UnknownOutcome.ToString();
        ClearLease(job, now);
        await SaveLeaseOutcomeAsync(db, job, token);
    }

    private static Task FinishPermanentAsync(
        ProxyHarborDbContext db,
        BackupDeliveryJob job,
        BackupDestinationErrorCode code,
        CancellationToken token) => FinishFailureAsync(
            db,
            job,
            maxAttempts: 0,
            new BackupDestinationFailure(code, BackupDestinationFailureDisposition.Permanent),
            token);

    private static void ClearLease(BackupDeliveryJob job, DateTimeOffset now)
    {
        job.LeaseId = null;
        job.LeaseUntil = null;
        job.UpdatedAt = now;
    }

    private static async Task<bool> SaveLeaseOutcomeAsync(
        ProxyHarborDbContext db,
        BackupDeliveryJob job,
        CancellationToken token)
    {
        var expectedLease = db.Entry(job).Property(item => item.LeaseId).OriginalValue;
        if (expectedLease is null) return false;
        if (!db.Database.IsRelational())
        {
            await db.SaveChangesAsync(token);
            return true;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT "LeaseId" FROM "BackupDeliveryJobs"
            WHERE "Id" = @id AND "State" = 'processing' AND "LeaseUntil" > @now
            FOR UPDATE
            """, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
        command.Parameters.AddWithValue("id", job.Id);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        if (await command.ExecuteScalarAsync(token) is not Guid currentLease || currentLease != expectedLease)
        {
            await transaction.RollbackAsync(token);
            return false;
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    private static string ResolveStagingPath(string directory, string? fileName)
    {
        if (!BackupOptions.IsDirectoryValid(directory) || string.IsNullOrWhiteSpace(fileName) ||
            fileName != Path.GetFileName(fileName))
            throw new InvalidOperationException("Backup staging path некорректен.");
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup staging path выходит за разрешённый каталог.");
        return path;
    }

    private static async Task<bool> MatchesContentAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken token)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expectedSize) return false;
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        return string.Equals(hash, expectedSha256, StringComparison.Ordinal);
    }
}

/// <summary>Fencing identity одной арендованной delivery job.</summary>
public sealed record BackupDeliveryLease(Guid JobId, Guid LeaseId);

/// <summary>Фоново дренирует durable backup jobs только при включённом routing flag.</summary>
public sealed class BackupDeliveryWorker(
    BackupDeliveryProcessor processor,
    IOptions<BackupRoutingOptions> routingOptions,
    ILogger<BackupDeliveryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly Action<ILogger, Exception?> CycleFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1510, "BackupDeliveryCycleFailed"),
        "Backup delivery worker cycle завершился ошибкой.");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!routingOptions.Value.Enabled)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }
                _ = await processor.ReconcileExpiredLeasesAsync(stoppingToken);
                var reconciliation = await processor.TryClaimReconciliationAsync(stoppingToken);
                if (reconciliation is not null)
                {
                    await processor.ProcessReconciliationAsync(reconciliation, stoppingToken);
                    continue;
                }
                var lease = await processor.TryClaimAsync(stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }
                await processor.ProcessAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                CycleFailed(logger, exception);
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }
}
