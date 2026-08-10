using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Единые bounded retention-запросы для pipeline и независимого maintenance worker.</summary>
internal static class OperationalRetention
{
    internal static Task<int> PruneProxyMembershipAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        int retentionDays,
        CancellationToken token)
    {
        var cutoff = now.AddDays(-Math.Max(1, retentionDays));
        return db.Proxies.Where(proxy =>
                (proxy.Status == ProxyStatus.Pending || proxy.Status == ProxyStatus.Dead) &&
                proxy.LastSeenAt < cutoff &&
                (proxy.CheckLeaseUntil == null || proxy.CheckLeaseUntil < now))
            .ExecuteDeleteAsync(token);
    }

    internal static async Task<(int CollectionRuns, int ValidationRuns)> PruneRunHistoryAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        int retentionDays,
        CancellationToken token)
    {
        var cutoff = now.AddDays(-Math.Max(1, retentionDays));
        var collectionRuns = await db.Runs
            .Where(run => run.StartedAt < cutoff && run.Status != "running")
            .ExecuteDeleteAsync(token);
        var validationRuns = await db.ValidationRuns
            .Where(run => run.StartedAt < cutoff && run.Status != "running")
            .ExecuteDeleteAsync(token);
        return (collectionRuns, validationRuns);
    }

    internal static Task<int> PruneBackupHistoryAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        int retentionDays,
        CancellationToken token)
    {
        var cutoff = now.AddDays(-Math.Max(1, retentionDays));
        return db.BackupRuns
            .Where(run => run.StartedAt < cutoff && run.Status != "running")
            .ExecuteDeleteAsync(token);
    }
}

