using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ProxyCountryGeolocationTests
{
    [Fact]
    public void ResolverSafelyHandlesMissingDatabaseAndInvalidAddresses()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"proxyharbor-missing-{Guid.NewGuid():N}.mmdb");
        using var resolver = new ProxyCountryResolver(Options.Create(new GeoIpOptions { DatabasePath = missingPath }));

        Assert.False(resolver.Reload());
        Assert.Null(resolver.Resolve(null));
        Assert.Null(resolver.Resolve("not-an-ip"));
        Assert.Null(resolver.Resolve("8.8.8.8"));
    }

    [Fact]
    public async Task BoundedCopyCopiesAllowedContentAndRejectsOversizedContent()
    {
        await using var allowedInput = new MemoryStream([1, 2, 3]);
        await using var output = new MemoryStream();
        await ProxyCountryWorker.CopyBoundedAsync(allowedInput, output, 3, CancellationToken.None);
        Assert.Equal([1, 2, 3], output.ToArray());

        await using var oversizedInput = new MemoryStream([1, 2, 3, 4]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProxyCountryWorker.CopyBoundedAsync(oversizedInput, Stream.Null, 3, CancellationToken.None));
    }

    [Fact]
    public void CountryUpdateProjectionNormalizesAndSkipsUnknownOrUnchangedValues()
    {
        var changed = Guid.NewGuid();
        var unchanged = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var updates = ProxyCountryWorker.BuildCountryUpdates(
        [
            new CountryCandidate(changed, "8.8.8.8", null),
            new CountryCandidate(unchanged, "1.1.1.1", "AU"),
            new CountryCandidate(unknown, "invalid", null)
        ], address => address switch
        {
            "8.8.8.8" => "us",
            "1.1.1.1" => "AU",
            _ => null
        });

        var update = Assert.Single(updates);
        Assert.Equal(changed, update.Id);
        Assert.Equal("US", update.CountryCode);
    }

    [Fact]
    public async Task CountryBulkUpdateRejectsInvalidOrDuplicateInputBeforeOpeningDatabase()
    {
        Assert.Equal(0, await ProxyCountryWorker.PersistCountryUpdatesAsync(
            null!, CountryCatalog.Proxy, []));
        await Assert.ThrowsAsync<ArgumentException>(() => ProxyCountryWorker.PersistCountryUpdatesAsync(
            null!, CountryCatalog.Proxy, [new CountryCodeUpdate(Guid.NewGuid(), null)]));
        await Assert.ThrowsAsync<ArgumentException>(() => ProxyCountryWorker.PersistCountryUpdatesAsync(
            null!, CountryCatalog.Proxy, [new CountryCodeUpdate(Guid.NewGuid(), "uS")]));
        await Assert.ThrowsAsync<ArgumentException>(() => ProxyCountryWorker.PersistCountryUpdatesAsync(
            null!, CountryCatalog.Proxy, [new CountryCodeUpdate(Guid.NewGuid(), "USA")]));
        await Assert.ThrowsAsync<ArgumentException>(() => ProxyCountryWorker.PersistCountryUpdatesAsync(
            null!, CountryCatalog.Proxy, [new CountryCodeUpdate(Guid.NewGuid(), "U1")]));
        var duplicate = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => ProxyCountryWorker.PersistCountryUpdatesAsync(
            null!, CountryCatalog.Proxy,
            [new CountryCodeUpdate(duplicate, "US"), new CountryCodeUpdate(duplicate, "AU")]));
    }

    [Fact]
    public void CountryTableMappingIsClosedToTheTwoSupportedCatalogs()
    {
        Assert.Equal("Proxies", ProxyCountryWorker.CountryTableName(CountryCatalog.Proxy));
        Assert.Equal("VpnEndpoints", ProxyCountryWorker.CountryTableName(CountryCatalog.Vpn));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProxyCountryWorker.CountryTableName((CountryCatalog)int.MaxValue));
    }

    [Fact]
    public async Task PostgreSqlCountryBulkUpdateSkipsLockedRowsAndRetriesThemLater()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"geoip_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
                .Options;
            var factory = new TestDbFactory(dbOptions);
            var checkedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var lockedProxy = new ProxyEndpoint
            {
                Host = "8.8.8.8",
                Port = 80,
                Status = ProxyStatus.Alive,
                LastCheckedAt = checkedAt,
                LatencyMs = 10,
                SuccessfulChecks = 1
            };
            var freeProxy = new ProxyEndpoint
            {
                Host = "1.1.1.1",
                Port = 80,
                Status = ProxyStatus.Alive,
                LastCheckedAt = checkedAt,
                LatencyMs = 10,
                SuccessfulChecks = 1
            };
            var vpn = new VpnEndpoint { Host = "9.9.9.9", Port = 443, Protocol = VpnProtocol.Trojan };
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                seed.Proxies.AddRange(lockedProxy, freeProxy);
                seed.VpnEndpoints.Add(vpn);
                await seed.SaveChangesAsync();
            }

            await using var locker = new NpgsqlConnection(builder.ConnectionString);
            await locker.OpenAsync();
            await using var lockTransaction = await locker.BeginTransactionAsync();
            await using (var acquire = new NpgsqlCommand(
                "SELECT 1 FROM \"Proxies\" WHERE \"Id\" = @id FOR UPDATE", locker, lockTransaction))
            {
                acquire.Parameters.AddWithValue("id", lockedProxy.Id);
                await acquire.ExecuteScalarAsync();
            }

            var firstPass = await ProxyCountryWorker.PersistCountryUpdatesAsync(factory, CountryCatalog.Proxy,
            [
                new CountryCodeUpdate(lockedProxy.Id, "US"),
                new CountryCodeUpdate(freeProxy.Id, "AU")
            ]);
            Assert.Equal(1, firstPass);
            await lockTransaction.CommitAsync();

            var retry = await ProxyCountryWorker.PersistCountryUpdatesAsync(factory, CountryCatalog.Proxy,
                [new CountryCodeUpdate(lockedProxy.Id, "US")]);
            var vpnUpdated = await ProxyCountryWorker.PersistCountryUpdatesAsync(factory, CountryCatalog.Vpn,
                [new CountryCodeUpdate(vpn.Id, "US")]);
            Assert.Equal(1, retry);
            Assert.Equal(1, vpnUpdated);

            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal("US", (await verify.Proxies.SingleAsync(x => x.Id == lockedProxy.Id)).CountryCode);
            Assert.Equal("AU", (await verify.Proxies.SingleAsync(x => x.Id == freeProxy.Id)).CountryCode);
            Assert.Equal("US", (await verify.VpnEndpoints.SingleAsync(x => x.Id == vpn.Id)).CountryCode);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
