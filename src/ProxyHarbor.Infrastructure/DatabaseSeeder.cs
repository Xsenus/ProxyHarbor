using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Применяет миграции и синхронизирует встроенный каталог, не удаляя пользовательские источники.</summary>
public static class DatabaseSeeder
{
    internal const long MigrationLockKey = 0x5052484D49475203;
    internal const string StartupCleanupFailureDataKey = "ProxyHarbor.DatabaseSeeder.StartupCleanupFailure";
    private static readonly TimeSpan MigrationLockPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly IReadOnlyDictionary<string, string> CanonicalSourceUrlReplacements =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://raw.githubusercontent.com/TheSpeedX/PROXY-List/refs/heads/master/http.txt"] =
                "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt",
            ["https://raw.githubusercontent.com/TheSpeedX/PROXY-List/refs/heads/master/socks4.txt"] =
                "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks4.txt",
            ["https://raw.githubusercontent.com/TheSpeedX/PROXY-List/refs/heads/master/socks5.txt"] =
                "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt",
            ["https://raw.githubusercontent.com/databay-labs/free-proxy-list/refs/heads/master/http.txt"] =
                "https://raw.githubusercontent.com/databay-labs/free-proxy-list/master/http.txt",
            ["https://raw.githubusercontent.com/databay-labs/free-proxy-list/refs/heads/master/socks4.txt"] =
                "https://raw.githubusercontent.com/databay-labs/free-proxy-list/master/socks4.txt",
            ["https://raw.githubusercontent.com/databay-labs/free-proxy-list/refs/heads/master/socks5.txt"] =
                "https://raw.githubusercontent.com/databay-labs/free-proxy-list/master/socks5.txt",
            ["https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/bt/data.txt"] =
                "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/lv/data.txt",
            ["https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/lt/data.txt"] =
                "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/lu/data.txt",
            ["https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/gq/data.txt"] =
                "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/th/data.txt",
            ["https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/dk/data.txt"] =
                "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/tr/data.txt",
            ["https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/MostStable/socks4.txt"] =
                "https://raw.githubusercontent.com/proxygenerator1/ProxyGenerator/main/ForSites/cloudflare.com/socks4.txt",
            ["https://raw.githubusercontent.com/fyvri/fresh-proxy-list/archive/storage/classic/http.txt"] =
                "https://raw.githubusercontent.com/fyvri/fresh-proxy-list/refs/heads/archive/storage/classic/http.txt"
        };
    private static readonly IReadOnlyDictionary<string, string> CanonicalVpnSourceUrlReplacements =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://www.vpngate.net/api/iphone/"] =
                "https://raw.githubusercontent.com/9xN/auto-ovpn/main/json/data.json",
            ["https://raw.githubusercontent.com/9xN/auto-ovpn/main/configs/server_0_JP.ovpn"] =
                "https://raw.githubusercontent.com/9xN/auto-ovpn/main/json/data.json"
        };

    /// <summary>Добавляет недостающие feed'ы и обновляет их метаданные, сохраняя выбор Enabled/Disabled.</summary>
    public static Task InitializeAsync(ProxyHarborDbContext db, CancellationToken cancellationToken = default) =>
        ExecuteWithMigrationLockAsync(db, MigrateAndSeedAsync, hooks: null, cancellationToken);

    /// <summary>Внутренний overload с lifecycle hooks для детерминированных failure-canary.</summary>
    internal static Task InitializeAsync(
        ProxyHarborDbContext db,
        DatabaseSeederExecutionHooks hooks,
        CancellationToken cancellationToken = default) =>
        ExecuteWithMigrationLockAsync(db, MigrateAndSeedAsync, hooks, cancellationToken);

    /// <summary>Применяет только DDL migrations под общей startup-блокировкой, не изменяя строки приложения.</summary>
    public static Task MigrateSchemaAsync(ProxyHarborDbContext db, CancellationToken cancellationToken = default) =>
        ExecuteWithMigrationLockAsync(
            db,
            static (context, token) => context.Database.MigrateAsync(token),
            hooks: null,
            cancellationToken);

    private static async Task ExecuteWithMigrationLockAsync(
        ProxyHarborDbContext db,
        Func<ProxyHarborDbContext, CancellationToken, Task> operation,
        DatabaseSeederExecutionHooks? hooks,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var closeWhenFinished = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenFinished) await db.Database.OpenConnectionAsync(cancellationToken);
        var lockAcquired = false;
        Exception? primaryFailure = null;
        try
        {
            // EF защищает отдельные migration-команды транзакциями, но две одновременно стартующие
            // реплики всё равно могут увидеть один набор pending migrations. Сессионная блокировка
            // сериализует и миграции, и последующий idempotent seed для всех реплик общей БД.
            await SetMigrationLockAsync(connection, acquire: true, cancellationToken);
            lockAcquired = true;
            hooks?.AfterMigrationLockAcquired?.Invoke();
            await operation(db, cancellationToken);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (!lockAcquired)
            {
                // При неоднозначном сетевом исходе сервер мог получить lock до отмены клиента.
                // Текущая сессия должна быть физически закрыта, а не возвращена в pool.
                NpgsqlConnection.ClearPool(connection);
            }
        }

        Exception? releaseFailure = null;
        if (lockAcquired)
        {
            try
            {
                hooks?.BeforeMigrationLockRelease?.Invoke();
                await SetMigrationLockAsync(connection, acquire: false, CancellationToken.None);
            }
            catch (Exception exception)
            {
                releaseFailure = exception;
                // Не возвращаем потенциального владельца session lock в общий pool.
                NpgsqlConnection.ClearPool(connection);
                if (primaryFailure is not null)
                    AddCleanupFailure(primaryFailure, "unlock", exception);
            }
        }

        Exception? closeFailure = null;
        var discardSession = (!lockAcquired && primaryFailure is not null) || releaseFailure is not null;
        if (closeWhenFinished || discardSession)
        {
            try
            {
                hooks?.BeforeConnectionClose?.Invoke();
                if (discardSession)
                    await connection.DisposeAsync();
                else
                    await db.Database.CloseConnectionAsync();
            }
            catch (Exception exception)
            {
                closeFailure = exception;
                NpgsqlConnection.ClearPool(connection);
                var failureToPreserve = primaryFailure ?? releaseFailure ?? exception;
                if (failureToPreserve != exception)
                    AddCleanupFailure(failureToPreserve, "close", exception);
                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception disposeFailure)
                {
                    AddCleanupFailure(failureToPreserve, "dispose", disposeFailure);
                }
            }
        }

        var failure = primaryFailure ?? releaseFailure ?? closeFailure;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Добавляет bounded cleanup-диагностику, никогда не заменяя primary exception.</summary>
    private static void AddCleanupFailure(Exception primaryFailure, string stage, Exception cleanupFailure)
    {
        try
        {
            var detail = $"{stage}: {cleanupFailure.GetType().Name}";
            primaryFailure.Data[StartupCleanupFailureDataKey] =
                primaryFailure.Data[StartupCleanupFailureDataKey] is string previous
                    ? $"{previous} | {detail}"
                    : detail;
        }
        catch (Exception)
        {
            // Нестандартное read-only Exception.Data не может скрыть primary startup failure.
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
            "https://raw.githubusercontent.com/iplocate/free-proxy-list/main/protocols/http.txt",
            "https://raw.githubusercontent.com/iplocate/free-proxy-list/main/protocols/socks5.txt",
            "https://raw.githubusercontent.com/cyberh4ck3r/free-proxy-list/main/http-proxies.txt",
            "https://raw.githubusercontent.com/stormsia/proxy-list/main/http.txt",
            "https://raw.githubusercontent.com/zloi-user/hideip.me/main/http.txt",
            "https://raw.githubusercontent.com/zloi-user/hideip.me/main/https.txt",
            "https://raw.githubusercontent.com/zloi-user/hideip.me/main/socks4.txt",
            "https://raw.githubusercontent.com/zloi-user/hideip.me/main/socks5.txt",
            "https://raw.githubusercontent.com/CB-X2-Jun/proxy-lists/main/proxy.txt",
            "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/cr/data.txt",
            "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/cu/data.txt",
            "https://raw.githubusercontent.com/xyzs996/free-proxy-health-list/main/proxies/countries/gr/data.txt"
        };
        // Uri.AbsoluteUri уже канонизирует scheme/host, но path и query остаются
        // регистрозависимыми: /Feed и /feed могут быть разными HTTPS-ресурсами.
        var existingSources = await db.Sources.ToListAsync(cancellationToken);

        // raw.githubusercontent.com принимает обе формы пути неравномерно: URL с
        // /refs/heads/ может начать возвращать 400 только для отдельных файлов.
        // Переносим строку на устойчивый canonical URL, сохраняя Id, Enabled и историю.
        foreach (var (replacedUrl, canonicalUrl) in CanonicalSourceUrlReplacements)
        {
            var replaced = existingSources.SingleOrDefault(source => source.Url == replacedUrl);
            if (replaced is null) continue;

            var canonical = existingSources.SingleOrDefault(source => source.Url == canonicalUrl);
            if (canonical is not null)
            {
                db.Sources.Remove(replaced);
                existingSources.Remove(replaced);
                continue;
            }

            replaced.Url = canonicalUrl;
            // Валидаторы относятся к прежнему resource URL. Сбрасываем только состояние
            // HTTP-повтора, чтобы новый адрес был опрошен сразу и без условных заголовков.
            replaced.HttpETag = null;
            replaced.HttpLastModifiedAt = null;
            replaced.ConsecutiveFailures = 0;
            replaced.NextFetchAt = null;
            replaced.LastError = null;
        }

        // Удаляем только явно перечисленные бывшие встроенные URL: заменённые каноническими
        // feed'ами либо подтверждённо недоступные. Пользовательские источники не затрагиваются.
        var legacySources = existingSources.Where(source => legacyUrls.Contains(source.Url)).ToArray();
        db.Sources.RemoveRange(legacySources);
        var existing = existingSources
            .Except(legacySources)
            .ToDictionary(source => source.Url, StringComparer.Ordinal);

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

        var existingVpnSourcesList = await db.VpnSources.ToListAsync(cancellationToken);
        foreach (var (replacedUrl, canonicalUrl) in CanonicalVpnSourceUrlReplacements)
        {
            var replaced = existingVpnSourcesList.SingleOrDefault(source => source.Url == replacedUrl);
            if (replaced is null) continue;

            var canonical = existingVpnSourcesList.SingleOrDefault(source => source.Url == canonicalUrl);
            if (canonical is not null)
            {
                db.VpnSources.Remove(replaced);
                existingVpnSourcesList.Remove(replaced);
                continue;
            }

            replaced.Url = canonicalUrl;
            replaced.LastFetchedAt = null;
            replaced.LastSucceededAt = null;
            replaced.LastContentFetchedAt = null;
            replaced.NextFetchAt = null;
            replaced.HttpETag = null;
            replaced.HttpLastModifiedAt = null;
            replaced.LastItemCount = 0;
            replaced.ConsecutiveFailures = 0;
            replaced.LastError = null;
        }

        var existingVpnSources = existingVpnSourcesList.ToDictionary(x => x.Url, StringComparer.Ordinal);
        for (var index = 0; index < BuiltInVpnSourceCatalog.Sources.Count; index++)
        {
            var definition = BuiltInVpnSourceCatalog.Sources[index];
            if (existingVpnSources.TryGetValue(definition.Url, out var source))
            {
                source.Name = definition.Name;
                source.Provider = definition.Provider;
                source.DefaultProtocol = definition.Protocol;
                source.License = definition.License;
                source.Priority = (index + 1) * 10;
                continue;
            }
            db.VpnSources.Add(new VpnSource
            {
                Name = definition.Name,
                Provider = definition.Provider,
                Url = definition.Url,
                DefaultProtocol = definition.Protocol,
                License = definition.License,
                Priority = (index + 1) * 10
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SetMigrationLockAsync(
        NpgsqlConnection connection,
        bool acquire,
        CancellationToken cancellationToken)
    {
        // Блокирующий pg_advisory_lock держит активный statement snapshot, пока ждёт
        // другую реплику. CREATE INDEX CONCURRENTLY обязан дождаться такого snapshot,
        // что образует цикл: index ждёт waiter, waiter ждёт владельца advisory lock.
        // Короткий try-lock polling завершает statement между попытками и разрывает цикл.
        await using var command = new NpgsqlCommand(
            acquire ? "SELECT pg_try_advisory_lock(@key)" : "SELECT pg_advisory_unlock(@key)",
            connection);
        command.Parameters.AddWithValue("key", MigrationLockKey);
        while (true)
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (acquire)
            {
                if (result is true) return;
                await Task.Delay(MigrationLockPollInterval, cancellationToken);
                continue;
            }

            if (result is not true)
            {
                // Не возвращаем в pool сессию, для которой освобождение lock не подтверждено.
                NpgsqlConnection.ClearPool(connection);
                throw new InvalidOperationException("PostgreSQL не подтвердил освобождение migration lock.");
            }
            return;
        }
    }
}

/// <summary>
/// Внутренние точки наблюдения startup lifecycle. Production-код их не передаёт; тесты
/// используют hooks только для воспроизводимых unlock/close failure-сценариев.
/// </summary>
internal sealed record DatabaseSeederExecutionHooks(
    Action? AfterMigrationLockAcquired = null,
    Action? BeforeMigrationLockRelease = null,
    Action? BeforeConnectionClose = null);