/// <summary>Cluster-safe обслуживание таблиц, не зависящее от успеха collection/backup.</summary>
public sealed class OperationalMaintenanceService(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<CollectorOptions> collectorOptions,
    IOptions<BackupOptions> backupOptions)
{
    private long _lastSuccessUnixSeconds;
    private long _lastFailureUnixSeconds;
    private long _lastDeletedRows;
    private long _lastRecoveredRows;
    private int _status = -1;

    public long LastSuccessUnixSeconds => Interlocked.Read(ref _lastSuccessUnixSeconds);
    public long LastFailureUnixSeconds => Interlocked.Read(ref _lastFailureUnixSeconds);
    public long LastDeletedRows => Interlocked.Read(ref _lastDeletedRows);
    public long LastRecoveredRows => Interlocked.Read(ref _lastRecoveredRows);
    public int Status => Volatile.Read(ref _status);

    /// <summary>Возвращает null, когда maintenance уже выполняет другая реплика.</summary>
    public async Task<OperationalMaintenanceResult?> RunOnceAsync(CancellationToken token)
    {
        try
        {
            await using var clusterLock = await PostgresAdvisoryLock.TryAcquireAsync(
                dbFactory, PostgresAdvisoryLock.MaintenanceKey, token);
            if (clusterLock is null) return null;

            var now = DateTimeOffset.UtcNow;
            await using var db = await dbFactory.CreateDbContextAsync(token);
            var recoveredCollections = await RecoverCollectionRunsAsync(db, now, token);
            var recoveredValidations = await RecoverValidationRunsAsync(db, now, token);
            var recoveredBackups = await RecoverBackupRunsAsync(db, now, token);
            var proxies = await OperationalRetention.PruneProxyMembershipAsync(
                db, now, collectorOptions.Value.DeadRetentionDays, token);
            var histories = await OperationalRetention.PruneRunHistoryAsync(
                db, now, collectorOptions.Value.RunRetentionDays, token);
            var backups = await OperationalRetention.PruneBackupHistoryAsync(
                db, now, backupOptions.Value.HistoryRetentionDays, token);
            var result = new OperationalMaintenanceResult(
                now,
                proxies,
                histories.CollectionRuns,
                histories.ValidationRuns,
                backups,
                recoveredCollections,
                recoveredValidations,
                recoveredBackups);
            Interlocked.Exchange(ref _lastDeletedRows, result.TotalDeleted);
            Interlocked.Exchange(ref _lastRecoveredRows, result.TotalRecovered);
            Interlocked.Exchange(ref _lastSuccessUnixSeconds, now.ToUnixTimeSeconds());
            Volatile.Write(ref _status, 1);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Interlocked.Exchange(ref _lastFailureUnixSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Volatile.Write(ref _status, 0);
            throw;
        }
    }

    private async Task<int> RecoverCollectionRunsAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var operationLock = await PostgresAdvisoryLock.TryAcquireAsync(
            dbFactory, PostgresAdvisoryLock.CollectionKey, token);
        if (operationLock is null) return 0;
        return await db.Runs.Where(run => run.Status == "running" && run.FinishedAt == null &&
                run.StartedAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.FinishedAt, now)
                .SetProperty(run => run.Status, "failed")
                .SetProperty(run => run.Error,
                    "Сбор был прерван аварийным завершением предыдущего процесса."), token);
    }

    private async Task<int> RecoverValidationRunsAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        CancellationToken token)
    {
        var staleBefore = now.Subtract(
            ValidationLeasePolicy.Duration(collectorOptions.Value.ProbeTimeoutSeconds));
        return await db.ValidationRuns.Where(run => run.Status == "running" &&
                run.StartedAt < staleBefore &&
                !db.Proxies.Any(proxy => proxy.CheckLeaseId == run.LeaseId &&
                    proxy.CheckLeaseUntil >= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.FinishedAt, now)
                .SetProperty(run => run.Status, "failed")
                .SetProperty(run => run.Error,
                    "Validation-партия была прервана аварийным завершением предыдущего процесса."), token);
    }

    private async Task<int> RecoverBackupRunsAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var operationLock = await PostgresAdvisoryLock.TryAcquireAsync(
            dbFactory, PostgresAdvisoryLock.BackupKey, token);
        if (operationLock is null) return 0;
        return await db.BackupRuns.Where(run => run.Status == "running" && run.FinishedAt == null &&
                run.StartedAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.FinishedAt, now)
                .SetProperty(run => run.Status, "failed")
                .SetProperty(run => run.Error,
                    "Backup был прерван аварийным завершением предыдущего процесса."), token);
    }
}

/// <summary>Результат одного cluster-wide maintenance-цикла.</summary>
public sealed record OperationalMaintenanceResult(
    DateTimeOffset CompletedAt,
    int Proxies,
    int CollectionRuns,
    int ValidationRuns,
    int BackupRuns,
    int RecoveredCollectionRuns,
    int RecoveredValidationRuns,
    int RecoveredBackupRuns)
{
    public long TotalDeleted => (long)Proxies + CollectionRuns + ValidationRuns + BackupRuns;
    public long TotalRecovered =>
        (long)RecoveredCollectionRuns + RecoveredValidationRuns + RecoveredBackupRuns;
}

/// <summary>Ежечасно ограничивает рост operational-таблиц даже после ошибок других pipeline.</summary>
public sealed class OperationalMaintenanceWorker(
    OperationalMaintenanceService maintenance,
    ILogger<OperationalMaintenanceWorker> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly Action<ILogger, long, long, Exception?> MaintenanceCompleted =
        LoggerMessage.Define<long, long>(LogLevel.Information, new EventId(1401, "MaintenanceCompleted"),
            "Operational maintenance завершён: восстановлено {RecoveredRows}, удалено {DeletedRows} строк.");
    private static readonly Action<ILogger, Exception?> MaintenanceFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1402, "MaintenanceFailed"),
            "Operational maintenance завершился ошибкой.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await maintenance.RunOnceAsync(stoppingToken);
                if (result is not null)
                    MaintenanceCompleted(logger, result.TotalRecovered, result.TotalDeleted, null);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                MaintenanceFailed(logger, exception);
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
