using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ProxyHarbor.Infrastructure;

/// <summary>Сессионная блокировка PostgreSQL для единственного cluster-wide запуска тяжёлой операции.</summary>
internal sealed class PostgresAdvisoryLock : IAsyncDisposable
{
    internal const long CollectionKey = 0x505248434F4C4C01;
    internal const long BackupKey = 0x5052484241434B02;
    internal const long MaintenanceKey = 0x5052484D41494E04;
    internal const long RuntimeKey = 0x50524852554E5405;
    internal const long VpnCollectionKey = 0x50524856504E4306;
    internal const long VpnValidationKey = 0x50524856504E5607;
    internal const long VpnMutationKey = 0x50524856504E4D08;
    internal const long ProxyValidationClaimKey = 0x5052485056434C09;
    internal const string CleanupFailureDataKey = "ProxyHarbor.AdvisoryLockCleanupFailure";
    private static long _cleanupFailures;
    private readonly NpgsqlConnection _connection;
    private readonly long _key;
    private readonly bool _shared;
    private readonly PostgresAdvisoryLockExecutionHooks? _hooks;
    private int _disposed;

    internal int BackendProcessId => _connection.ProcessID;

    private PostgresAdvisoryLock(
        NpgsqlConnection connection,
        long key,
        bool shared,
        PostgresAdvisoryLockExecutionHooks? hooks)
    {
        _connection = connection;
        _key = key;
        _shared = shared;
        _hooks = hooks;
    }

    internal static async Task<PostgresAdvisoryLock?> TryAcquireAsync(
        IDbContextFactory<ProxyHarborDbContext> dbFactory,
        long key,
        CancellationToken token) =>
        await TryAcquireCoreAsync(dbFactory, key, shared: false, token);

    /// <summary>Shared-владелец совместим с другими mutation, но исключает collection-run.</summary>
    internal static async Task<PostgresAdvisoryLock?> TryAcquireSharedAsync(
        IDbContextFactory<ProxyHarborDbContext> dbFactory,
        long key,
        CancellationToken token) =>
        await TryAcquireCoreAsync(dbFactory, key, shared: true, token);

    private static async Task<PostgresAdvisoryLock?> TryAcquireCoreAsync(
        IDbContextFactory<ProxyHarborDbContext> dbFactory,
        long key,
        bool shared,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Не найдена строка подключения PostgreSQL.");
        return await TryAcquireCoreAsync(connectionString, key, shared, token);
    }

    internal static async Task<PostgresAdvisoryLock?> TryAcquireCoreAsync(
        string connectionString,
        long key,
        bool shared,
        CancellationToken token,
        PostgresAdvisoryLockExecutionHooks? hooks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var connection = new NpgsqlConnection(connectionString);
        Exception? primaryFailure = null;
        try
        {
            await connection.OpenAsync(token);
            var sql = shared
                ? "SELECT pg_try_advisory_lock_shared(@key)"
                : "SELECT pg_try_advisory_lock(@key)";
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("key", key);
            var acquired = (bool)(await command.ExecuteScalarAsync(token) ?? false);
            if (acquired)
            {
                hooks?.AfterLockAcquired?.Invoke();
                return new PostgresAdvisoryLock(connection, key, shared, hooks);
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            // Сервер мог получить session lock непосредственно перед сетевым отказом.
            NpgsqlConnection.ClearPool(connection);
        }

        var cleanupFailure = await DisposeConnectionAsync(connection, hooks);
        if (cleanupFailure is not null)
            Interlocked.Increment(ref _cleanupFailures);
        if (primaryFailure is not null)
        {
            if (cleanupFailure is not null)
                AddCleanupFailure(primaryFailure, "dispose", cleanupFailure);
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        return null;
    }

    /// <summary>
    /// Сериализует короткие транзакции, изменяющие один горячий каталог. В отличие от
    /// session lock этот lease автоматически снимается PostgreSQL при commit/rollback.
    /// </summary>
    internal static async Task AcquireTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long key,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@key)", connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Освобождение lease не меняет уже определённый caller outcome. Любая неоднозначность
    /// unlock исключает сессию из pool; физический dispose выполняется при любом результате.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Exception? unlockFailure = null;
        try
        {
            if (_connection.State == System.Data.ConnectionState.Open)
            {
                _hooks?.BeforeUnlock?.Invoke();
                var sql = _shared
                    ? "SELECT pg_advisory_unlock_shared(@key)"
                    : "SELECT pg_advisory_unlock(@key)";
                await using var command = new NpgsqlCommand(sql, _connection);
                command.Parameters.AddWithValue("key", _key);
                var released = (bool)(await command.ExecuteScalarAsync(CancellationToken.None) ?? false);
                if (!released)
                    throw new InvalidOperationException("PostgreSQL не подтвердил освобождение advisory lock.");
            }
        }
        catch (Exception exception)
        {
            unlockFailure = exception;
            // Не возвращаем потенциально заблокированную сессию в pool.
            NpgsqlConnection.ClearPool(_connection);
        }

        var disposeFailure = await DisposeConnectionAsync(_connection, _hooks);
        if (unlockFailure is not null || disposeFailure is not null)
            Interlocked.Increment(ref _cleanupFailures);
        ObserveCleanupFailure(_hooks, "unlock", unlockFailure);
        ObserveCleanupFailure(_hooks, "dispose", disposeFailure);
    }

    internal static long CleanupFailures => Interlocked.Read(ref _cleanupFailures);

