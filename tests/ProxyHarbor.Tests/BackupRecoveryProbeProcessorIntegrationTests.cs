using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupRecoveryProbeProcessorIntegrationTests
{
    [Fact]
    public async Task DisabledRoutingNeverStartsProviderProbe()
    {
        using var worker = new BackupRecoveryProbeWorker(
            null!, Options.Create(new BackupRoutingOptions { Enabled = false }),
            NullLogger<BackupRecoveryProbeWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await worker.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(BackupDestinationProbeOutcome.Matching, "verified", "matching", null, false, true, false)]
    [InlineData(BackupDestinationProbeOutcome.Missing, "missing", "missing", null, false, true, false)]
    [InlineData(BackupDestinationProbeOutcome.Mismatching, "quarantined", "mismatching", null, false, true, false)]
    [InlineData(BackupDestinationProbeOutcome.Inconclusive, "verified", "inconclusive",
        BackupDestinationErrorCode.AuthenticationFailed, false, true, false)]
    [InlineData(BackupDestinationProbeOutcome.Matching, "verified", "invalid",
        BackupDestinationErrorCode.InvalidConfiguration, true, true, false)]
    [InlineData(BackupDestinationProbeOutcome.Matching, "verified", null, null, false, false, false)]
    [InlineData(BackupDestinationProbeOutcome.Matching, "quarantined", null, null, false, true, true)]
    [Trait("Category", "PostgresIntegration")]
    public async Task ProbeRecordsExactOutcomeAndNeverWritesProvider(
        BackupDestinationProbeOutcome providerOutcome,
        string expectedCopyState,
        string? expectedOutcome,
        BackupDestinationErrorCode? expectedFailure,
        bool wrongLocator,
        bool routeEnabled,
        bool changesDuringProbe)
    {
        var baseConnection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        var schema = $"proxyharbor_recovery_probe_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnection)
                {
                    SearchPath = schema
                }.ConnectionString).Options;
            var factory = new ProbeDbFactory(options);
            Guid destinationId;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                var pool = new BackupPool { Name = "recovery-probe", PolicyVersion = 1 };
                var destination = new BackupDestination
                {
                    Name = "recovery-s3",
                    Kind = "s3",
                    Enabled = true,
                    FailureDomain = "recovery-test",
                    Priority = 10
                };
                var run = new BackupRun
                {
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    FinishedAt = DateTimeOffset.UtcNow,
                    Status = "completed",
                    FileName = "probe.phbackup",
                    SizeBytes = 5,
                    ContentSha256 = new string('a', 64),
                    BackupPoolId = pool.Id,
                    ProtectionPolicyVersion = pool.PolicyVersion,
                    RequiredVerifiedCopies = 1,
                    DesiredVerifiedCopies = 1
                };
                var copy = new BackupCopy
                {
                    BackupRunId = run.Id,
                    BackupDestinationId = destination.Id,
                    ContentSha256 = run.ContentSha256,
                    SizeBytes = run.SizeBytes,
                    PolicyVersion = pool.PolicyVersion,
                    State = "verified",
                    NativeLocator = "safe/probe.phbackup",
                    VerifiedAt = DateTimeOffset.UtcNow
                };
                seed.AddRange(pool, destination, run, copy,
                    new BackupPoolDestination
                    {
                        BackupPoolId = pool.Id,
                        BackupDestinationId = destination.Id,
                        AllowedOperations = "verify,read",
                        Enabled = routeEnabled,
                        Priority = 10
                    });
                await seed.SaveChangesAsync();
                destinationId = destination.Id;
            }
            Func<Task>? changeCopy = null;
            if (changesDuringProbe)
            {
                changeCopy = async () =>
                {
                    await using var concurrent = await factory.CreateDbContextAsync();
                    _ = await concurrent.BackupCopies.ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.State, "quarantined")
                        .SetProperty(item => item.VerifiedAt, (DateTimeOffset?)null));
                };
            }
            var s3 = new ProbeAdapter(providerOutcome, wrongLocator, expectedFailure, changeCopy);
            var health = new BackupDestinationHealth();
            var processor = new BackupRecoveryProbeProcessor(
                factory, new BackupDestinationRegistry([s3, new TelegramMetadataAdapter()]), health);

            var recorded = routeEnabled && !changesDuringProbe;
            Assert.Equal(recorded ? 1 : 0,
                await processor.TryProbeOneAsync(CancellationToken.None));
            Assert.Equal(0, await processor.TryProbeOneAsync(CancellationToken.None));
            Assert.Equal(routeEnabled ? 1 : 0, s3.ProbeCalls);
            await using var verify = await factory.CreateDbContextAsync();
            var copyAfter = await verify.BackupCopies.SingleAsync();
            Assert.Equal(expectedCopyState, copyAfter.State);
            Assert.Equal(expectedCopyState == "verified", copyAfter.VerifiedAt.HasValue);
            if (!recorded)
            {
                Assert.Empty(await verify.BackupDestinationHealthOutcomes.ToArrayAsync());
                return;
            }
            var observation = await verify.BackupDestinationHealthOutcomes.SingleAsync();
            Assert.Equal(destinationId, observation.BackupDestinationId);
            Assert.Equal("verify", observation.Operation);
            Assert.Equal(expectedOutcome, observation.ProbeOutcome);
            Assert.Equal(expectedFailure?.ToString(), observation.ErrorCode);
            Assert.Equal(expectedFailure is null, observation.Succeeded);
            if (providerOutcome == BackupDestinationProbeOutcome.Missing)
                Assert.Equal(BackupDestinationErrorCode.NotFound.ToString(), copyAfter.LastErrorCode);
            if (providerOutcome == BackupDestinationProbeOutcome.Mismatching)
                Assert.Equal(BackupDestinationErrorCode.IntegrityMismatch.ToString(), copyAfter.LastErrorCode);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ProbeDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);

        public Task<ProxyHarborDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class ProbeAdapter(
        BackupDestinationProbeOutcome outcome,
        bool wrongLocator,
        BackupDestinationErrorCode? failure,
        Func<Task>? beforeReturn) : IBackupDestinationAdapter
    {
        public string Kind => "s3";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(true), new(true), true, true, true);
        public int ProbeCalls { get; private set; }

        public async Task<BackupDestinationProbeResult> ProbeWriteOutcomeAsync(
            BackupDestination destination, string fileName, string expectedSha256,
            long expectedSize, CancellationToken token)
        {
            ProbeCalls++;
            Assert.Equal("probe.phbackup", fileName);
            Assert.Equal(5, expectedSize);
            Assert.Equal(new string('a', 64), expectedSha256);
            if (beforeReturn is not null) await beforeReturn();
            return new BackupDestinationProbeResult(
                outcome,
                wrongLocator ? "other/probe.phbackup" : "safe/probe.phbackup",
                FailureCode: failure);
        }
    }

    private sealed class TelegramMetadataAdapter : IBackupDestinationAdapter
    {
        public string Kind => "telegram";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(false), new(false), false, false, false);
    }
}
