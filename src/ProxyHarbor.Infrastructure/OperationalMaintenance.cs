using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Единые bounded retention-запросы для независимого maintenance worker.</summary>
internal static class OperationalRetention
{
    // Validation leases создаются тысячами в час. Ограниченный DELETE не держит
    // большую транзакцию и постепенно освобождает историю без всплеска WAL/IO.
    internal const int RunCleanupBatchSize = 10_000;

    internal const int ProxyCleanupBatchSize = 250;

    internal static async Task<int> PruneProxyMembershipAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        int retentionDays,
        CancellationToken token,
        int maximumRows = RunCleanupBatchSize,
        TimeSpan? timeBudget = null)
    {
        var cutoff = now.AddDays(-Math.Max(1, retentionDays));
        var rowLimit = Math.Clamp(maximumRows, 1, RunCleanupBatchSize);
        // Полный поиск по LastSeenAt не должен удерживать claim-lock: этот столбец
        // намеренно не индексируется ради HOT updates массового refresh каталога.
        var candidates = await db.Proxies
            .Where(proxy =>
                (proxy.Status == ProxyStatus.Pending || proxy.Status == ProxyStatus.Dead) &&
                proxy.FirstAliveAt == null && proxy.SuccessfulChecks == 0 &&
                proxy.LastSeenAt < cutoff &&
                !db.ProxyValidationLeases.Any(lease => lease.ProxyId == proxy.Id))
            .Select(proxy => proxy.Id)
            .Take(rowLimit)
            .ToArrayAsync(token);

        var budget = timeBudget ?? TimeSpan.FromMinutes(1);
        if (budget < TimeSpan.Zero || budget > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeBudget));
        var elapsed = Stopwatch.StartNew();
        var deleted = 0;
        var offset = 0;
        var batchSize = ProxyCleanupBatchSize;
        while (offset < candidates.Length && elapsed.Elapsed < budget)
        {
            token.ThrowIfCancellationRequested();
            var batch = candidates.Skip(offset).Take(batchSize).ToArray();
            try
            {
                deleted += await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(token);
                    // Ожидание чужой claim-lock не удерживает её: отдельный lock timeout
                    // позволяет пройти обычную очередь, не расходуя DELETE deadline.
                    await db.Database.ExecuteSqlRawAsync(
                        "SET LOCAL lock_timeout = '30s'", token);
                    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                    await PostgresAdvisoryLock.AcquireTransactionAsync(
                        connection,
                        (NpgsqlTransaction)transaction.GetDbTransaction(),
                        PostgresAdvisoryLock.ProxyValidationClaimKey,
                        token);
                    // Ограничиваем именно DELETE под уже полученной claim-lock.
                    await db.Database.ExecuteSqlRawAsync(
                        "SET LOCAL statement_timeout = '3s'", token);
                    // Кандидат мог получить lease, обновиться или успешно пройти проверку
                    // после поиска. Повторная проверка под claim-lock закрывает эту гонку.
                    // Любая lease, включая expired, сохраняет прежний порядок FK locks.
                    var batchDeleted = await db.Proxies.Where(proxy =>
                            batch.Contains(proxy.Id) &&
                            (proxy.Status == ProxyStatus.Pending || proxy.Status == ProxyStatus.Dead) &&
                            proxy.FirstAliveAt == null && proxy.SuccessfulChecks == 0 &&
                            proxy.LastSeenAt < cutoff &&
                            !db.ProxyValidationLeases.Any(lease => lease.ProxyId == proxy.Id))
                        .ExecuteDeleteAsync(token);
                    await transaction.CommitAsync(token);
                    return batchDeleted;
                });
                offset += batch.Length;
            }
            catch (PostgresException exception) when (
                !token.IsCancellationRequested && batch.Length > 1 &&
                exception.SqlState == PostgresErrorCodes.QueryCanceled &&
                exception.MessageText.Contains("statement timeout", StringComparison.OrdinalIgnoreCase))
            {
                // FK/heap IO на небольшой VPS может не уложить 250 строк в 3s.
                // Транзакция уже откатилась; повторяем те же IDs меньшей порцией.
                // Ошибка даже одной строки остаётся ошибкой maintenance.
                batchSize = Math.Max(1, batch.Length / 2);
            }
            // Между транзакциями queued claim получает блокировку раньше следующей
            // порции cleanup. Весь цикл ограничен объёмом и минутным IO budget.
        }
        return deleted;
    }

    internal static async Task<(int CollectionRuns, int ValidationRuns)> PruneRunHistoryAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        int collectionRetentionDays,
        int validationRetentionHours,
        CancellationToken token,
        int maximumRowsPerTable = RunCleanupBatchSize)
    {
        var cleanupBatchSize = Math.Clamp(maximumRowsPerTable, 1, RunCleanupBatchSize);
        var collectionCutoff = now.AddDays(-Math.Max(1, collectionRetentionDays));
        var validationCutoff = now.AddHours(-Math.Max(1, validationRetentionHours));
        var collectionRuns = await db.Runs
            .Where(run => run.StartedAt < collectionCutoff && run.Status != "running")
            .OrderBy(run => run.StartedAt)
            .Take(cleanupBatchSize)
            .ExecuteDeleteAsync(token);
        var validationRuns = await db.ValidationRuns
            .Where(run => run.StartedAt < validationCutoff && run.Status != "running")
            .OrderBy(run => run.StartedAt)
            .Take(cleanupBatchSize)
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
    IOptions<BackupOptions> backupOptions,
    IBackupConfigurationStore? backupConfigurationStore = null)
{
    private long _lastSuccessUnixSeconds;
    private long _lastFailureUnixSeconds;
    private long _lastDeletedRows;
    private long _lastRecoveredRows;
    private int _status = -1;

    /// <summary>Unix-время последнего успешного cluster-wide цикла либо ноль.</summary>
    public long LastSuccessUnixSeconds => Interlocked.Read(ref _lastSuccessUnixSeconds);
    /// <summary>Unix-время последнего неуспешного цикла либо ноль.</summary>
    public long LastFailureUnixSeconds => Interlocked.Read(ref _lastFailureUnixSeconds);
    /// <summary>Число строк, удалённых последним успешным циклом.</summary>
    public long LastDeletedRows => Interlocked.Read(ref _lastDeletedRows);
    /// <summary>Число orphan audit rows, восстановленных последним успешным циклом.</summary>
    public long LastRecoveredRows => Interlocked.Read(ref _lastRecoveredRows);
    /// <summary>-1 до первого запуска, 0 после ошибки, 1 после успеха.</summary>
    public int Status => Volatile.Read(ref _status);

    /// <summary>Возвращает null, когда maintenance уже выполняет другая реплика.</summary>
    public async Task<OperationalMaintenanceResult?> RunOnceAsync(CancellationToken token)
    {
        try
        {
            await using var databaseLease = await DatabaseRuntimeGate.TryAcquireOperationLeaseAsync(
                dbFactory, token);
            if (databaseLease is null) return null;
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
                db, now, collectorOptions.Value.RunRetentionDays,
                collectorOptions.Value.ValidationRunRetentionHours, token);
            var currentBackupOptions = backupConfigurationStore is null
                ? backupOptions.Value
                : await backupConfigurationStore.GetAsync(token);
            var backups = await OperationalRetention.PruneBackupHistoryAsync(
                db, now, currentBackupOptions.HistoryRetentionDays, token);
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
                !db.ProxyValidationLeases.Any(lease => lease.LeaseId == run.LeaseId &&
                    lease.LeaseUntil >= now))
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
    /// <summary>Суммарное число удалённых proxy и audit rows.</summary>
    public long TotalDeleted => (long)Proxies + CollectionRuns + ValidationRuns + BackupRuns;
    /// <summary>Суммарное число аварийно прерванных run'ов, переведённых в failed.</summary>
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
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(5);
    private static readonly Action<ILogger, long, long, Exception?> MaintenanceCompleted =
        LoggerMessage.Define<long, long>(LogLevel.Information, new EventId(1401, "MaintenanceCompleted"),
            "Operational maintenance завершён: восстановлено {RecoveredRows}, удалено {DeletedRows} строк.");
    private static readonly Action<ILogger, Exception?> MaintenanceFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1402, "MaintenanceFailed"),
            "Operational maintenance завершился ошибкой.");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var failed = false;
            try
            {
                var result = await maintenance.RunOnceAsync(stoppingToken);
                if (result is not null)
                    OperationalLogBoundary.Write(() =>
                        MaintenanceCompleted(logger, result.TotalRecovered, result.TotalDeleted, null));
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                failed = true;
                OperationalLogBoundary.Write(() => MaintenanceFailed(logger, exception));
            }
            await Task.Delay(NextDelay(failed), stoppingToken);
        }
    }

    internal static TimeSpan NextDelay(bool previousRunFailed) =>
        previousRunFailed ? FailureRetryInterval : Interval;
}