    /// <summary>Всегда пытается выполнить настоящий async и fallback sync dispose.</summary>
    private static async ValueTask<Exception?> DisposeConnectionAsync(
        NpgsqlConnection connection,
        PostgresAdvisoryLockExecutionHooks? hooks)
    {
        Exception? failure = null;
        try { hooks?.BeforeConnectionDispose?.Invoke(); }
        catch (Exception exception) { failure = exception; }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure ??= exception;
            NpgsqlConnection.ClearPool(connection);
            try { connection.Dispose(); }
            catch (Exception fallbackFailure) { failure ??= fallbackFailure; }
        }
        return failure;
    }

    /// <summary>Прикрепляет bounded тип secondary failure без сообщения/connection string.</summary>
    private static void AddCleanupFailure(Exception primaryFailure, string stage, Exception cleanupFailure)
    {
        try
        {
            var detail = $"{stage}: {cleanupFailure.GetType().Name}";
            primaryFailure.Data[CleanupFailureDataKey] =
                primaryFailure.Data[CleanupFailureDataKey] is string previous
                    ? $"{previous} | {detail}"
                    : detail;
        }
        catch (Exception)
        {
            // Нестандартное read-only Exception.Data не может заменить primary acquire failure.
        }
    }

    /// <summary>Тестовая/диагностическая точка не участвует в production control flow.</summary>
    private static void ObserveCleanupFailure(
        PostgresAdvisoryLockExecutionHooks? hooks,
        string stage,
        Exception? failure)
    {
        if (failure is null) return;
        try { hooks?.CleanupFailureObserved?.Invoke(stage, failure); }
        catch (Exception) { /* observer не может изменить non-throwing disposal contract */ }
    }

    /// <summary>
    /// Проверяет именно выделенную lock-сессию. Успешный запрос доказывает, что backend,
    /// на котором был получен session-level advisory lock, всё ещё существует.
    /// </summary>
    internal async Task VerifySessionAsync(CancellationToken token)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
            throw new NpgsqlException("PostgreSQL advisory-lock session закрыта.");
        await using var command = new NpgsqlCommand("SELECT 1", _connection);
        var result = await command.ExecuteScalarAsync(token);
        if (result is not 1)
            throw new NpgsqlException("PostgreSQL не подтвердил advisory-lock session heartbeat.");
    }
}

/// <summary>
/// Внутренние lifecycle hooks для детерминированных PostgreSQL failure-canary. Production
/// acquisition их не передаёт; реальный unlock/dispose всегда выполняется независимо от observer.
/// </summary>
internal sealed record PostgresAdvisoryLockExecutionHooks(
    Action? AfterLockAcquired = null,
    Action? BeforeUnlock = null,
    Action? BeforeConnectionDispose = null,
    Action<string, Exception>? CleanupFailureObserved = null);

/// <summary>
/// Database-wide lifetime gate: API-реплики совместно владеют shared lease, а destructive
/// restore получает exclusive lease только после остановки всех реплик.
/// </summary>
public static class DatabaseRuntimeGate
{
    /// <summary>Число lease cleanup-инцидентов текущего процесса с момента запуска.</summary>
    public static long AdvisoryLockCleanupFailures => PostgresAdvisoryLock.CleanupFailures;

    /// <summary>Пытается зарегистрировать живую API-реплику; null означает активный restore.</summary>
    public static async Task<DatabaseRuntimeLease?> TryAcquireApiLeaseAsync(
        string connectionString,
        CancellationToken token)
    {
        var lease = await PostgresAdvisoryLock.TryAcquireCoreAsync(
            connectionString, PostgresAdvisoryLock.RuntimeKey, shared: true, token);
        return lease is null ? null : new DatabaseRuntimeLease(lease);
    }

    /// <summary>Пытается получить эксклюзивное владение БД для destructive restore.</summary>
    public static async Task<IAsyncDisposable?> TryAcquireRestoreLeaseAsync(
        string connectionString,
        CancellationToken token) =>
        await PostgresAdvisoryLock.TryAcquireCoreAsync(
            connectionString, PostgresAdvisoryLock.RuntimeKey, shared: false, token);

    /// <summary>
    /// Защищает отдельную write-операцию, даже если долгоживущая API-сессия была потеряна
    /// из-за сетевого сбоя раньше остановки процесса.
    /// </summary>
    public static async Task<IAsyncDisposable?> TryAcquireOperationLeaseAsync(
        IDbContextFactory<ProxyHarborDbContext> dbFactory,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        // Быстрые unit-тесты используют InMemory provider, где межпроцессного restore нет.
        if (!db.Database.IsRelational()) return NoOpLease.Instance;
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Не найдена строка подключения PostgreSQL.");
        return await PostgresAdvisoryLock.TryAcquireCoreAsync(
            connectionString, PostgresAdvisoryLock.RuntimeKey, shared: true, token);
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        internal static readonly NoOpLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Проверяемая lifetime-lease одной API-реплики.</summary>
public sealed class DatabaseRuntimeLease : IAsyncDisposable
{
    private readonly PostgresAdvisoryLock _lease;

    internal DatabaseRuntimeLease(PostgresAdvisoryLock lease) => _lease = lease;
    internal int BackendProcessId => _lease.BackendProcessId;

    /// <summary>Подтверждает, что PostgreSQL-сессия, владеющая shared lock, не потеряна.</summary>
    public Task VerifyAsync(CancellationToken token) => _lease.VerifySessionAsync(token);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _lease.DisposeAsync();
}

/// <summary>Ожидаемая ошибка повторного cluster-wide запуска операции.</summary>
public sealed class OperationAlreadyRunningException(string operation)
    : InvalidOperationException($"Операция «{operation}» уже выполняется другой репликой.");
