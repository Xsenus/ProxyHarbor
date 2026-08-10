using System.Data;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>
/// Выполняет несколько буферизованных API SQL-чтений в одном REPEATABLE READ snapshot.
/// В отличие от streaming export, весь ответ ещё можно безопасно повторить при transient failure,
/// поэтому пользовательская транзакция обязательно обёрнута в настроенную EF execution strategy.
/// </summary>
internal static class BufferedReadSnapshot
{
    internal static async Task<TResult> ExecuteAsync<TResult>(
        ProxyHarborDbContext db,
        Func<CancellationToken, Task<TResult>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        cancellationToken.ThrowIfCancellationRequested();

        // InMemory provider используется быстрыми unit-тестами и не поддерживает транзакции.
        if (!db.Database.IsRelational())
            return await read(cancellationToken);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);
            var result = await read(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }
}
