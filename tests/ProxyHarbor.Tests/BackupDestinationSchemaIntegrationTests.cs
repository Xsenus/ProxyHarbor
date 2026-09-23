using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет fail-closed ограничения нового destination-based persistence слоя.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class BackupDestinationSchemaIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task AdminDestinationOverviewReadsLatestOutcomeFromPostgres()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_backup_destination_overview_{Guid.NewGuid():N}";
        var connectionBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connectionBuilder.ConnectionString).Options;
            await using (var db = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.MigrateSchemaAsync(db, CancellationToken.None);
                var pool = new BackupPool { Name = "overview-test", PolicyVersion = 4 };
                var destination = new BackupDestination
                {
                    Name = "overview-test-s3",
                    Kind = "s3",
                    Enabled = true,
                    FailureDomain = "private-account",
                    SettingsJson = "{}"
                };
                db.BackupPools.Add(pool);
                db.BackupDestinations.Add(destination);
                db.BackupPoolDestinations.Add(new BackupPoolDestination
                {
                    BackupPoolId = pool.Id,
                    BackupDestinationId = destination.Id,
                    AllowedOperations = "put,verify,read"
                });
                db.BackupDestinationHealthOutcomes.AddRange(
                    new BackupDestinationHealthOutcome
                    {
                        BackupDestinationId = destination.Id,
                        Operation = "put",
                        Succeeded = true,
                        ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
                    },
                    new BackupDestinationHealthOutcome
                    {
                        BackupDestinationId = destination.Id,
                        Operation = "verify",
                        Succeeded = false,
                        ProbeOutcome = "missing",
                        ErrorCode = nameof(BackupDestinationErrorCode.NotFound),
                        ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                    });
                await db.SaveChangesAsync();
            }
            var factory = new ContextFactory(options);
            var controller = new AdminController(factory, null!, null!, null!, null!,
                Options.Create(new BackupOptions()), Options.Create(new CollectorOptions()));
            var result = Assert.IsType<PagedResult<BackupDestinationOverviewResponse>>(
                Assert.IsType<OkObjectResult>((await controller.BackupDestinations(
                    1, 10, CancellationToken.None)).Result).Value);
            var item = Assert.Single(result.Items);
            Assert.Equal("missing", item.LastOutcome?.ProbeOutcome);
            Assert.Equal(nameof(BackupDestinationErrorCode.NotFound), item.LastOutcome?.ErrorCode);
            Assert.Single(item.Routes);
            var route = Assert.Single(item.Routes);
            Assert.Equal(4, route.PolicyVersion);
            Assert.IsType<NoContentResult>(await controller.DrainBackupRoute(route.PoolId,
                item.Id, new DrainBackupRouteRequest(4), CancellationToken.None));
            Assert.IsType<NoContentResult>(await controller.DrainBackupRoute(route.PoolId,
                item.Id, new DrainBackupRouteRequest(4), CancellationToken.None));
            Assert.Equal(400, Assert.IsType<ObjectResult>(await controller.DrainBackupRoute(
                route.PoolId, item.Id, new DrainBackupRouteRequest(0), CancellationToken.None)).StatusCode);
            Assert.Equal(409, Assert.IsType<ObjectResult>(await controller.DrainBackupRoute(
                route.PoolId, item.Id, new DrainBackupRouteRequest(3), CancellationToken.None)).StatusCode);
            Assert.IsType<NotFoundResult>(await controller.DrainBackupRoute(route.PoolId,
                Guid.NewGuid(), new DrainBackupRouteRequest(4), CancellationToken.None));
            Assert.Equal(409, Assert.IsType<ObjectResult>(await controller.DrainBackupRoute(
                BackupLegacyDestinationProjector.LegacyPoolId, item.Id,
                new DrainBackupRouteRequest(4), CancellationToken.None)).StatusCode);
            await using var verify = new ProxyHarborDbContext(options);
            var persistedRoute = await verify.BackupPoolDestinations.Include(value => value.BackupDestination)
                .SingleAsync();
            Assert.False(persistedRoute.Enabled);
            Assert.True(persistedRoute.Draining);
            var registry = new BackupDestinationRegistry([
                new S3BackupDestinationAdapter(), new TelegramBackupDestinationAdapter()
            ]);
            Assert.IsType<S3BackupDestinationAdapter>(registry.Resolve(persistedRoute.BackupDestination,
                persistedRoute, BackupDestinationOperation.Materialize, 1));
            Assert.Throws<BackupDestinationRouteException>(() =>
            {
                _ = registry.Resolve(persistedRoute.BackupDestination, persistedRoute,
                    BackupDestinationOperation.Put, 1);
            });

            var protector = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
            var createController = new AdminController(factory, null!, null!, null!, null!,
                Options.Create(new BackupOptions()), Options.Create(new CollectorOptions()),
                credentialProtectionProvider: protector);
            var request = new CreateS3BackupDestinationRequest(
                "new-s3", "https://s3.example.test", "EU-WEST-1", "private-backups",
                "proxyharbor-drill", true, "test-access-key", "test-secret-key", 5);
            Assert.Equal(201, Assert.IsType<ObjectResult>((await createController.CreateS3BackupDestination(
                request, CancellationToken.None)).Result).StatusCode);
            Assert.Equal(409, Assert.IsType<ConflictObjectResult>((await createController.CreateS3BackupDestination(
                request, CancellationToken.None)).Result).StatusCode);
            var created = await verify.BackupDestinations.SingleAsync(item => item.Name == "new-s3");
            Assert.False(created.Enabled);
            Assert.Equal("s3:s3.example.test:eu-west-1", created.FailureDomain);
            Assert.DoesNotContain("test-secret-key", created.ProtectedSecrets, StringComparison.Ordinal);
            Assert.Contains("test-secret-key", protector.CreateProtector(
                "ProxyHarbor.BackupDestination.Secrets.v1").Unprotect(created.ProtectedSecrets),
                StringComparison.Ordinal);
            var secondRequest = request with
            {
                Name = "new-s3-secondary",
                Endpoint = "https://s3.other.test",
                Bucket = "secondary-backups"
            };
            var second = Assert.IsType<BackupDestinationOverviewResponse>(Assert.IsType<ObjectResult>(
                (await createController.CreateS3BackupDestination(secondRequest,
                    CancellationToken.None)).Result).Value);
            var poolRequest = new CreateBackupPoolRequest("new-durable", 1, 2, 3, 600, 900,
            [
                new CreateBackupPoolRouteRequest(created.Id, 10, "primary", true),
                new CreateBackupPoolRouteRequest(second.Id, 20, "fallback", true)
            ]);
            Assert.Equal(201, Assert.IsType<ObjectResult>((await createController.CreateBackupPool(
                poolRequest, CancellationToken.None)).Result).StatusCode);
            await using var verifyPool = new ProxyHarborDbContext(options);
            var newPool = await verifyPool.BackupPools.SingleAsync(item => item.Name == "new-durable");
            Assert.Equal(2, newPool.DesiredVerifiedCopies);
            Assert.Equal(2, await verifyPool.BackupPoolDestinations.CountAsync(item =>
                item.BackupPoolId == newPool.Id && item.Enabled && !item.Draining));
            Assert.True((await verifyPool.BackupDestinations.SingleAsync(item => item.Id == created.Id)).Enabled);
            Assert.True((await verifyPool.BackupDestinations.SingleAsync(item => item.Id == second.Id)).Enabled);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FreshSchemaCreatesNoJobsAndRejectsInvalidDurableState()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_backup_destinations_{Guid.NewGuid():N}";
        var connectionBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(connectionBuilder.ConnectionString)
                .Options;
            await using var db = new ProxyHarborDbContext(options);
            await DatabaseSeeder.MigrateSchemaAsync(db, CancellationToken.None);

            Assert.Empty(await db.BackupDestinations.ToArrayAsync());
            Assert.Empty(await db.BackupDeliveryJobs.ToArrayAsync());

            var now = DateTimeOffset.UtcNow;
            var run = new BackupRun
            {
                StartedAt = now.AddMinutes(-2),
                FinishedAt = now.AddMinutes(-1),
                Status = "completed",
                FileName = "schema-test.phbackup",
                SizeBytes = 123
            };
            var destination = new BackupDestination
            {
                Name = "schema-test-s3",
                Kind = "s3",
                Enabled = true,
                FailureDomain = "independent-a",
                CapabilitiesJson = "{\"operations\":[\"put\",\"verify\"]}",
                SettingsJson = "{}"
            };
            db.AddRange(run, destination);
            await db.SaveChangesAsync();

            db.BackupRuns.Add(new BackupRun
            {
                StartedAt = now,
                Status = "running",
                ContentSha256 = "not-a-sha256"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.BackupRuns.Add(new BackupRun
            {
                StartedAt = now,
                Status = "running",
                BackupPoolId = Guid.NewGuid(),
                ProtectionPolicyVersion = 2,
                RequiredVerifiedCopies = 2
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var protectedRun = new BackupRun
            {
                StartedAt = now,
                Status = "running",
                ContentSha256 = new string('c', 64),
                BackupPoolId = Guid.NewGuid(),
                ProtectionPolicyVersion = 2,
                RequiredVerifiedCopies = 2,
                DesiredVerifiedCopies = 3
            };
            db.BackupRuns.Add(protectedRun);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var persistedRun = await db.BackupRuns.SingleAsync(item => item.Id == protectedRun.Id);
            Assert.Equal(2, persistedRun.RequiredVerifiedCopies);
            Assert.Equal(3, persistedRun.DesiredVerifiedCopies);

            db.BackupCopies.Add(new BackupCopy
            {
                BackupRunId = run.Id,
                BackupDestinationId = destination.Id,
                ContentSha256 = new string('a', 64),
                SizeBytes = 123,
                State = "verified",
                VerifiedAt = now
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.BackupPools.Add(new BackupPool
            {
                Name = "invalid-policy",
                RequiredVerifiedCopies = 2,
                DesiredVerifiedCopies = 1
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var copy = new BackupCopy
            {
                BackupRunId = run.Id,
                BackupDestinationId = destination.Id,
                ContentSha256 = new string('b', 64),
                SizeBytes = 123,
                State = "planned"
            };
            db.BackupCopies.Add(copy);
            await db.SaveChangesAsync();

            db.BackupDeliveryJobs.Add(new BackupDeliveryJob
            {
                BackupCopyId = copy.Id,
                IdempotencyKey = "schema-test-job",
                State = "pending",
                NotBefore = now,
                LeaseId = Guid.NewGuid(),
                CreatedAt = now,
                UpdatedAt = now
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ContextFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
    }
}
