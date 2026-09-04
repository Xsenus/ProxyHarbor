using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет ownership, возврат потерянной партии и завершение внешним узлом на PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class DistributedProxyValidationIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SharedQueueClaimPreservesPriorityAndSkipsFutureOrLeasedRows()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_queue_claim_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var claimShape = new ValidationClaimShapeInterceptor();
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .AddInterceptors(claimShape)
                .Options;
            var now = DateTimeOffset.UtcNow;
            var aliveNeverChecked = Endpoint("198.51.100.1", ProxyStatus.Alive, null);
            var aliveDue = Endpoint("198.51.100.2", ProxyStatus.Alive, now.AddMinutes(-2));
            var aliveFuture = Endpoint("198.51.100.3", ProxyStatus.Alive, now.AddMinutes(2));
            var pendingDue = Endpoint("198.51.100.4", ProxyStatus.Pending, now.AddMinutes(-5));
            var deadDue = Endpoint("198.51.100.5", ProxyStatus.Dead, now.AddHours(-1));
            var leasedDead = Endpoint("198.51.100.6", ProxyStatus.Dead, now.AddHours(-2));

            await using (var migrate = new ProxyHarborDbContext(dbOptions))
            {
                await migrate.Database.MigrateAsync();
                migrate.Proxies.AddRange(
                    aliveNeverChecked, aliveDue, aliveFuture, pendingDue, deadDue, leasedDead);
                migrate.ProxyValidationLeases.Add(new ProxyValidationLease
                {
                    ProxyId = leasedDead.Id,
                    LeaseId = Guid.NewGuid(),
                    LeaseUntil = now.AddMinutes(2)
                });
                await migrate.SaveChangesAsync();
            }

            await using var claimDb = new ProxyHarborDbContext(dbOptions);
            await using var transaction = await claimDb.Database.BeginTransactionAsync();
            var leaseId = Guid.NewGuid();
            var leaseUntil = now.AddMinutes(2);
            var serializedIdleGate = new ValidationClaimIdleGate();
            serializedIdleGate.MarkEmpty();
            var coalescedLeaseId = Guid.NewGuid();
            var coalesced = await ValidationQueueClaim.ClaimAndLeaseAsync(
                claimDb, 3, now, leaseUntil, coalescedLeaseId, serializedIdleGate, CancellationToken.None);
            Assert.Empty(coalesced);
            Assert.Equal(1, serializedIdleGate.CoalescedClaims);
            Assert.False(await claimDb.ProxyValidationLeases.AnyAsync(x => x.LeaseId == coalescedLeaseId));
            Assert.Single(await claimDb.ProxyValidationLeases.ToArrayAsync());
            serializedIdleGate.MarkWorkAvailable();

            var claimed = await ValidationQueueClaim.ClaimAndLeaseAsync(
                claimDb, 3, now, leaseUntil, leaseId, serializedIdleGate, CancellationToken.None);

            Assert.Equal(
                [aliveNeverChecked.Id, aliveDue.Id, pendingDue.Id],
                claimed.Select(proxy => proxy.Id));
            Assert.DoesNotContain(claimed, proxy => proxy.Id == aliveFuture.Id);
            Assert.DoesNotContain(claimed, proxy => proxy.Id == deadDue.Id);
            Assert.DoesNotContain(claimed, proxy => proxy.Id == leasedDead.Id);
            Assert.All(claimed, proxy => Assert.Null(proxy.PreviousLeaseId));
            Assert.False(serializedIdleGate.CooldownActive);
            Assert.Equal(3, await claimDb.ProxyValidationLeases.CountAsync(lease =>
                lease.LeaseId == leaseId && lease.LeaseUntil == leaseUntil));
            Assert.NotEmpty(claimShape.Commands);
            Assert.Contains(claimShape.Commands, command => command.Contains(
                "ORDER BY proxy.\"NextCheckAt\" NULLS FIRST, proxy.\"LastCheckedAt\" NULLS FIRST",
                StringComparison.Ordinal));
            Assert.All(claimShape.Commands, command =>
            {
                Assert.Contains("INSERT INTO \"ProxyValidationLeases\"", command, StringComparison.Ordinal);
                Assert.DoesNotContain("UPDATE \"Proxies\"", command, StringComparison.Ordinal);
                Assert.Contains("candidate.\"Id\", candidate.\"Host\", candidate.\"Port\"", command,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("SELECT *", command, StringComparison.OrdinalIgnoreCase);
            });
            await transaction.RollbackAsync();

            await using var underfilledDb = new ProxyHarborDbContext(dbOptions);
            await using var underfilledTransaction = await underfilledDb.Database.BeginTransactionAsync();
            var underfilledGate = new ValidationClaimIdleGate();
            var underfilled = await ValidationQueueClaim.ClaimAndLeaseAsync(
                underfilledDb, 10, now, leaseUntil, Guid.NewGuid(), underfilledGate, CancellationToken.None);
            Assert.Equal(4, underfilled.Count);
            Assert.True(underfilledGate.CooldownActive);

            var commandsAfterDrain = claimShape.CommandCount;
            var repeated = await ValidationQueueClaim.ClaimAndLeaseAsync(
                underfilledDb, 10, now, leaseUntil, Guid.NewGuid(), underfilledGate, CancellationToken.None);
            Assert.Empty(repeated);
            Assert.Equal(commandsAfterDrain, claimShape.CommandCount);
            await underfilledTransaction.RollbackAsync();
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task NodesReceiveDisjointLeasesAndExpiredBatchIsReclaimed()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_distributed_validation_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                // Production enables retry-on-failure. The distributed completion owns
                // an explicit transaction and therefore must execute inside that strategy.
                .UseNpgsql(connection.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var firstNode = Node("first", batchSize: 6);
            var secondNode = Node("second", batchSize: 6);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.CheckerNodes.AddRange(firstNode, secondNode);
                seed.Proxies.AddRange(Enumerable.Range(1, 20).Select(index => new ProxyEndpoint
                {
                    Host = $"198.51.100.{index}",
                    Port = 8000 + index,
                    Protocol = ProxyProtocol.Http,
                    Status = index <= 4 ? ProxyStatus.Alive : index <= 12 ? ProxyStatus.Pending : ProxyStatus.Dead,
                    LastCheckedAt = index <= 4 || index > 12 ? DateTimeOffset.UtcNow : null,
                    LatencyMs = index <= 4 ? 50 + index : null,
                    SuccessfulChecks = index <= 4 ? 1 : 0,
                    FailedChecks = index > 12 ? 1 : 0,
                    ConsecutiveFailedChecks = index > 12 ? 1 : 0,
                    // NULL входит в отдельный первый index-range и не должен
                    // пересечься с явно просроченной частью очереди.
                    NextCheckAt = index == 1 ? null : DateTimeOffset.UtcNow.AddMinutes(-1)
                }));
                await seed.SaveChangesAsync();
            }

            var settings = new CollectorOptions { ProbeTimeoutSeconds = 5 };
            var dispatcher = new DistributedProxyValidationService(
                factory, Options.Create(settings), new ValidationClaimIdleGate());

            var claims = await Task.WhenAll(
                dispatcher.ClaimAsync(firstNode.Id, CancellationToken.None),
                dispatcher.ClaimAsync(secondNode.Id, CancellationToken.None));
            Assert.All(claims, claim => Assert.NotNull(claim));
            Assert.Equal(6, claims[0]!.Items.Count);
            Assert.Equal(6, claims[1]!.Items.Count);
            Assert.Empty(claims[0]!.Items.Select(x => x.Id).Intersect(claims[1]!.Items.Select(x => x.Id)));
            await using (var priorityCheck = await factory.CreateDbContextAsync())
            {
                var aliveIds = await priorityCheck.Proxies.AsNoTracking()
                    .Where(x => x.Status == ProxyStatus.Alive).Select(x => x.Id).ToArrayAsync();
                var deadIds = await priorityCheck.Proxies.AsNoTracking()
                    .Where(x => x.Status == ProxyStatus.Dead).Select(x => x.Id).ToArrayAsync();
                var claimedIds = claims.SelectMany(claim => claim!.Items).Select(item => item.Id).ToHashSet();
                Assert.All(aliveIds, id => Assert.Contains(id, claimedIds));
                Assert.DoesNotContain(deadIds, id => claimedIds.Contains(id));
            }

            // Второй узел штатно завершает свою партию нейтральными результатами.
            var secondResult = new CheckerLeaseResultRequest(claims[1]!.Items.Select(item =>
                new CheckerProxyResult(item.Id, false, null, null, false, "control unavailable", true)).ToArray());
            factory.ResetCreateCount();
            var completed = await dispatcher.CompleteAsync(
                secondNode.Id, claims[1]!.LeaseId, secondResult, CancellationToken.None);
            Assert.Equal(6, completed.Deferred);
            Assert.Equal(2, factory.CreateCount);

            // Первый VPS «пропадает». Имитируем истечение TTL и убеждаемся, что его
            // пакет получает второй узел, а незавершённый аудит явно помечается failed.
            await using (var expire = await factory.CreateDbContextAsync())
            {
                var past = DateTimeOffset.UtcNow.AddSeconds(-1);
                await expire.CheckerNodes.Where(x => x.Id == firstNode.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CurrentLeaseUntil, past));
                await expire.ProxyValidationLeases.Where(x => x.LeaseId == claims[0]!.LeaseId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseUntil, past));
            }

            var reclaimed = await dispatcher.ClaimAsync(secondNode.Id, CancellationToken.None);
            Assert.NotNull(reclaimed);
            Assert.Equal(claims[0]!.Items.Select(x => x.Id).Order(), reclaimed!.Items.Select(x => x.Id).Order());

            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal("failed", await verify.ValidationRuns
                .Where(x => x.LeaseId == claims[0]!.LeaseId).Select(x => x.Status).SingleAsync());
            Assert.Equal("completed", await verify.ValidationRuns
                .Where(x => x.LeaseId == claims[1]!.LeaseId).Select(x => x.Status).SingleAsync());
            Assert.Equal(6, await verify.CheckerNodes.Where(x => x.Id == secondNode.Id)
                .Select(x => x.CompletedChecks).SingleAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CompletionReplayAcknowledgesCommittedBatchWithoutTouchingNewLease()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_completion_replay_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connection.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options;
            var factory = new TestDbFactory(dbOptions);
            var node = Node("first", batchSize: 3);
            var otherNode = Node("second", batchSize: 3);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                seed.CheckerNodes.AddRange(node, otherNode);
                seed.Proxies.AddRange(Enumerable.Range(1, 6).Select(index =>
                    Endpoint($"198.51.100.{index}", ProxyStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-1))));
                await seed.SaveChangesAsync();
            }

            var dispatcher = new DistributedProxyValidationService(
                factory, Options.Create(new CollectorOptions { ProbeTimeoutSeconds = 5 }), new ValidationClaimIdleGate());
            var lease = Assert.IsType<CheckerLeaseResponse>(await dispatcher.ClaimAsync(node.Id, CancellationToken.None));
            var request = new CheckerLeaseResultRequest(lease.Items.Select((item, index) =>
                new CheckerProxyResult(item.Id, index == 0, index == 0 ? 25 : null,
                    null, false, index == 0 ? null : "test probe", IsDeferred: index == 2)).ToArray());

            // Simulate a committed response lost in transit: two uploads race,
            // then the client retransmits again after the first reply is gone.
            var replies = await Task.WhenAll(
                dispatcher.CompleteAsync(node.Id, lease.LeaseId, request, CancellationToken.None),
                dispatcher.CompleteAsync(node.Id, lease.LeaseId, request, CancellationToken.None));
            Assert.All(replies, reply => Assert.Equal(new CheckerLeaseCompletion(2, 1, 1), reply));
            Assert.Equal(replies[0], await dispatcher.CompleteAsync(node.Id, lease.LeaseId, request, CancellationToken.None));

            var next = Assert.IsType<CheckerLeaseResponse>(await dispatcher.ClaimAsync(node.Id, CancellationToken.None));
            Assert.NotEqual(lease.LeaseId, next.LeaseId);
            Assert.Equal(replies[0], await dispatcher.CompleteAsync(node.Id, lease.LeaseId, request, CancellationToken.None));
            // A lease ID is an immutable completion key. A changed replay cannot
            // overwrite the first committed results or contribute more counters.
            var changed = new CheckerLeaseResultRequest(request.Results.Select(result => result with
            {
                IsAlive = true, LatencyMs = 1, IsDeferred = false, Error = null
            }).ToArray());
            Assert.Equal(replies[0], await dispatcher.CompleteAsync(node.Id, lease.LeaseId, changed, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                dispatcher.CompleteAsync(otherNode.Id, lease.LeaseId, request, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                dispatcher.CompleteAsync(node.Id, Guid.NewGuid(), request, CancellationToken.None));

            await using var verify = await factory.CreateDbContextAsync();
            var saved = await verify.CheckerNodes.AsNoTracking().SingleAsync(x => x.Id == node.Id);
            Assert.Equal(3, saved.CompletedChecks);
            Assert.Equal(1, saved.AliveChecks);
            Assert.Equal(next.LeaseId, saved.CurrentLeaseId);
            Assert.Equal(next.LeaseUntil, saved.CurrentLeaseUntil);
            Assert.Equal(3, await verify.ProxyValidationLeases.CountAsync(x => x.LeaseId == next.LeaseId));
            Assert.False(await verify.ProxyValidationLeases.AnyAsync(x => x.LeaseId == lease.LeaseId));
            Assert.Equal(1, await verify.Proxies.SumAsync(x => x.SuccessfulChecks));
            Assert.Equal(1, await verify.Proxies.SumAsync(x => x.FailedChecks));
            Assert.Equal(1, await verify.ValidationRuns.CountAsync(x => x.Status == "completed"));

            // Actual expired, uncommitted work must still be rejected.
            await verify.CheckerNodes.Where(x => x.Id == node.Id).ExecuteUpdateAsync(setters =>
                setters.SetProperty(x => x.CurrentLeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                dispatcher.CompleteAsync(node.Id, next.LeaseId, request, CancellationToken.None));
            await verify.CheckerNodes.Where(x => x.Id == node.Id).ExecuteUpdateAsync(setters =>
                setters.SetProperty(x => x.Enabled, false));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                dispatcher.CompleteAsync(node.Id, lease.LeaseId, request, CancellationToken.None));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CompletionFailureRollsBackProxyRunAndNodeInOneTransaction()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_completion_atomic_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var failure = new CompletionFailureInterceptor();
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connection.ConnectionString, postgres => postgres.EnableRetryOnFailure())
                .AddInterceptors(failure)
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var node = Node("atomic", batchSize: 1);
            var proxy = Endpoint("198.51.100.220", ProxyStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-1));
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.AddRange(node, proxy);
                await seed.SaveChangesAsync();
            }

            var settings = new CollectorOptions { ProbeTimeoutSeconds = 5 };
            var dispatcher = new DistributedProxyValidationService(
                factory, Options.Create(settings), new ValidationClaimIdleGate());

            var claim = Assert.IsType<CheckerLeaseResponse>(
                await dispatcher.ClaimAsync(node.Id, CancellationToken.None));
            var claimed = Assert.Single(claim.Items);
            var request = new CheckerLeaseResultRequest([
                new CheckerProxyResult(claimed.Id, true, 25, "8.8.8.8", true, null, false)
            ]);
            failure.Armed = true;

            await Assert.ThrowsAsync<InjectedCompletionFailure>(() => dispatcher.CompleteAsync(
                node.Id, claim.LeaseId, request, CancellationToken.None));

            await using var verify = await factory.CreateDbContextAsync();
            var savedProxy = await verify.Proxies.AsNoTracking().SingleAsync(x => x.Id == proxy.Id);
            var savedRun = await verify.ValidationRuns.AsNoTracking().SingleAsync(x => x.LeaseId == claim.LeaseId);
            var savedNode = await verify.CheckerNodes.AsNoTracking().SingleAsync(x => x.Id == node.Id);
            Assert.Equal(ProxyStatus.Pending, savedProxy.Status);
            Assert.Equal(0, savedProxy.SuccessfulChecks);
            Assert.True(await verify.ProxyValidationLeases.AnyAsync(lease =>
                lease.ProxyId == savedProxy.Id && lease.LeaseId == claim.LeaseId));
            Assert.Null(savedProxy.LastValidationAttemptAt);
            Assert.Equal("running", savedRun.Status);
            Assert.Null(savedRun.FinishedAt);
            Assert.Equal(claim.LeaseId, savedNode.CurrentLeaseId);
            Assert.Equal(0, savedNode.CompletedChecks);
            Assert.Equal(0, savedNode.AliveChecks);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task EmptyClaimsAreCoalescedForTwoSecondsThenNewWorkIsClaimed()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_idle_claim_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connection.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var probingNode = Node("idle-probe", batchSize: 1);
            probingNode.Host = "203.0.113.20";
            var coalescedNode = Node("idle-coalesced", batchSize: 1);
            coalescedNode.Host = "203.0.113.21";
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.CheckerNodes.AddRange(probingNode, coalescedNode);
                await seed.SaveChangesAsync();
            }

            long timestamp = 10_000;
            var idleGate = new ValidationClaimIdleGate(
                () => timestamp, 1_000, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
            var settings = new CollectorOptions { ProbeTimeoutSeconds = 5 };
            var dispatcher = new DistributedProxyValidationService(
                factory, Options.Create(settings), idleGate);

            Assert.Null(await dispatcher.ClaimAsync(probingNode.Id, CancellationToken.None));
            Assert.True(idleGate.CooldownActive);

            var dueProxy = Endpoint("198.51.100.210", ProxyStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-1));
            await using (var addWork = await factory.CreateDbContextAsync())
            {
                addWork.Proxies.Add(dueProxy);
                await addWork.SaveChangesAsync();
            }

            Assert.Null(await dispatcher.ClaimAsync(coalescedNode.Id, CancellationToken.None));
            Assert.Equal(1, idleGate.CoalescedClaims);
            await using (var heartbeatCheck = await factory.CreateDbContextAsync())
                Assert.NotNull(await heartbeatCheck.CheckerNodes.Where(x => x.Id == coalescedNode.Id)
                    .Select(x => x.LastHeartbeatAt).SingleAsync());

            timestamp += 2_000;
            var claimed = await dispatcher.ClaimAsync(coalescedNode.Id, CancellationToken.None);

            Assert.NotNull(claimed);
            Assert.Equal(dueProxy.Id, Assert.Single(claimed!.Items).Id);
            Assert.False(idleGate.CooldownActive);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task MissingUnloggedLeaseAfterDatabaseRecoveryDoesNotStrandNodeOrWork()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_unlogged_recovery_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connection.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var orphanedLeaseId = Guid.NewGuid();
            var node = Node("recovered", batchSize: 1);
            node.CurrentLeaseId = orphanedLeaseId;
            node.CurrentLeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5);
            var proxy = Endpoint("198.51.100.230", ProxyStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-1));
            var orphanedRun = new ValidationRun
            {
                LeaseId = orphanedLeaseId,
                CheckerNodeId = node.Id,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Claimed = 1
            };
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.AddRange(node, proxy, orphanedRun);
                await seed.SaveChangesAsync();
            }

            var dispatcher = new DistributedProxyValidationService(
                factory,
                Options.Create(new CollectorOptions { ProbeTimeoutSeconds = 5 }),
                new ValidationClaimIdleGate());

            var recovered = Assert.IsType<CheckerLeaseResponse>(
                await dispatcher.ClaimAsync(node.Id, CancellationToken.None));

            Assert.NotEqual(orphanedLeaseId, recovered.LeaseId);
            Assert.Equal(proxy.Id, Assert.Single(recovered.Items).Id);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal("failed", await verify.ValidationRuns.Where(run => run.Id == orphanedRun.Id)
                .Select(run => run.Status).SingleAsync());
            Assert.Equal(recovered.LeaseId, await verify.CheckerNodes.Where(item => item.Id == node.Id)
                .Select(item => item.CurrentLeaseId).SingleAsync());
            Assert.True(await verify.ProxyValidationLeases.AnyAsync(lease =>
                lease.ProxyId == proxy.Id && lease.LeaseId == recovered.LeaseId));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ProxyEndpoint Endpoint(
        string host,
        ProxyStatus status,
        DateTimeOffset? nextCheckAt)
    {
        var observedAt = nextCheckAt ?? DateTimeOffset.UtcNow.AddMinutes(-10);
        var aliveAt = status == ProxyStatus.Alive ? observedAt : (DateTimeOffset?)null;
        return new ProxyEndpoint
        {
            Host = host,
            Port = 8080,
            Protocol = ProxyProtocol.Http,
            Status = status,
            NextCheckAt = nextCheckAt,
            LastCheckedAt = status == ProxyStatus.Pending ? null : observedAt,
            FirstSeenAt = observedAt.AddDays(-1),
            LastSeenAt = observedAt,
            FirstAliveAt = aliveAt,
            LastAliveAt = aliveAt,
            CurrentAliveSince = aliveAt,
            LatencyMs = status == ProxyStatus.Alive ? 20 : null,
            SuccessfulChecks = status == ProxyStatus.Alive ? 1 : 0,
            FailedChecks = status == ProxyStatus.Dead ? 1 : 0,
            ConsecutiveFailedChecks = status == ProxyStatus.Dead ? 1 : 0
        };
    }

    private sealed class ValidationClaimShapeInterceptor : DbCommandInterceptor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _commands = new();

        internal IReadOnlyCollection<string> Commands => _commands.ToArray();
        internal int CommandCount => _commands.Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("WITH candidate AS MATERIALIZED", StringComparison.Ordinal))
                _commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ExpiredClaimLocksOnlyNarrowLeaseBeforeWaitingForValidationAudit()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var schema = $"proxyharbor_claim_lock_order_{suffix}";
        var applicationName = $"proxyharbor-claim-lock-order-{suffix}";
        var serviceConnection = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schema,
            ApplicationName = applicationName
        };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(serviceConnection.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var expiredLeaseId = Guid.NewGuid();
            var node = Node("expired", batchSize: 1);
            node.CurrentLeaseId = expiredLeaseId;
            node.CurrentLeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
            var proxy = new ProxyEndpoint
            {
                Host = "198.51.100.200",
                Port = 8200,
                Protocol = ProxyProtocol.Http,
                NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(-2)
            };
            var run = new ValidationRun
            {
                LeaseId = expiredLeaseId,
                CheckerNodeId = node.Id,
                Claimed = 1
            };
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.AddRange(node, proxy, run);
                seed.ProxyValidationLeases.Add(new ProxyValidationLease
                {
                    ProxyId = proxy.Id,
                    LeaseId = expiredLeaseId,
                    LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1)
                });
                await seed.SaveChangesAsync();
            }

            var settings = new CollectorOptions { ProbeTimeoutSeconds = 5 };
            var dispatcher = new DistributedProxyValidationService(
                factory, Options.Create(settings), new ValidationClaimIdleGate());

            var blockerConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schema,
                ApplicationName = $"proxyharbor-claim-blocker-{suffix}"
            };
            await using var blocker = new NpgsqlConnection(blockerConnectionString.ConnectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var lockRun = new NpgsqlCommand(
                "SELECT \"Id\" FROM \"ValidationRuns\" WHERE \"Id\" = @id FOR UPDATE", blocker, blockerTransaction))
            {
                lockRun.Parameters.AddWithValue("id", run.Id);
                Assert.Equal(run.Id, await lockRun.ExecuteScalarAsync());
            }

            var claimTask = dispatcher.ClaimAsync(node.Id, CancellationToken.None);
            var waitingForAuditLock = false;
            for (var attempt = 0; attempt < 100 && !waitingForAuditLock; attempt++)
            {
                await using var activity = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM pg_stat_activity
                        WHERE application_name = @application_name
                          AND wait_event_type = 'Lock'
                          AND query LIKE '%ValidationRuns%')
                    """, admin);
                activity.Parameters.AddWithValue("application_name", applicationName);
                waitingForAuditLock = (bool)(await activity.ExecuteScalarAsync())!;
                if (!waitingForAuditLock) await Task.Delay(50);
            }

            var probeConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schema,
                ApplicationName = $"proxyharbor-claim-probe-{suffix}"
            };
            await using var probe = new NpgsqlConnection(probeConnectionString.ConnectionString);
            await probe.OpenAsync();
            await using var probeTransaction = await probe.BeginTransactionAsync();
            await using var probeProxy = new NpgsqlCommand(
                "SELECT \"Id\" FROM \"Proxies\" WHERE \"Id\" = @id FOR UPDATE SKIP LOCKED", probe, probeTransaction);
            probeProxy.Parameters.AddWithValue("id", proxy.Id);
            var unlockedProxyId = await probeProxy.ExecuteScalarAsync();
            await using var probeLease = new NpgsqlCommand(
                "SELECT \"ProxyId\" FROM \"ProxyValidationLeases\" WHERE \"ProxyId\" = @id FOR UPDATE SKIP LOCKED",
                probe, probeTransaction);
            probeLease.Parameters.AddWithValue("id", proxy.Id);
            var lockedLeaseId = await probeLease.ExecuteScalarAsync();
            await probeTransaction.RollbackAsync();

            await blockerTransaction.CommitAsync();
            var claim = await claimTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(waitingForAuditLock);
            Assert.Equal(proxy.Id, unlockedProxyId);
            Assert.Null(lockedLeaseId);
            Assert.NotNull(claim);
            Assert.Single(claim!.Items);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal("failed", await verify.ValidationRuns
                .Where(x => x.Id == run.Id).Select(x => x.Status).SingleAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static CheckerNode Node(string name, int batchSize) => new()
    {
        Name = name,
        Host = name == "first" ? "203.0.113.10" : "203.0.113.11",
        SshUsername = "root",
        TokenHash = SHA256.HashData(Guid.NewGuid().ToByteArray()),
        BatchSize = batchSize,
        DeploymentStatus = "starting"
    };

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        private int createCount;

        public int CreateCount => Volatile.Read(ref createCount);
        public void ResetCreateCount() => Volatile.Write(ref createCount, 0);

        public ProxyHarborDbContext CreateDbContext()
        {
            Interlocked.Increment(ref createCount);
            return new(options);
        }

        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class CompletionFailureInterceptor : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("UPDATE \"ValidationRuns\"", StringComparison.Ordinal))
            {
                Armed = false;
                throw new InjectedCompletionFailure();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedCompletionFailure : Exception
    {
    }

}
