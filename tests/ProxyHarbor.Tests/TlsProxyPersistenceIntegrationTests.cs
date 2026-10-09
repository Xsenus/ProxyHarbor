using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Checks real PostgreSQL constraints and non-destructive rollback for TLS proxy profiles.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class TlsProxyPersistenceIntegrationTests
{
    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task LocalReserveRemainsActiveUntilHealthyTlsCapacityCanDrainDueWork()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var now = DateTimeOffset.UtcNow;
        var node = new CheckerNode
        {
            Name = "legacy-reserve",
            Host = "8.8.8.8",
            SshUsername = "root",
            TokenHash = SHA256.HashData(Guid.NewGuid().ToByteArray()),
            Enabled = true,
            DeploymentStatus = "online",
            LastHeartbeatAt = now,
            Concurrency = 80
        };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.CheckerNodes.Add(node);
            db.Proxies.Add(new ProxyEndpoint { Host = "1.1.1.1", Port = 443, Protocol = ProxyProtocol.HttpTls });
            await db.SaveChangesAsync();
        }
        using (var gate = new LocalValidationStandbyGate(database.Factory,
                   Options.Create(new CollectorOptions { ValidationConcurrency = 80 }),
                   NullLogger<LocalValidationStandbyGate>.Instance, TimeProvider.System))
            Assert.False(await gate.ShouldStandByAsync(CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
        {
            db.ProxyValidationLeases.Add(new ProxyValidationLease
            {
                ProxyId = (await db.Proxies.SingleAsync()).Id,
                LeaseId = Guid.NewGuid(),
                LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(10)
            });
            await db.SaveChangesAsync();
        }
        using (var leasedGate = new LocalValidationStandbyGate(database.Factory,
                   Options.Create(new CollectorOptions { ValidationConcurrency = 80 }),
                   NullLogger<LocalValidationStandbyGate>.Instance, TimeProvider.System))
            Assert.True(await leasedGate.ShouldStandByAsync(CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
        {
            var lease = await db.ProxyValidationLeases.SingleAsync();
            lease.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        using (var expiredGate = new LocalValidationStandbyGate(database.Factory,
                   Options.Create(new CollectorOptions { ValidationConcurrency = 80 }),
                   NullLogger<LocalValidationStandbyGate>.Instance, TimeProvider.System))
            Assert.False(await expiredGate.ShouldStandByAsync(CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
        {
            var existing = await db.CheckerNodes.SingleAsync();
            existing.SupportsTlsProxyTransport = true;
            await db.SaveChangesAsync();
        }
        using var capableGate = new LocalValidationStandbyGate(database.Factory,
            Options.Create(new CollectorOptions { ValidationConcurrency = 80 }),
            NullLogger<LocalValidationStandbyGate>.Instance, TimeProvider.System);
        Assert.True(await capableGate.ShouldStandByAsync(CancellationToken.None));
    }

    [Fact, Trait("Category", "PostgresIntegration")]
    public async Task LegacyAgentCannotLeaseTlsRowsOrSuppressWorkForCapableAgent()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var legacy = new CheckerNode
        {
            Name = "legacy",
            Host = "8.8.8.8",
            SshUsername = "root",
            TokenHash = SHA256.HashData(Guid.NewGuid().ToByteArray())
        };
        var capable = new CheckerNode
        {
            Name = "capable",
            Host = "1.1.1.1",
            SshUsername = "root",
            TokenHash = SHA256.HashData(Guid.NewGuid().ToByteArray())
        };
        await using (var db = database.Factory.CreateDbContext())
        {
            db.CheckerNodes.AddRange(legacy, capable);
            db.Proxies.AddRange(
                new ProxyEndpoint { Host = "8.8.8.8", Port = 443, Protocol = ProxyProtocol.HttpTls },
                new ProxyEndpoint { Host = "1.1.1.1", Port = 443, Protocol = ProxyProtocol.HttpTlsUnverified });
            await db.SaveChangesAsync();
        }
        var gate = new ValidationClaimIdleGate();
        var service = new DistributedProxyValidationService(database.Factory, Options.Create(new CollectorOptions()), gate);
        await service.TouchAsync(legacy.Id, new CheckerHeartbeatRequest("legacy"), null, default);
        Assert.Null(await service.ClaimAsync(legacy.Id, default));
        Assert.False(gate.CooldownActive);
        await using (var db = database.Factory.CreateDbContext())
            Assert.Empty(await db.ProxyValidationLeases.ToArrayAsync());
        await service.TouchAsync(capable.Id, new CheckerHeartbeatRequest("tls", SupportsTlsProxyTransport: true), null, default);
        var lease = Assert.IsType<CheckerLeaseResponse>(await service.ClaimAsync(capable.Id, default));
        Assert.Equal(2, lease.Items.Count);
        Assert.Contains(lease.Items, item => item.Protocol == ProxyProtocol.HttpTls);
        Assert.Contains(lease.Items, item => item.Protocol == ProxyProtocol.HttpTlsUnverified);
        await using var verify = database.Factory.CreateDbContext();
        Assert.False((await verify.CheckerNodes.SingleAsync(node => node.Id == legacy.Id)).SupportsTlsProxyTransport);
        Assert.True((await verify.CheckerNodes.SingleAsync(node => node.Id == capable.Id)).SupportsTlsProxyTransport);
    }

    [Theory, Trait("Category", "PostgresIntegration")]
    [InlineData(ProxyProtocol.HttpTls)]
    [InlineData(ProxyProtocol.HttpTlsUnverified)]
    public async Task MigrationAcceptsEndpointsSourceSnapshotsAndCapturesAndRejectsDestructiveRollback(ProxyProtocol protocol)
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await database.AddSourceAsync("tls-profile");
        source.DefaultProtocol = protocol;
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Sources.Update(source);
            db.Proxies.Add(new ProxyEndpoint { Host = "8.8.8.8", Port = 443, Protocol = protocol });
            var payload = new byte[8];
            db.SourceApiCaptureStates.Add(new SourceApiCaptureState
            {
                ProxySourceId = source.Id,
                SourceUrl = source.Url,
                SourceProtocol = (int)protocol,
                Payload = payload,
                PayloadHash = SHA256.HashData(payload),
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var store = new ProxySourceImportStore(database.Factory);
        var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:443", protocol);
        Assert.NotNull(await store.BeginAsync(source, snapshot, CancellationToken.None));

        await using (var db = database.Factory.CreateDbContext())
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>()
                .MigrateAsync("20261006170423_AddMtProtoProtocol"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        await using var verify = database.Factory.CreateDbContext();
        // EF commits completed newer steps before the protected TLS rollback fails.
        // Verify exactly which steps were undone, then recover the complete current model.
        var migrations = verify.Database.GetMigrations().ToArray();
        var protectedIndex = Array.IndexOf(migrations, "20261006193647_AddTlsProxyTransports");
        Assert.True(protectedIndex >= 0);
        Assert.Equal(migrations[(protectedIndex + 1)..], await verify.Database.GetPendingMigrationsAsync());
        await verify.Database.MigrateAsync();
        Assert.Equal(protocol, (await verify.Proxies.SingleAsync()).Protocol);
        Assert.Equal(protocol, (await verify.Sources.SingleAsync()).DefaultProtocol);
        Assert.Equal(protocol, (await verify.ProxySourceImportStates.SingleAsync()).SourceProtocol);
        Assert.Equal((int)protocol, (await verify.SourceApiCaptureStates.SingleAsync()).SourceProtocol);
        Assert.Empty(await verify.Database.GetPendingMigrationsAsync());
    }
}
