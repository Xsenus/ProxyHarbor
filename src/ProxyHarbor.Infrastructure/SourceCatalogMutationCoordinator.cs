using Microsoft.EntityFrameworkCore;

namespace ProxyHarbor.Infrastructure;

/// <summary>Cluster-wide барьер между изменением source-каталога и collection snapshot.</summary>
public interface ISourceCatalogMutationCoordinator
{
    /// <summary>Возвращает shared lease либо <see langword="null"/>, когда collection уже владеет exclusive-lock.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken token);
}

/// <summary>
/// Shared-lock разрешает параллельные CRUD-запросы, но не позволяет exclusive
/// collection-run пересечься с их транзакционным изменением каталога.
/// </summary>
public sealed class SourceCatalogMutationCoordinator(
    IDbContextFactory<ProxyHarborDbContext> dbFactory) : ISourceCatalogMutationCoordinator
{
    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken token)
    {
        var databaseLease = await DatabaseRuntimeGate.TryAcquireOperationLeaseAsync(dbFactory, token);
        if (databaseLease is null) return null;
        try
        {
            var catalogLease = await PostgresAdvisoryLock.TryAcquireSharedAsync(
                dbFactory, PostgresAdvisoryLock.CollectionKey, token);
            if (catalogLease is null)
            {
                await databaseLease.DisposeAsync();
                return null;
            }
            return new CompositeLease(catalogLease, databaseLease);
        }
        catch
        {
            await databaseLease.DisposeAsync();
            throw;
        }
    }

    /// <summary>Освобождает locks в порядке, обратном их получению.</summary>
    private sealed class CompositeLease(
        IAsyncDisposable catalogLease,
        IAsyncDisposable databaseLease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await catalogLease.DisposeAsync(); }
            finally { await databaseLease.DisposeAsync(); }
        }
    }
}
