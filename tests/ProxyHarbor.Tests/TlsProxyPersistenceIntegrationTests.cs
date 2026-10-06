using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
        Assert.Equal(protocol, (await verify.Proxies.SingleAsync()).Protocol);
        Assert.Equal(protocol, (await verify.Sources.SingleAsync()).DefaultProtocol);
        Assert.Equal(protocol, (await verify.ProxySourceImportStates.SingleAsync()).SourceProtocol);
        Assert.Equal((int)protocol, (await verify.SourceApiCaptureStates.SingleAsync()).SourceProtocol);
        Assert.Empty(await verify.Database.GetPendingMigrationsAsync());
    }
}
