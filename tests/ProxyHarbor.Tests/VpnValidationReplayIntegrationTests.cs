using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class VpnValidationReplayIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "PostgresIntegration")]
    public Task ReplayedOrOlderResultsAreAcknowledgedWithoutWriting(bool older, bool deferred) =>
        WithDatabaseAsync(async (factory, service, endpoint, now) =>
        {
            var update = new VpnValidationUpdate(endpoint.Id, VpnEndpointStatus.Reachable, 12, null,
                now, now.AddMinutes(5), deferred);
            Assert.Equal(1, await service.PersistValidationResultsAsync([update]));
            var before = await ReadAsync(factory, endpoint.Id);
            if (older) update = update with { CheckedAt = now.AddSeconds(-1), NextCheckAt = now.AddMinutes(1),
                Status = VpnEndpointStatus.Unreachable, LatencyMs = null, Error = "stale", IsDeferred = !deferred };
            Assert.Equal(1, await service.PersistValidationResultsAsync([update]));
            var after = await ReadAsync(factory, endpoint.Id);
            Assert.Equal(before, after);
        });

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public Task MissingEndpointRollsBackTheWholeBatch() =>
        WithDatabaseAsync(async (factory, service, endpoint, now) =>
        {
            var before = await ReadAsync(factory, endpoint.Id);
            var update = new VpnValidationUpdate(endpoint.Id, VpnEndpointStatus.Reachable, 12, null, now, now.AddMinutes(5));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                var saved = await service.PersistValidationResultsAsync([update, update with { Id = Guid.NewGuid() }]);
                VpnCatalogService.EnsureCompletePersistence(saved, 2);
            });
            Assert.Equal(before, await ReadAsync(factory, endpoint.Id));
        });

    private static async Task<Row> ReadAsync(Factory factory, Guid id)
    {
        await using var db = factory.CreateDbContext();
        return await db.Database.SqlQuery<Row>($"""
            SELECT xmin::text AS "Version", "Status", "LatencyMs", "LastError", "LastCheckedAt",
                   "LastValidationAttemptAt", "LastValidationDeferred", "NextCheckAt", "SuccessfulChecks", "FailedChecks"
            FROM "VpnEndpoints" WHERE "Id" = {id}
            """).SingleAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgresIntegration")]
    public Task LegacyVerifiedTimestampAlsoFencesOlderAttempts(bool hasOlderAttempt) =>
        WithDatabaseAsync(async (factory, service, endpoint, now) =>
        {
            await using (var db = factory.CreateDbContext())
                await db.VpnEndpoints.Where(row => row.Id == endpoint.Id).ExecuteUpdateAsync(set => set
                    .SetProperty(row => row.LastCheckedAt, now.AddMinutes(1))
                    .SetProperty(row => row.LastValidationAttemptAt, hasOlderAttempt ? now.AddMinutes(-1) : (DateTimeOffset?)null));
            var before = await ReadAsync(factory, endpoint.Id);
            Assert.Equal(1, await service.PersistValidationResultsAsync(
                [new(endpoint.Id, VpnEndpointStatus.Reachable, 15, null, now, now.AddMinutes(5))]));
            Assert.Equal(before, await ReadAsync(factory, endpoint.Id));
        });

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public Task ConcurrentDuplicatesCountOnlyOnce() =>
        WithDatabaseAsync(async (factory, service, endpoint, now) =>
        {
            var update = new VpnValidationUpdate(endpoint.Id, VpnEndpointStatus.Reachable, 12, null, now, now.AddMinutes(5));
            var acknowledgements = await Task.WhenAll(Enumerable.Range(0, 6)
                .Select(_ => service.PersistValidationResultsAsync([update])));
            Assert.All(acknowledgements, count => Assert.Equal(1, count));
            var after = await ReadAsync(factory, endpoint.Id);
            Assert.Equal(4, after.SuccessfulChecks);
            Assert.Equal(2, after.FailedChecks);
        });

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public Task MixedFreshAndReplayedBatchAcknowledgesBoth() =>
        WithDatabaseAsync(async (factory, service, endpoint, now) =>
        {
            var other = new VpnEndpoint { Host = "9.9.9.9", Port = 443, Protocol = VpnProtocol.Trojan };
            await using (var db = factory.CreateDbContext())
            {
                db.VpnEndpoints.Add(other);
                await db.SaveChangesAsync();
            }
            var update = new VpnValidationUpdate(endpoint.Id, VpnEndpointStatus.Reachable, 12, null, now, now.AddMinutes(5));
            Assert.Equal(1, await service.PersistValidationResultsAsync([update]));
            var before = await ReadAsync(factory, endpoint.Id);
            Assert.Equal(2, await service.PersistValidationResultsAsync([update, update with { Id = other.Id }]));
            Assert.Equal(before, await ReadAsync(factory, endpoint.Id));
            Assert.Equal(1, (await ReadAsync(factory, other.Id)).SuccessfulChecks);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgresIntegration")]
    public Task LostCommitAcknowledgementRetriesWithoutDoubleCounting(bool newerWriteBeforeRetry) =>
        WithDatabaseAsync(async (factory, service, endpoint, now) =>
        {
            var update = new VpnValidationUpdate(endpoint.Id, VpnEndpointStatus.Reachable, 12, null, now, now.AddMinutes(5));
            var calls = 0;
            Row? committed = null;
            Assert.Equal(1, await service.PersistValidationResultsAsync([update], afterCommit: async () =>
            {
                if (++calls != 1) return;
                if (newerWriteBeforeRetry)
                    await service.PersistValidationResultsAsync([update with { CheckedAt = now.AddSeconds(1),
                        Status = VpnEndpointStatus.Unreachable, LatencyMs = null, Error = "newer", NextCheckAt = now.AddMinutes(30) }]);
                committed = await ReadAsync(factory, endpoint.Id);
                throw new NpgsqlException("Simulated lost commit acknowledgement", new IOException("transport lost"));
            }));
            Assert.Equal(2, calls);
            Assert.NotNull(committed);
            Assert.Equal(4, committed.SuccessfulChecks);
            Assert.Equal(newerWriteBeforeRetry ? 3 : 2, committed.FailedChecks);
            Assert.Equal(committed, await ReadAsync(factory, endpoint.Id));
        });

    private static async Task WithDatabaseAsync(Func<Factory, VpnCatalogService, VpnEndpoint, DateTimeOffset, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schema = $"vpn_replay_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var factory = new Factory(new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString,
                    pg => pg.EnableRetryOnFailure(2, TimeSpan.FromMilliseconds(10), null)).Options);
            var now = new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero).AddTicks(7);
            var endpoint = new VpnEndpoint { Host = "8.8.8.8", Port = 443, Protocol = VpnProtocol.Trojan,
                FirstSeenAt = now.AddDays(-1), LastSeenAt = now.AddDays(-1), SuccessfulChecks = 3, FailedChecks = 2 };
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                db.VpnEndpoints.Add(endpoint);
                await db.SaveChangesAsync();
            }
            var service = new VpnCatalogService(factory, new UnusedClients(), Options.Create(new CollectorOptions()),
                NullLogger<VpnCatalogService>.Instance);
            await test(factory, service, endpoint, now);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class Factory(DbContextOptions<ProxyHarborDbContext> options) : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
    private sealed class UnusedClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Persistence must not use HTTP.");
    }
    private sealed record Row(string Version, VpnEndpointStatus Status, int? LatencyMs, string? LastError,
        DateTimeOffset? LastCheckedAt, DateTimeOffset? LastValidationAttemptAt, bool LastValidationDeferred,
        DateTimeOffset? NextCheckAt, int SuccessfulChecks, int FailedChecks);
}
