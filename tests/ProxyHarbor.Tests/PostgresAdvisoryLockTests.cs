using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет реальную cluster-wide семантику advisory lock при наличии integration PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class PostgresAdvisoryLockTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OnlyOneConnectionOwnsOperationLockAndReleaseMakesItAvailable()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>().UseNpgsql(connectionString).Options;
        var factory = new TestDbFactory(options);

        await using (var first = await PostgresAdvisoryLock.TryAcquireAsync(
            factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None))
        {
            Assert.NotNull(first);
            var second = await PostgresAdvisoryLock.TryAcquireAsync(
                factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None);
            Assert.Null(second);
        }

        await using var afterRelease = await PostgresAdvisoryLock.TryAcquireAsync(
            factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None);
        Assert.NotNull(afterRelease);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SharedCatalogMutationsExcludeCollectionButNotEachOther()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>().UseNpgsql(connectionString).Options;
        var factory = new TestDbFactory(options);

        {
            await using var firstMutation = await PostgresAdvisoryLock.TryAcquireSharedAsync(
                factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None);
            await using var secondMutation = await PostgresAdvisoryLock.TryAcquireSharedAsync(
                factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None);
            Assert.NotNull(firstMutation);
            Assert.NotNull(secondMutation);

            var blockedCollection = await PostgresAdvisoryLock.TryAcquireAsync(
                factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None);
            Assert.Null(blockedCollection);
        }

        await using var collection = await PostgresAdvisoryLock.TryAcquireAsync(
            factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None);
        Assert.NotNull(collection);
        var blockedMutation = await PostgresAdvisoryLock.TryAcquireSharedAsync(
            factory, PostgresAdvisoryLock.CollectionKey, CancellationToken.None);
        Assert.Null(blockedMutation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ApiAndOperationLeasesExcludeRestoreAndReleaseRestoresAccess()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>().UseNpgsql(connectionString).Options;
        var factory = new TestDbFactory(options);

        {
            await using var firstApi = await DatabaseRuntimeGate.TryAcquireApiLeaseAsync(
                connectionString, CancellationToken.None);
            await using var secondApi = await DatabaseRuntimeGate.TryAcquireApiLeaseAsync(
                connectionString, CancellationToken.None);
            Assert.NotNull(firstApi);
            Assert.NotNull(secondApi);

            var blockedRestore = await DatabaseRuntimeGate.TryAcquireRestoreLeaseAsync(
                connectionString, CancellationToken.None);
            Assert.Null(blockedRestore);
        }

        await using (var operation = await DatabaseRuntimeGate.TryAcquireOperationLeaseAsync(
            factory, CancellationToken.None))
        {
            Assert.NotNull(operation);
            var blockedRestore = await DatabaseRuntimeGate.TryAcquireRestoreLeaseAsync(
                connectionString, CancellationToken.None);
            Assert.Null(blockedRestore);
        }

        await using var restore = await DatabaseRuntimeGate.TryAcquireRestoreLeaseAsync(
            connectionString, CancellationToken.None);
        Assert.NotNull(restore);
        var blockedApi = await DatabaseRuntimeGate.TryAcquireApiLeaseAsync(
            connectionString, CancellationToken.None);
        Assert.Null(blockedApi);
        var blockedOperation = await DatabaseRuntimeGate.TryAcquireOperationLeaseAsync(
            factory, CancellationToken.None);
        Assert.Null(blockedOperation);
        var sourceMutation = await new SourceCatalogMutationCoordinator(factory)
            .TryAcquireAsync(CancellationToken.None);
        Assert.Null(sourceMutation);
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
