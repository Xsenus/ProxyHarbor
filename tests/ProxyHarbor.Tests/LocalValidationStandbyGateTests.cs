using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Fixes failover and caching semantics of the built-in validator reserve mode.</summary>
public sealed class LocalValidationStandbyGateTests
{
    [Fact]
    public async Task HealthyExternalCapacityMovesLocalValidatorToStandby()
    {
        var clock = new ManualTimeProvider();
        var factory = CreateFactory();
        await AddNodeAsync(factory, clock.GetUtcNow(), enabled: true, status: "online", concurrency: 80);
        using var gate = CreateGate(factory, clock, localConcurrency: 80);

        Assert.True(await gate.ShouldStandByAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false, "online", 200, 0)]
    [InlineData(true, "failed", 200, 0)]
    [InlineData(true, "online", 79, 0)]
    [InlineData(true, "online", 200, -121)]
    public async Task UnavailableOrInsufficientNodesKeepLocalValidatorRunning(
        bool enabled,
        string status,
        int concurrency,
        int heartbeatAgeSeconds)
    {
        var clock = new ManualTimeProvider();
        var factory = CreateFactory();
        var heartbeat = clock.GetUtcNow().AddSeconds(heartbeatAgeSeconds);
        await AddNodeAsync(factory, heartbeat, enabled, status, concurrency);
        using var gate = CreateGate(factory, clock, localConcurrency: 80);

        Assert.False(await gate.ShouldStandByAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CachedStandbyDecisionExpiresAndDetectsLostHeartbeat()
    {
        var clock = new ManualTimeProvider();
        var factory = CreateFactory();
        await AddNodeAsync(factory, clock.GetUtcNow(), enabled: true, status: "online", concurrency: 200);
        using var gate = CreateGate(factory, clock, localConcurrency: 80);

        Assert.True(await gate.ShouldStandByAsync(CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var node = await db.CheckerNodes.SingleAsync();
            node.LastHeartbeatAt = clock.GetUtcNow().AddMinutes(-10);
            await db.SaveChangesAsync();
        }

        Assert.True(await gate.ShouldStandByAsync(CancellationToken.None));
        clock.Advance(LocalValidationStandbyGate.SnapshotLifetime.Add(TimeSpan.FromMilliseconds(1)));
        Assert.False(await gate.ShouldStandByAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DatabaseFailureFailsOpenAndRetriesSoonerThanHealthySnapshot()
    {
        var clock = new ManualTimeProvider();
        var factory = new ThrowingFactory();
        using var gate = CreateGate(factory, clock, localConcurrency: 80);

        Assert.False(await gate.ShouldStandByAsync(CancellationToken.None));
        Assert.Equal(1, factory.Attempts);
        clock.Advance(LocalValidationStandbyGate.FailureRetryDelay.Subtract(TimeSpan.FromMilliseconds(1)));
        Assert.False(await gate.ShouldStandByAsync(CancellationToken.None));
        Assert.Equal(1, factory.Attempts);
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert.False(await gate.ShouldStandByAsync(CancellationToken.None));
        Assert.Equal(2, factory.Attempts);
    }

    private static LocalValidationStandbyGate CreateGate(
        IDbContextFactory<ProxyHarborDbContext> factory,
        TimeProvider clock,
        int localConcurrency) =>
        new(
            factory,
            Options.Create(new CollectorOptions { ValidationConcurrency = localConcurrency }),
            NullLogger<LocalValidationStandbyGate>.Instance,
            clock);

    private static Factory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"local-validation-standby-{Guid.NewGuid():N}")
            .Options;
        return new Factory(options);
    }

    private static async Task AddNodeAsync(
        IDbContextFactory<ProxyHarborDbContext> factory,
        DateTimeOffset heartbeat,
        bool enabled,
        string status,
        int concurrency)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CheckerNodes.Add(new CheckerNode
        {
            Name = Guid.NewGuid().ToString("N"),
            Host = "203.0.113.10",
            SshUsername = "root",
            TokenHash = new byte[32],
            Enabled = enabled,
            DeploymentStatus = status,
            LastHeartbeatAt = heartbeat,
            Concurrency = concurrency
        });
        await db.SaveChangesAsync();
    }

    private sealed class Factory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ThrowingFactory : IDbContextFactory<ProxyHarborDbContext>
    {
        public int Attempts { get; private set; }
        public ProxyHarborDbContext CreateDbContext() =>
            throw new NotSupportedException("Use the asynchronous factory path.");
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Task.FromException<ProxyHarborDbContext>(new InvalidOperationException("database unavailable"));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
