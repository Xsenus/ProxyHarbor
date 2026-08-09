using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Применяет миграции и синхронизирует встроенный каталог, не удаляя пользовательские источники.</summary>
public static class DatabaseSeeder
{
    private const long MigrationLockKey = 0x5052484D49475203;

    /// <summary>Добавляет недостающие feed'ы и обновляет их метаданные, сохраняя выбор Enabled/Disabled.</summary>
    public static Task InitializeAsync(ProxyHarborDbContext db, CancellationToken cancellationToken = default) =>
        ExecuteWithMigrationLockAsync(db, MigrateAndSeedAsync, cancellationToken);

    /// <summary>Применяет только DDL migrations под общей startup-блокировкой, не изменяя строки приложения.</summary>
    public static Task MigrateSchemaAsync(ProxyHarborDbContext db, CancellationToken cancellationToken = default) =>
        ExecuteWithMigrationLockAsync(
            db,
            static (context, token) => context.Database.MigrateAsync(token),
            cancellationToken);

    private static async Task ExecuteWithMigrationLockAsync(
        ProxyHarborDbContext db,
        Func<ProxyHarborDbContext, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var closeWhenFinished = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenFinished) await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // EF защищает отдельные migration-команды транзакциями, но две одновременно стартующие
            // реплики всё равно могут увидеть один набор pending migrations. Сессионная блокировка
            // сериализует и миграции, и последующий idempotent seed для всех реплик общей БД.
            try
            {
                await SetMigrationLockAsync(connection, acquire: true, cancellationToken);
            }
            catch
            {
                // При неоднозначном сетевом исходе сервер мог получить lock до отмены клиента.
                // Текущая сессия должна быть физически закрыта, а не возвращена в pool.
                NpgsqlConnection.ClearPool(connection);
                throw;
            }
            try
            {
                await operation(db, cancellationToken);
            }
            finally
            {
                await SetMigrationLockAsync(connection, acquire: false, CancellationToken.None);
            }
        }
        finally
        {
            if (closeWhenFinished) await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task MigrateAndSeedAsync(ProxyHarborDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.MigrateAsync(cancellationToken);
        var legacyUrls = new HashSet<string>(StringComparer.Ordinal)
        {
            "https://cdn.jsdelivr.net/gh/proxifly/free-proxy-list@main/proxies/protocols/http/data.txt",
            "https://cdn.jsdelivr.net/gh/proxifly/free-proxy-list@main/proxies/protocols/socks4/data.txt",
            "https://cdn.jsdelivr.net/gh/proxifly/free-proxy-list@main/proxies/protocols/socks5/data.txt",
            "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt",
            "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks4.txt",
            "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt",
            "https://raw.githubusercontent.com/iplocate/free-proxy-list/main/protocols/http.txt",
            "https://raw.githubusercontent.com/iplocate/free-proxy-list/main/protocols/socks5.txt"
        };
        // Uri.AbsoluteUri уже канонизирует scheme/host, но path и query остаются
        // регистрозависимыми: /Feed и /feed могут быть разными HTTPS-ресурсами.
        var existing = await db.Sources.ToDictionaryAsync(x => x.Url, StringComparer.Ordinal, cancellationToken);

        // Удаляем только URL из первоначальной встроенной версии, заменённые каноническими feed'ами.
        db.Sources.RemoveRange(existing.Values.Where(x => legacyUrls.Contains(x.Url)));

        foreach (var definition in BuiltInSourceCatalog.Sources)
        {
            if (existing.TryGetValue(definition.Url, out var source))
            {
                source.Name = definition.Name;
                source.DefaultProtocol = definition.Protocol;
                source.Priority = definition.Rank * 10;
                continue;
            }

            db.Sources.Add(new ProxySource
            {
                Name = definition.Name,
                Url = definition.Url,
                DefaultProtocol = definition.Protocol,
                Priority = definition.Rank * 10
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SetMigrationLockAsync(
        NpgsqlConnection connection,
        bool acquire,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            acquire ? "SELECT pg_advisory_lock(@key)" : "SELECT pg_advisory_unlock(@key)",
            connection);
        command.Parameters.AddWithValue("key", MigrationLockKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!acquire && result is not true)
        {
            // Не возвращаем в pool сессию, для которой освобождение lock не подтверждено.
            NpgsqlConnection.ClearPool(connection);
            throw new InvalidOperationException("PostgreSQL не подтвердил освобождение migration lock.");
        }
    }
}
