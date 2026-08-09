using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ProxyHarbor.Infrastructure;

/// <summary>Сессионная блокировка PostgreSQL для единственного cluster-wide запуска тяжёлой операции.</summary>
internal sealed class PostgresAdvisoryLock : IAsyncDisposable
{
    internal const long CollectionKey = 0x505248434F4C4C01;
    internal const long BackupKey = 0x5052484241434B02;
    private readonly NpgsqlConnection _connection;
    private readonly long _key;

    private PostgresAdvisoryLock(NpgsqlConnection connection, long key)
    {
        _connection = connection;
        _key = key;
    }

    internal static async Task<PostgresAdvisoryLock?> TryAcquireAsync(
        IDbContextFactory<ProxyHarborDbContext> dbFactory,
        long key,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Не найдена строка подключения PostgreSQL.");
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            var acquired = (bool)(await command.ExecuteScalarAsync(token) ?? false);
            if (acquired) return new PostgresAdvisoryLock(connection, key);
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
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _connection);
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
