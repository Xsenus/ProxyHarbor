using Microsoft.EntityFrameworkCore;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Проверяет не только соединение с PostgreSQL, но и минимальный актуальный контракт
/// всех рабочих таблиц. LIMIT 0 заставляет PostgreSQL разрешить таблицы, колонки и
/// read-permissions, не читая пользовательские строки и не создавая нагрузки от health probe.
/// </summary>
public sealed class DatabaseReadinessProbe(IDbContextFactory<ProxyHarborDbContext> dbFactory)
{
    /// <summary>Возвращает false при недоступной или несовместимой схеме; отмену вызывающего не скрывает.</summary>
    public async Task<bool> CheckAsync(CancellationToken token)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            await db.Database.ExecuteSqlRawAsync("""
                SELECT
                    proxy."LastValidationDeferred",
                    proxy."FirstAliveAt",
                    proxy."LastAliveAt",
                    proxy."CurrentAliveSince",
                    source."LastContentFetchedAt",
                    run."CandidateLimitReached",
                    validation."LeaseId",
                    backup."SentToTelegram",
                    backup."SentToObjectStorage"
                FROM "Proxies" AS proxy
                CROSS JOIN "Sources" AS source
                CROSS JOIN "Runs" AS run
                CROSS JOIN "ValidationRuns" AS validation
                CROSS JOIN "BackupRuns" AS backup
                LIMIT 0;
                """, token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
