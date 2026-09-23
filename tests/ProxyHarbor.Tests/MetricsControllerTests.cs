using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует Prometheus-контракт и выбор последнего завершённого цикла.</summary>
public sealed class MetricsControllerTests
{
    [Fact]
    public async Task StagingMetricsReportExactOwnedBytesAndExplicitReadFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-staging-metrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "proxyharbor-20260810-140000-0000.phbackup"), new byte[6]);
            File.WriteAllBytes(Path.Combine(directory, "proxyharbor-invalid.phbackup"), new byte[100]);
            var factory = new TestDbFactory(new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase($"metrics-staging-{Guid.NewGuid():N}").Options);
            var routing = Options.Create(new BackupRoutingOptions { Enabled = true, MaximumStagingBytes = 104_857_600 });
            var controller = new MetricsController(factory, Options.Create(new CollectorOptions()),
                Options.Create(new BackupOptions { Directory = directory }), new ProbeControlHealth(),
                backupRoutingOptions: routing);
            var metrics = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None)).Content!;
            Assert.Contains("proxyharbor_backup_staging_read_success 1\n", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_backup_staging_used_bytes 6\n", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_backup_staging_limit_bytes 104857600\n", metrics, StringComparison.Ordinal);

            Directory.Delete(directory, recursive: true);
            metrics = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None)).Content!;
            Assert.Contains("proxyharbor_backup_staging_read_success 0\n", metrics, StringComparison.Ordinal);
            Assert.Contains("proxyharbor_backup_staging_used_bytes 0\n", metrics, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DestinationMetricsAllowlistKindAndNeverRenderProviderSettings()
    {
        var factory = new TestDbFactory(new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-destination-labels-{Guid.NewGuid():N}").Options);
        var destination = new BackupDestination
        {
            Name = "private-account-name",
            Kind = "bad-kind\"} secret-label",
            SettingsJson = "private-bucket-setting",
            ProtectedSecrets = "protected-secret-ciphertext"
        };
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.BackupDestinations.Add(destination);
            await seed.SaveChangesAsync();
        }

        var controller = new MetricsController(factory, Options.Create(new CollectorOptions()),
            Options.Create(new BackupOptions()), new ProbeControlHealth());
        var metrics = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None)).Content!;

        Assert.Contains($"proxyharbor_backup_destination_put_failed_last_1h{{destination_id=\"{destination.Id:N}\",kind=\"unknown\"}} 0",
            metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("private-account-name", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("private-bucket-setting", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("protected-secret-ciphertext", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-label", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupProtectionMetricsUseIndependentVerifiedCopiesAndBoundedStates()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-backup-protection-{Guid.NewGuid():N}").Options;
        var factory = new TestDbFactory(options);
        var now = DateTimeOffset.UtcNow;
        var pool = new BackupPool { RequiredVerifiedCopies = 1, DesiredVerifiedCopies = 2 };
        var primary = new BackupDestination
        {
            Name = "Primary",
            Kind = "s3",
            Enabled = true,
            FailureDomain = "account-a"
        };
        var fallback = new BackupDestination
        {
            Name = "Fallback",
            Kind = "s3",
            Enabled = true,
            FailureDomain = "account-b"
        };
        var sameDomain = new BackupDestination
        {
            Name = "Same account",
            Kind = "s3",
            Enabled = true,
            FailureDomain = "account-a"
        };
        var run = new BackupRun
        {
            Status = "completed",
            FinishedAt = now.AddMinutes(-3),
            BackupPoolId = pool.Id,
            ProtectionPolicyVersion = 1,
            RequiredVerifiedCopies = 1,
            DesiredVerifiedCopies = 2,
            ContentSha256 = new string('a', 64),
            SizeBytes = 123
        };
        var verified = new BackupCopy
        {
            BackupRunId = run.Id,
            BackupDestinationId = primary.Id,
            State = "verified",
            VerifiedAt = now.AddMinutes(-2),
            NativeLocator = "opaque-private-object-key",
            ContentSha256 = run.ContentSha256,
            SizeBytes = run.SizeBytes,
            PolicyVersion = 1
        };
        var uncertain = new BackupCopy
        {
            BackupRunId = run.Id,
            BackupDestinationId = fallback.Id,
            State = "unknown",
            ContentSha256 = run.ContentSha256,
            SizeBytes = run.SizeBytes,
            PolicyVersion = 1
        };
        var duplicateDomain = new BackupCopy
        {
            BackupRunId = run.Id,
            BackupDestinationId = sameDomain.Id,
            State = "verified",
            VerifiedAt = now.AddMinutes(-2),
            NativeLocator = "second-object-in-same-account",
            ContentSha256 = run.ContentSha256,
            SizeBytes = run.SizeBytes,
            PolicyVersion = 1
        };
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.BackupPools.Add(pool);
            seed.BackupDestinations.AddRange(primary, fallback, sameDomain);
            seed.BackupPoolDestinations.AddRange(
                new BackupPoolDestination { BackupPoolId = pool.Id, BackupDestinationId = primary.Id },
                new BackupPoolDestination { BackupPoolId = pool.Id, BackupDestinationId = fallback.Id },
                new BackupPoolDestination { BackupPoolId = pool.Id, BackupDestinationId = sameDomain.Id });
            seed.BackupRuns.Add(run);
            seed.BackupCopies.AddRange(verified, uncertain, duplicateDomain);
            seed.BackupDeliveryJobs.Add(new BackupDeliveryJob
            {
                BackupCopyId = uncertain.Id,
                State = "pending",
                CreatedAt = now.AddMinutes(-10),
                NotBefore = now.AddMinutes(-10)
            });
            seed.BackupRestoreVerifications.Add(new BackupRestoreVerification
            {
                BackupRunId = run.Id,
                Environment = "isolated",
                Result = "passed",
                FinishedAt = now.AddDays(-1)
            });
            seed.BackupDestinationHealthOutcomes.AddRange(
                new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = primary.Id,
                    Operation = "put",
                    Succeeded = true,
                    ObservedAt = now.AddMinutes(-5)
                },
                new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = fallback.Id,
                    Operation = "put",
                    Succeeded = false,
                    ErrorCode = "timeout",
                    ObservedAt = now.AddMinutes(-4)
                },
                new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = primary.Id,
                    Operation = "verify",
                    Succeeded = true,
                    ProbeOutcome = "matching",
                    ObservedAt = now.AddMinutes(-3)
                },
                new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = fallback.Id,
                    Operation = "verify",
                    Succeeded = false,
                    ErrorCode = "not_found",
                    ProbeOutcome = "missing",
                    ObservedAt = now.AddMinutes(-2)
                },
                new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = sameDomain.Id,
                    Operation = "verify",
                    Succeeded = false,
                    ErrorCode = "checksum_mismatch",
                    ProbeOutcome = "mismatching",
                    ObservedAt = now.AddMinutes(-2)
                },
                new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = fallback.Id,
                    Operation = "verify",
                    Succeeded = false,
                    ErrorCode = "timeout",
                    ProbeOutcome = "inconclusive",
                    ObservedAt = now.AddMinutes(-1)
                },
                new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = fallback.Id,
                    Operation = "put",
                    Succeeded = false,
                    ErrorCode = "old_failure",
                    ObservedAt = now.AddHours(-2)
                });
            await seed.SaveChangesAsync();
        }

        var registry = new BackupDestinationRegistry(
            [new MetadataAdapter("s3", true), new MetadataAdapter("telegram", false)]);
        var controller = new MetricsController(factory, Options.Create(new CollectorOptions()),
            Options.Create(new BackupOptions()), new ProbeControlHealth(),
            backupRoutingOptions: Options.Create(new BackupRoutingOptions { Enabled = true }),
            backupProtectionEvaluator: new BackupProtectionEvaluator(factory, registry));

        var metrics = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None)).Content!;
        Assert.Contains("proxyharbor_backup_routing_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_latest_routed_run_assessed 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_latest_verified_independent_copies 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_latest_required_copy_debt 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_last_protected_run_search_assessed 1", metrics,
            StringComparison.Ordinal);
        Assert.Contains($"proxyharbor_backup_last_protected_run_timestamp_seconds {run.FinishedAt!.Value.ToUnixTimeSeconds()}",
            metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_latest_desired_copy_debt 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_copies_unknown 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_delivery_jobs_pending 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_last_isolated_restore_pass_timestamp_seconds ", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_provider_put_verified_last_1h 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_provider_put_failed_last_1h 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_provider_verify_matching_last_1h 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_provider_verify_missing_last_1h 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_provider_verify_mismatching_last_1h 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_provider_verify_inconclusive_last_1h 1", metrics,
            StringComparison.Ordinal);
        Assert.Contains($"proxyharbor_backup_destination_put_failed_last_1h{{destination_id=\"{fallback.Id:N}\",kind=\"s3\"}} 1",
            metrics, StringComparison.Ordinal);
        Assert.Contains($"proxyharbor_backup_destination_verify_unhealthy_last_1h{{destination_id=\"{fallback.Id:N}\",kind=\"s3\"}} 2",
            metrics, StringComparison.Ordinal);
        Assert.Contains($"proxyharbor_backup_destination_verify_unhealthy_last_1h{{destination_id=\"{sameDomain.Id:N}\",kind=\"s3\"}} 1",
            metrics, StringComparison.Ordinal);
        Assert.Contains($"proxyharbor_backup_destination_put_failed_last_1h{{destination_id=\"{primary.Id:N}\",kind=\"s3\"}} 0",
            metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-private-object-key", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("second-object-in-same-account", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("account-a", metrics, StringComparison.Ordinal);

        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.BackupRuns.Add(new BackupRun
            {
                Status = "completed",
                FinishedAt = now.AddMinutes(-1),
                BackupPoolId = pool.Id,
                ProtectionPolicyVersion = 1,
                RequiredVerifiedCopies = 1,
                DesiredVerifiedCopies = 2,
                ContentSha256 = new string('b', 64),
                SizeBytes = 123
            });
            await seed.SaveChangesAsync();
        }
        metrics = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None)).Content!;
        Assert.Contains("proxyharbor_backup_latest_required_copy_debt 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_last_protected_run_search_assessed 1", metrics,
            StringComparison.Ordinal);
        Assert.Contains($"proxyharbor_backup_last_protected_run_timestamp_seconds {run.FinishedAt!.Value.ToUnixTimeSeconds()}",
            metrics, StringComparison.Ordinal);

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var unassessable = new BackupRun
            {
                Status = "completed",
                FinishedAt = now.AddMinutes(-2),
                BackupPoolId = pool.Id,
                ProtectionPolicyVersion = 1,
                RequiredVerifiedCopies = 0,
                DesiredVerifiedCopies = 2,
                ContentSha256 = new string('c', 64),
                SizeBytes = 123
            };
            seed.BackupRuns.Add(unassessable);
            seed.BackupCopies.Add(new BackupCopy
            {
                BackupRunId = unassessable.Id,
                BackupDestinationId = primary.Id,
                State = "verified",
                VerifiedAt = now.AddMinutes(-2),
                NativeLocator = "unassessable-candidate",
                ContentSha256 = unassessable.ContentSha256,
                SizeBytes = 123,
                PolicyVersion = 1
            });
            await seed.SaveChangesAsync();
        }
        metrics = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None)).Content!;
        Assert.Contains("proxyharbor_backup_last_protected_run_search_assessed 0", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_last_protected_run_timestamp_seconds 0", metrics,
            StringComparison.Ordinal);
        Assert.DoesNotContain("unassessable-candidate", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupProtectionMetricsDoNotAssessLegacyRunAsProtected()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-backup-legacy-{Guid.NewGuid():N}").Options;
        var factory = new TestDbFactory(options);
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.BackupRuns.Add(new BackupRun
            {
                Status = "completed",
                FinishedAt = DateTimeOffset.UtcNow,
                BackupPoolId = Guid.NewGuid(),
                ContentSha256 = new string('b', 64)
            });
            seed.BackupDeliveryJobs.Add(new BackupDeliveryJob
            {
                BackupCopyId = Guid.NewGuid(),
                State = "pending",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-3),
                NotBefore = DateTimeOffset.UtcNow.AddHours(1)
            });
            await seed.SaveChangesAsync();
        }

        var controller = new MetricsController(factory, Options.Create(new CollectorOptions()),
            Options.Create(new BackupOptions()), new ProbeControlHealth(),
            backupRoutingOptions: Options.Create(new BackupRoutingOptions { Enabled = true }),
            backupProtectionEvaluator: new BackupProtectionEvaluator(factory,
                new BackupDestinationRegistry(
                    [new MetadataAdapter("s3", true), new MetadataAdapter("telegram", false)])));

        var metrics = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None)).Content!;
        Assert.Contains("proxyharbor_backup_latest_routed_run_exists 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_latest_routed_run_assessed 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_latest_verified_independent_copies 0", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_delivery_jobs_pending 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_oldest_due_pending_job_age_seconds 0", metrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsExposeVpnSourceAndBuiltInCatalogHealth()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-vpn-sources-{Guid.NewGuid():N}").Options;
        var auditedAt = DateTimeOffset.UtcNow;
        var builtIn = BuiltInVpnSourceCatalog.Sources[0];
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.VpnSources.AddRange(
                new VpnSource
                {
                    Name = builtIn.Name,
                    Provider = builtIn.Provider,
                    Url = builtIn.Url,
                    DefaultProtocol = builtIn.Protocol,
                    License = builtIn.License,
                    LastFetchedAt = auditedAt,
                    LastSucceededAt = auditedAt,
                    LastItemCount = 10
                },
                new VpnSource
                {
                    Name = "custom failure",
                    Provider = "Custom",
                    Url = "https://example.com/custom-vpn.txt",
                    DefaultProtocol = VpnProtocol.Vless,
                    License = "custom",
                    LastFetchedAt = auditedAt,
                    LastError = "timeout",
                    ConsecutiveFailures = 1
                });
            await seed.SaveChangesAsync();
        }

        var controller = new MetricsController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { CollectionIntervalMinutes = 15 }),
            Options.Create(new BackupOptions()),
            new ProbeControlHealth());

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_vpn_sources_enabled 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_sources_failing 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_sources_healthy 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_sources_never_audited 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_source_catalog_complete 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_source_catalog_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_catalog_audit_timestamp_seconds 1789344000", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_expected 270", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_healthy 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_failing 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_never_audited 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_providers_expected 33", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_providers_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_vpn_providers_enabled 1", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsDoNotReportHistoricallySuccessfulStaleSourceAsHealthy()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-stale-{Guid.NewGuid():N}").Options;
        var builtIn = BuiltInSourceCatalog.Sources[0];
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Sources.Add(new ProxySource
            {
                Name = builtIn.Name,
                Url = builtIn.Url,
                DefaultProtocol = builtIn.Protocol,
                LastFetchedAt = DateTimeOffset.UtcNow.AddHours(-1),
                LastSucceededAt = DateTimeOffset.UtcNow.AddHours(-1),
                LastItemCount = 10
            });
            await seed.SaveChangesAsync();
        }

        var httpTelemetry = new HttpRequestTelemetry();
        httpTelemetry.Record(HttpRouteGroup.Proxies, 503, TimeSpan.FromMilliseconds(250));
        long idleTimestamp = 1_000;
        var idleGate = new ValidationClaimIdleGate(
            () => idleTimestamp, 1_000, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        idleGate.MarkEmpty();
        Assert.True(idleGate.TryCoalesce(Guid.NewGuid()).Coalesced);
        Assert.True(idleGate.TryCoalesceSerializedProbe());
        using var checkerCredentials = new CheckerNodeCredentialCache(
            new TestDbFactory(options), TimeProvider.System);
        checkerCredentials.Invalidate();
        var invalidCheckerToken = new string('x', 48);
        Assert.False(await checkerCredentials.AuthenticateAsync(Guid.NewGuid(), invalidCheckerToken, default));
        Assert.False(await checkerCredentials.AuthenticateAsync(Guid.NewGuid(), invalidCheckerToken, default));
        var controller = new MetricsController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { CollectionIntervalMinutes = 5 }),
            Options.Create(new BackupOptions()),
            new ProbeControlHealth(),
            httpTelemetry: httpTelemetry,
            validationIdleGate: idleGate,
            checkerCredentialCache: checkerCredentials);

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.DoesNotContain('\r', metrics);
        Assert.Contains("proxyharbor_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_sources_stale 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_stale 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_background_workers_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("# TYPE proxyharbor_validation_empty_claims_coalesced_total counter", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_empty_claims_coalesced_total 2", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_empty_claims_serialized_total 1", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_empty_claim_cooldown_active 1", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_attempts_total 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_failures_total 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_snapshot_hits_total 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_database_reads_total 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_checker_auth_invalidations_total 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_last_success_timestamp_seconds 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_last_failure_timestamp_seconds 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_last_deleted_rows 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_last_recovered_rows 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_maintenance_healthy -1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_collection_interval_seconds 300", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_public_freshness_seconds 900", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_validation_concurrency_limit 800", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_validation_batch_size 1600", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_reachable_validation_interval_seconds 600", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_unreachable_retry_seconds 1800", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_unsupported_retry_seconds 21600", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_public_freshness_seconds 900", metrics, StringComparison.Ordinal);
        Assert.Contains("# TYPE proxyharbor_vpn_endpoints gauge", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_endpoints 0", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyharbor_vpn_endpoints_total", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_validation_due 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_vpn_published 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_configuration_read_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_enabled 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_interval_seconds 86400", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_telegram_configured 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_object_storage_configured 0", metrics, StringComparison.Ordinal);
        Assert.Contains("# TYPE proxyharbor_advisory_lock_cleanup_failures_total counter", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_http_requests_total{route=\"proxies\",status=\"5xx\"} 1", metrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsIgnoreNewerRunningCycleForLastCompletionValues()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-{Guid.NewGuid():N}").Options;
        var finishedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_100);
        var sourceAuditedAt = DateTimeOffset.UtcNow;
        var latestValidationAttempt = sourceAuditedAt.AddMinutes(-1);
        var builtIn = BuiltInSourceCatalog.Sources[0];
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Sources.AddRange(
                new ProxySource
                {
                    Name = "healthy",
                    Url = builtIn.Url,
                    DefaultProtocol = builtIn.Protocol,
                    LastFetchedAt = sourceAuditedAt,
                    LastSucceededAt = sourceAuditedAt,
                    LastItemCount = 10,
                    LastResultTruncated = true
                },
                new ProxySource { Name = "failed", Url = "https://example.com/b", ConsecutiveFailures = 1, LastError = "timeout" });
            seed.Proxies.AddRange(
                new ProxyEndpoint
                {
                    Host = "8.8.8.8",
                    Port = 8080,
                    Status = ProxyStatus.Alive,
                    LastCheckedAt = latestValidationAttempt,
                    LastValidationAttemptAt = latestValidationAttempt
                },
                new ProxyEndpoint
                {
                    Host = "1.1.1.1",
                    Port = 8081,
                    FirstSeenAt = sourceAuditedAt.AddDays(-5),
                    LastSeenAt = sourceAuditedAt.AddDays(-4),
                    LastValidationAttemptAt = sourceAuditedAt.AddMinutes(-2),
                    LastValidationDeferred = true
                },
                new ProxyEndpoint { Host = "9.9.9.9", Port = 8082 });
            var leasedProxy = new ProxyEndpoint
            {
                Host = "4.4.4.4",
                Port = 8083,
                LastValidationAttemptAt = sourceAuditedAt.AddMinutes(-3)
            };
            seed.Proxies.Add(leasedProxy);
            seed.ProxyValidationLeases.Add(new ProxyValidationLease
            {
                ProxyId = leasedProxy.Id,
                LeaseId = Guid.NewGuid(),
                LeaseUntil = sourceAuditedAt.AddMinutes(1)
            });
            seed.ValidationRuns.AddRange(
                new ValidationRun
                {
                    LeaseId = Guid.NewGuid(),
                    StartedAt = sourceAuditedAt.AddMinutes(-1).AddSeconds(-1),
                    FinishedAt = sourceAuditedAt.AddMinutes(-1),
                    Claimed = 2,
                    Checked = 1,
                    Alive = 1,
                    Deferred = 1,
                    Status = "completed"
                },
                new ValidationRun
                {
                    LeaseId = Guid.NewGuid(),
                    StartedAt = sourceAuditedAt.AddSeconds(-31),
                    FinishedAt = sourceAuditedAt.AddSeconds(-30),
                    Status = "failed",
                    Error = "probe pipeline failed"
                },
                new ValidationRun
                {
                    LeaseId = Guid.NewGuid(),
                    StartedAt = sourceAuditedAt,
                    Claimed = 3,
                    Status = "running"
                });
            seed.Runs.AddRange(
                new CollectionRun
                {
                    StartedAt = finishedAt.AddSeconds(-12.5),
                    FinishedAt = finishedAt,
                    Status = "completed",
                    CandidatesFound = 42,
                    SourcesTruncated = 1,
                    CandidateLimitReached = true
                },
                new CollectionRun { StartedAt = finishedAt.AddMinutes(1), Status = "running", CandidatesFound = 999 });
            seed.BackupRuns.AddRange(
                new BackupRun
                {
                    StartedAt = finishedAt.AddMinutes(-2),
                    FinishedAt = finishedAt.AddMinutes(-1),
                    Status = "completed",
                    TelegramConfigured = true,
                    SentToTelegram = true,
                    SizeBytes = 12_345
                },
                new BackupRun { StartedAt = finishedAt.AddMinutes(2), Status = "running" });
            await seed.SaveChangesAsync();
        }

        var controlHealth = new ProbeControlHealth();
        controlHealth.Record(available: true);
        var controller = new MetricsController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15, CollectionIntervalMinutes = 20 }),
            Options.Create(new BackupOptions
            {
                Enabled = true,
                IntervalHours = 12,
                TelegramBotToken = "test-token",
                TelegramChatId = "123"
            }),
            controlHealth);

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_sources_truncated 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_source_catalog_complete 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_source_catalog_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_catalog_audit_timestamp_seconds 1789171200", metrics,
            StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_expected 543", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_healthy 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_stale 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_sources_truncated 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_providers_expected 283", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_builtin_providers_present 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_collection_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_never_attempted 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_proxies_stale_unseen 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_proxies_published 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_due 3", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_leased 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_attempts_last_5m 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_checked_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_alive_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_deferred_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_runs_failed_last_5m 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_background_workers_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_collection_interval_seconds 1200", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_public_freshness_seconds 900", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_concurrency_limit 800", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_batch_size 1600", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_checks_per_second 2", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_validation_estimated_drain_seconds 2", metrics, StringComparison.Ordinal);
        Assert.Contains($"proxyharbor_validation_last_attempt_timestamp_seconds {latestValidationAttempt.ToUnixTimeSeconds()}",
            metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_probe_control_available 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_candidates 42", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_sources_skipped 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_sources_truncated 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_candidate_limit_reached 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_timestamp_seconds 1700000100", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_collection_duration_seconds 12.5", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyharbor_last_collection_candidates 999", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_interval_seconds 43200", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_telegram_configured 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_runs_active 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_telegram_configured 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_sent_to_telegram 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_size_bytes 12345", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_last_backup_timestamp_seconds 1700000040", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsUseEffectiveRuntimeBackupConfiguration()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-runtime-backup-{Guid.NewGuid():N}").Options;
        var runtimeOptions = new BackupOptions
        {
            Enabled = true,
            IntervalHours = 6,
            TelegramRecipientId = Guid.NewGuid()
        };
        var controller = new MetricsController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions()),
            Options.Create(new BackupOptions { Enabled = false, IntervalHours = 24 }),
            new ProbeControlHealth(),
            backupConfigurationStore: new TestBackupConfigurationStore(runtimeOptions));

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_backup_configuration_read_success 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_enabled 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_interval_seconds 21600", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_telegram_configured 1", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_object_storage_configured 0", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsExposeRuntimeBackupConfigurationReadFailureAndUseSafeFallback()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"metrics-runtime-backup-failure-{Guid.NewGuid():N}").Options;
        var controller = new MetricsController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions()),
            Options.Create(new BackupOptions { Enabled = false, IntervalHours = 12 }),
            new ProbeControlHealth(),
            backupConfigurationStore: new FailingBackupConfigurationStore());

        var result = Assert.IsType<ContentResult>(await controller.Get(CancellationToken.None));
        var metrics = result.Content!;
        Assert.Contains("proxyharbor_backup_configuration_read_success 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_enabled 0", metrics, StringComparison.Ordinal);
        Assert.Contains("proxyharbor_backup_interval_seconds 43200", metrics, StringComparison.Ordinal);
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestBackupConfigurationStore(BackupOptions options) : IBackupConfigurationStore
    {
        public Task<BackupOptions> GetAsync(CancellationToken token = default) => Task.FromResult(options);

        public Task SaveAsync(BackupOptions optionsToSave, CancellationToken token = default) =>
            Task.CompletedTask;
    }

    private sealed class FailingBackupConfigurationStore : IBackupConfigurationStore
    {
        public Task<BackupOptions> GetAsync(CancellationToken token = default) =>
            Task.FromException<BackupOptions>(new InvalidOperationException("Invalid runtime backup configuration."));

        public Task SaveAsync(BackupOptions options, CancellationToken token = default) =>
            Task.CompletedTask;
    }

    private sealed class MetadataAdapter(string kind, bool canVerify) : IBackupDestinationAdapter
    {
        public string Kind { get; } = kind;
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(canVerify), new(false), false, false, false);
    }
}
