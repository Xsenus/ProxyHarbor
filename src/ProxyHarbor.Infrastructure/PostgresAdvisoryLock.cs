using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ProxyHarbor.Infrastructure;

/// <summary>Сессионная блокировка PostgreSQL для единственного cluster-wide запуска тяжёлой операции.</summary>
internal sealed class PostgresAdvisoryLock : IAsyncDisposable
{
    internal const long CollectionKey = 0x505248434F4C4C01;
    internal const long BackupKey = 0x5052484241434B02;
    internal const long MaintenanceKey = 0x5052484D41494E04;
    private readonly NpgsqlConnection _connection;
    private readonly long _key;
    private readonly bool _shared;

    private PostgresAdvisoryLock(NpgsqlConnection connection, long key, bool shared)
    {
        _connection = connection;
        _key = key;
        _shared = shared;
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
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(token);
            var sql = shared
                ? "SELECT pg_try_advisory_lock_shared(@key)"
                : "SELECT pg_try_advisory_lock(@key)";
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("key", key);
            var acquired = (bool)(await command.ExecuteScalarAsync(token) ?? false);
            if (acquired) return new PostgresAdvisoryLock(connection, key, shared);
            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_connection.State == System.Data.ConnectionState.Open)
            {
                var sql = _shared
                    ? "SELECT pg_advisory_unlock_shared(@key)"
                    : "SELECT pg_advisory_unlock(@key)";
                await using var command = new NpgsqlCommand(sql, _connection);
                command.Parameters.AddWithValue("key", _key);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        catch
        {
            // Не возвращаем потенциально заблокированную сессию в pool.
            NpgsqlConnection.ClearPool(_connection);
        }
        finally { await _connection.DisposeAsync(); }
    }
}

/// <summary>Ожидаемая ошибка повторного cluster-wide запуска операции.</summary>
public sealed class OperationAlreadyRunningException(string operation)
    : InvalidOperationException($"Операция «{operation}» уже выполняется другой репликой.");
