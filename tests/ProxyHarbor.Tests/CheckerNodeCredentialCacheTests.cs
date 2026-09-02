using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class CheckerNodeCredentialCacheTests
{
    private const string FirstToken = "first-checker-token-1234567890-abcdefghijklmno";
    private const string SecondToken = "second-checker-token-1234567890-abcdefghijklmn";

    [Fact]
    public async Task MalformedOrAmbiguousHeadersAreRejectedBeforeAnyDatabaseRead()
    {
        var factory = new CountingFactory(Options());
        using var cache = new CheckerNodeCredentialCache(factory, new ManualTimeProvider());
        var nodeId = Guid.NewGuid().ToString();
        var cases = new[]
        {
            (Node: new StringValues([nodeId, nodeId]), Authorization: new StringValues($"Bearer {FirstToken}")),
            (Node: new StringValues(nodeId), Authorization: new StringValues([$"Bearer {FirstToken}", $"Bearer {FirstToken}"])),
            (Node: new StringValues(nodeId), Authorization: new StringValues("Bearer short"))
        };

        foreach (var headers in cases)
        {
            var controller = new CheckerAgentController(
                cache, null!, NullLogger<CheckerAgentController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.Request.Headers["X-Checker-Node"] = headers.Node;
            controller.Request.Headers.Authorization = headers.Authorization;

            Assert.IsType<UnauthorizedResult>(await controller.Heartbeat(new CheckerHeartbeatRequest("test"), default));
        }

        Assert.Equal(0, factory.Created);
        Assert.Equal(0, cache.AuthenticationAttempts);
    }

    [Fact]
    public async Task ConcurrentRequestsForAllNodesShareOneEnabledCredentialSnapshot()
    {
        var options = Options();
        var enabled = Node("enabled", FirstToken);
        var disabled = Node("disabled", SecondToken, enabled: false);
        await SeedAsync(options, enabled, disabled);
        var factory = new CountingFactory(options);
        using var cache = new CheckerNodeCredentialCache(factory, new ManualTimeProvider());

        var accepted = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => cache.AuthenticateAsync(enabled.Id, FirstToken, default).AsTask()));

        Assert.All(accepted, Assert.True);
        Assert.False(await cache.AuthenticateAsync(enabled.Id, SecondToken, default));
        Assert.False(await cache.AuthenticateAsync(disabled.Id, SecondToken, default));
        Assert.False(await cache.AuthenticateAsync(Guid.NewGuid(), FirstToken, default));
        Assert.Equal(1, factory.Created);
        Assert.Equal(1, cache.DatabaseReads);
        Assert.Equal(35, cache.AuthenticationAttempts);
        Assert.Equal(3, cache.AuthenticationFailures);
        Assert.Equal(34, cache.SnapshotHits);
    }

    [Fact]
    public async Task ExplicitInvalidationMakesRotationAndRevocationImmediate()
    {
        var options = Options();
        var node = Node("rotated", FirstToken);
        await SeedAsync(options, node);
        var factory = new CountingFactory(options);
        using var cache = new CheckerNodeCredentialCache(factory, new ManualTimeProvider());
        Assert.True(await cache.AuthenticateAsync(node.Id, FirstToken, default));

        await using (var update = new ProxyHarborDbContext(options))
        {
            var persisted = await update.CheckerNodes.SingleAsync(item => item.Id == node.Id);
            persisted.TokenHash = Hash(SecondToken);
            persisted.Enabled = false;
            await update.SaveChangesAsync();
        }
        cache.Invalidate();

        Assert.False(await cache.AuthenticateAsync(node.Id, FirstToken, default));
        Assert.False(await cache.AuthenticateAsync(node.Id, SecondToken, default));
        Assert.Equal(2, factory.Created);
        Assert.Equal(2, cache.DatabaseReads);
        Assert.Equal(1, cache.Invalidations);
    }

    [Fact]
    public async Task ExpiredSnapshotRefreshesAndNeverFallsBackToStaleCredentials()
    {
        var options = Options();
        var node = Node("fail-closed", FirstToken);
        await SeedAsync(options, node);
        var clock = new ManualTimeProvider();
        var factory = new FailAfterFirstFactory(options);
        using var cache = new CheckerNodeCredentialCache(factory, clock);
        Assert.True(await cache.AuthenticateAsync(node.Id, FirstToken, default));

        clock.Advance(CheckerNodeCredentialCache.MaximumAge + TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cache.AuthenticateAsync(node.Id, FirstToken, default));
        Assert.Equal(2, factory.Attempts);
        Assert.Equal(1, cache.DatabaseReads);
    }

    [Fact]
    public async Task InvalidationDuringRefreshDiscardsCrossGenerationSnapshot()
    {
        var options = Options();
        var node = Node("generation", FirstToken);
        await SeedAsync(options, node);
        var factory = new BlockingFirstFactory(options);
        using var cache = new CheckerNodeCredentialCache(factory, new ManualTimeProvider());

        var authentication = cache.AuthenticateAsync(node.Id, SecondToken, default).AsTask();
        await factory.FirstCreateStarted;
        await using (var update = new ProxyHarborDbContext(options))
        {
            var persisted = await update.CheckerNodes.SingleAsync(item => item.Id == node.Id);
            persisted.TokenHash = Hash(SecondToken);
            await update.SaveChangesAsync();
        }
        cache.Invalidate();
        factory.ReleaseFirstCreate();

        Assert.True(await authentication);
        Assert.Equal(2, factory.Created);
        Assert.Equal(2, cache.DatabaseReads);
    }

    private static DbContextOptions<ProxyHarborDbContext> Options() =>
        new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"checker-credentials-{Guid.NewGuid():N}").Options;

    private static CheckerNode Node(string name, string token, bool enabled = true) => new()
    {
        Name = name,
        Host = "203.0.113.10",
        SshUsername = "root",
        TokenHash = Hash(token),
        Enabled = enabled
    };

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static async Task SeedAsync(
        DbContextOptions<ProxyHarborDbContext> options,
        params CheckerNode[] nodes)
    {
        await using var seed = new ProxyHarborDbContext(options);
        seed.CheckerNodes.AddRange(nodes);
        await seed.SaveChangesAsync();
    }

    private sealed class CountingFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        private int created;
        internal int Created => Volatile.Read(ref created);
        public ProxyHarborDbContext CreateDbContext() =>
            throw new NotSupportedException("Use the async factory path.");
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref created);
            return Task.FromResult(new ProxyHarborDbContext(options));
        }
    }

    private sealed class FailAfterFirstFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        private int attempts;
        internal int Attempts => Volatile.Read(ref attempts);
        public ProxyHarborDbContext CreateDbContext() =>
            throw new NotSupportedException("Use the async factory path.");
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromResult(new ProxyHarborDbContext(options))
                : Task.FromException<ProxyHarborDbContext>(new InvalidOperationException("database unavailable"));
    }

    private sealed class BlockingFirstFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        private readonly TaskCompletionSource firstCreateStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstCreate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int created;
        internal Task FirstCreateStarted => firstCreateStarted.Task;
        internal int Created => Volatile.Read(ref created);
        internal void ReleaseFirstCreate() => releaseFirstCreate.TrySetResult();
        public ProxyHarborDbContext CreateDbContext() =>
            throw new NotSupportedException("Use the async factory path.");
        public async Task<ProxyHarborDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref created);
            if (attempt == 1)
            {
                firstCreateStarted.TrySetResult();
                await releaseFirstCreate.Task.WaitAsync(cancellationToken);
            }
            return new ProxyHarborDbContext(options);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan duration) =>
            Interlocked.Add(ref timestamp, (long)Math.Ceiling(duration.TotalSeconds * TimestampFrequency));
    }
}
