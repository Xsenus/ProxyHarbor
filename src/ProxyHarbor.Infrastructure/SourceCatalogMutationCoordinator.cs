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
    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken token) =>
        await PostgresAdvisoryLock.TryAcquireSharedAsync(
            dbFactory, PostgresAdvisoryLock.CollectionKey, token);
}
