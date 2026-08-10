using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует provider fallback и caller cancellation общего read-snapshot helper.</summary>
public sealed class BufferedReadSnapshotTests
{
    [Fact]
    public async Task NonRelationalProviderExecutesReadExactlyOnceWithoutTransaction()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"buffered-read-{Guid.NewGuid():N}").Options;
        await using var db = new ProxyHarborDbContext(options);
        var invocations = 0;

        var result = await BufferedReadSnapshot.ExecuteAsync(db, _ =>
        {
            invocations++;
            return Task.FromResult(42);
        }, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task PreCancelledCallerNeverStartsRead()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"buffered-read-cancel-{Guid.NewGuid():N}").Options;
        await using var db = new ProxyHarborDbContext(options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoked = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BufferedReadSnapshot.ExecuteAsync(db, _ =>
            {
                invoked = true;
                return Task.FromResult(0);
            }, cancellation.Token));

        Assert.False(invoked);
    }
}
