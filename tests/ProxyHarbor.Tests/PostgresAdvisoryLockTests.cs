using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет реальную cluster-wide семантику advisory lock при наличии integration PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class PostgresAdvisoryLockTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CleanupFailuresPreserveAcquireErrorAndLeaseDisposalRemainsNonThrowing()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        const long acquisitionKey = 0x5052485445535406;
        const long releaseKey = 0x5052485445535407;

        var primaryFailure = new InvalidOperationException("Deterministic acquire failure.");
        var cleanupFailuresBeforeAcquire = DatabaseRuntimeGate.AdvisoryLockCleanupFailures;
        var acquireFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresAdvisoryLock.TryAcquireCoreAsync(
                connectionString,
                acquisitionKey,
                shared: false,
                CancellationToken.None,
                new PostgresAdvisoryLockExecutionHooks(
                    AfterLockAcquired: () => throw primaryFailure,
                    BeforeConnectionDispose: () =>
                        throw new IOException("Deterministic acquisition cleanup failure."))));

        Assert.Same(primaryFailure, acquireFailure);
        Assert.Equal(
            "dispose: IOException",
            acquireFailure.Data[PostgresAdvisoryLock.CleanupFailureDataKey]);
        Assert.Equal(cleanupFailuresBeforeAcquire + 1, DatabaseRuntimeGate.AdvisoryLockCleanupFailures);
        await AssertExclusiveLockAvailableAsync(connectionString, acquisitionKey);

        var observedCleanup = new List<string>();
        var lease = await PostgresAdvisoryLock.TryAcquireCoreAsync(
            connectionString,
            releaseKey,
            shared: false,
            CancellationToken.None,
            new PostgresAdvisoryLockExecutionHooks(
                BeforeUnlock: () => throw new IOException("Deterministic unlock failure."),
                BeforeConnectionDispose: () => throw new IOException("Deterministic dispose failure."),
                CleanupFailureObserved: (stage, failure) =>
                    observedCleanup.Add($"{stage}: {failure.GetType().Name}")));
        Assert.NotNull(lease);

        var cleanupFailuresBefore = DatabaseRuntimeGate.AdvisoryLockCleanupFailures;
        var disposeFailure = await Record.ExceptionAsync(async () => await lease.DisposeAsync());
        Assert.Null(disposeFailure);
        Assert.Equal(cleanupFailuresBefore + 1, DatabaseRuntimeGate.AdvisoryLockCleanupFailures);
        Assert.Equal(["unlock: IOException", "dispose: IOException"], observedCleanup);
        // Повторный Dispose идемпотентен и не публикует вторую диагностику.
        await lease.DisposeAsync();
        Assert.Equal(2, observedCleanup.Count);
        await AssertExclusiveLockAvailableAsync(connectionString, releaseKey);
    }

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

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ApiLeaseHeartbeatDetectsTerminatedOwningBackend()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var apiLease = await DatabaseRuntimeGate.TryAcquireApiLeaseAsync(
            connectionString, CancellationToken.None);
        Assert.NotNull(apiLease);
        await apiLease.VerifyAsync(CancellationToken.None);

        await using (var killer = new NpgsqlConnection(connectionString))
        {
            await killer.OpenAsync();
            await using var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(@process_id)", killer);
            terminate.Parameters.AddWithValue("process_id", apiLease.BackendProcessId);
            Assert.Equal(true, await terminate.ExecuteScalarAsync());
        }

        await Assert.ThrowsAnyAsync<Exception>(() => apiLease.VerifyAsync(CancellationToken.None));
        await using var restore = await DatabaseRuntimeGate.TryAcquireRestoreLeaseAsync(
            connectionString, CancellationToken.None);
        Assert.NotNull(restore);
    }

    private static async Task AssertExclusiveLockAvailableAsync(string connectionString, long key)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        acquire.Parameters.AddWithValue("key", key);
        Assert.True((bool)(await acquire.ExecuteScalarAsync() ?? false));
        await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        release.Parameters.AddWithValue("key", key);
        Assert.True((bool)(await release.ExecuteScalarAsync() ?? false));
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
