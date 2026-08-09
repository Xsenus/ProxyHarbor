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

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
