using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет управляемый из админки жизненный цикл локальных encrypted-backup.</summary>
public sealed class AdminBackupControllerTests
{
    [Fact]
    public async Task CreatePoolRequiresIndependentS3DomainsAndActivatesOnlyChosenRoutes()
    {
        var factory = Factory($"admin-create-pool-{Guid.NewGuid():N}");
        var controller = new AdminController(factory, null!, null!, null!, null!,
            Options.Create(new BackupOptions()), Options.Create(new CollectorOptions()),
            credentialProtectionProvider: new EphemeralDataProtectionProvider());
        async Task<Guid> CreateDestination(string name, string endpoint, string bucket)
        {
            var request = new CreateS3BackupDestinationRequest(name, endpoint, "eu-west-1",
                bucket, "backup", true, "test-access-key", "test-secret-key", 10);
            return Assert.IsType<BackupDestinationOverviewResponse>(Assert.IsType<ObjectResult>(
                (await controller.CreateS3BackupDestination(request, CancellationToken.None)).Result).Value).Id;
        }
        var first = await CreateDestination("first", "https://s3.example.test", "first-bucket");
        var sameDomain = await CreateDestination("same-domain", "https://s3.example.test", "other-bucket");
        var second = await CreateDestination("second", "https://s3.other.test", "second-bucket");
        static CreateBackupPoolRouteRequest Route(Guid id) => new(id, 10, "primary", true);
        var impossible = new CreateBackupPoolRequest("durable", 1, 2, 3, 600, 900,
            [Route(first), Route(sameDomain)]);

        Assert.Equal(409, Assert.IsType<ObjectResult>((await controller.CreateBackupPool(
            impossible, CancellationToken.None)).Result).StatusCode);
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await verify.BackupPools.ToArrayAsync());
            Assert.All(await verify.BackupDestinations.ToArrayAsync(), item => Assert.False(item.Enabled));
        }

        var valid = impossible with
        {
            Routes = [Route(first), new CreateBackupPoolRouteRequest(second, 20, "fallback", true)]
        };
        var response = Assert.IsType<BackupPoolCreationResponse>(Assert.IsType<ObjectResult>(
            (await controller.CreateBackupPool(valid, CancellationToken.None)).Result).Value);
        Assert.Equal(2, response.DesiredVerifiedCopies);
        Assert.Equal(2, response.RouteCount);
        Assert.Equal(1, response.PolicyVersion);
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.Equal(2, await verify.BackupPoolDestinations.CountAsync());
            Assert.False((await verify.BackupDestinations.SingleAsync(item => item.Id == sameDomain)).Enabled);
            Assert.True((await verify.BackupDestinations.SingleAsync(item => item.Id == first)).Enabled);
            Assert.True((await verify.BackupDestinations.SingleAsync(item => item.Id == second)).Enabled);
        }
        Assert.Equal(409, Assert.IsType<ConflictObjectResult>((await controller.CreateBackupPool(
            valid, CancellationToken.None)).Result).StatusCode);
    }

    [Fact]
    public async Task CreatePoolRejectsCredentialsFromDifferentKeyRingWithoutActivatingDestination()
    {
        var factory = Factory($"admin-pool-wrong-key-{Guid.NewGuid():N}");
        var creator = new AdminController(factory, null!, null!, null!, null!,
            Options.Create(new BackupOptions()), Options.Create(new CollectorOptions()),
            credentialProtectionProvider: new EphemeralDataProtectionProvider());
        var destination = Assert.IsType<BackupDestinationOverviewResponse>(Assert.IsType<ObjectResult>(
            (await creator.CreateS3BackupDestination(new CreateS3BackupDestinationRequest(
                "wrong-key", "https://s3.example.test", "eu-west-1", "private-backups",
                "backup", true, "test-access-key", "test-secret-key", 10), CancellationToken.None)).Result).Value);
        var otherKeyRing = new AdminController(factory, null!, null!, null!, null!,
            Options.Create(new BackupOptions()), Options.Create(new CollectorOptions()),
            credentialProtectionProvider: new EphemeralDataProtectionProvider());
        var request = new CreateBackupPoolRequest("wrong-key-pool", 1, 1, 3, 600, 900,
            [new CreateBackupPoolRouteRequest(destination.Id, 10, "primary", true)]);

        Assert.Equal(409, Assert.IsType<ObjectResult>((await otherKeyRing.CreateBackupPool(
            request, CancellationToken.None)).Result).StatusCode);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.BackupPools.ToArrayAsync());
            var saved = await db.BackupDestinations.SingleAsync();
            Assert.False(saved.Enabled);
            saved.FailureDomain = "s3:forged-independent-domain:eu-west-1";
            await db.SaveChangesAsync();
        }
        Assert.Equal(409, Assert.IsType<ObjectResult>((await creator.CreateBackupPool(
            request, CancellationToken.None)).Result).StatusCode);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.BackupPools.ToArrayAsync());
        Assert.False((await verify.BackupDestinations.SingleAsync()).Enabled);
    }

    [Fact]
    public async Task CreateS3DestinationEncryptsCredentialsAndLeavesRoutingDisabled()
    {
        var factory = Factory($"admin-create-s3-{Guid.NewGuid():N}");
        var protector = new EphemeralDataProtectionProvider();
        var controller = new AdminController(factory, null!, null!, null!, null!,
            Options.Create(new BackupOptions()), Options.Create(new CollectorOptions()),
            credentialProtectionProvider: protector);
        var request = new CreateS3BackupDestinationRequest(
            " Netherlands ", "https://s3.example.test", "eu-west-1", "private-backups",
            "proxyharbor-drill", true, "test-access-key", "test-secret-key", 10);

        var created = Assert.IsType<BackupDestinationOverviewResponse>(
            Assert.IsType<ObjectResult>((await controller.CreateS3BackupDestination(
                request, CancellationToken.None)).Result).Value);
        Assert.Equal("Netherlands", created.Name);
        Assert.False(created.Enabled);
        Assert.Empty(created.Routes);
        Assert.True(created.CredentialsConfigured);
        var response = JsonSerializer.Serialize(created);
        Assert.DoesNotContain(request.AccessKey!, response, StringComparison.Ordinal);
        Assert.DoesNotContain(request.SecretKey!, response, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Bucket!, response, StringComparison.Ordinal);

        await using var db = await factory.CreateDbContextAsync();
        var saved = Assert.Single(await db.BackupDestinations.ToArrayAsync());
        Assert.False(saved.Enabled);
        Assert.Empty(await db.BackupPoolDestinations.ToArrayAsync());
        Assert.Equal("s3:s3.example.test:eu-west-1", saved.FailureDomain);
        Assert.DoesNotContain(request.AccessKey!, saved.ProtectedSecrets, StringComparison.Ordinal);
        Assert.DoesNotContain(request.SecretKey!, saved.ProtectedSecrets, StringComparison.Ordinal);
        Assert.DoesNotContain(request.SecretKey!, saved.SettingsJson, StringComparison.Ordinal);
        var plaintext = protector.CreateProtector("ProxyHarbor.BackupDestination.Secrets.v1")
            .Unprotect(saved.ProtectedSecrets);
        Assert.Contains(request.AccessKey!, plaintext, StringComparison.Ordinal);
        Assert.Contains(request.SecretKey!, plaintext, StringComparison.Ordinal);

        Assert.Equal(409, Assert.IsType<ConflictObjectResult>((await controller.CreateS3BackupDestination(
            request, CancellationToken.None)).Result).StatusCode);
        Assert.Single(await db.BackupDestinations.ToArrayAsync());
    }

    [Fact]
    public async Task CreateS3DestinationRejectsUnsafeConfigurationWithoutSavingSecrets()
    {
        var factory = Factory($"admin-create-invalid-s3-{Guid.NewGuid():N}");
        var controller = new AdminController(factory, null!, null!, null!, null!,
            Options.Create(new BackupOptions()), Options.Create(new CollectorOptions()),
            credentialProtectionProvider: new EphemeralDataProtectionProvider());
        var request = new CreateS3BackupDestinationRequest(
            "unsafe", "http://127.0.0.1:9000", "eu-west-1", "private-backups",
            "../escape", true, "test-access-key", "test-secret-key", 0);

        Assert.Equal(400, Assert.IsType<ObjectResult>((await controller.CreateS3BackupDestination(
            request, CancellationToken.None)).Result).StatusCode);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.BackupDestinations.ToArrayAsync());
    }

    [Fact]
    public async Task DestinationOverviewShowsRoutesAndTypedOutcomeWithoutSecrets()
    {
        var factory = Factory($"admin-backup-destinations-{Guid.NewGuid():N}");
        var pool = new BackupPool
        {
            Name = "durable",
            RequiredVerifiedCopies = 1,
            DesiredVerifiedCopies = 2,
            PolicyVersion = 3
        };
        var destination = new BackupDestination
        {
            Name = "Netherlands",
            Kind = "s3",
            Enabled = true,
            Priority = 2,
            FailureDomain = "private-account-id",
            SettingsJson = "private-bucket-settings",
            ProtectedSecrets = "private-protected-credentials"
        };
        var latest = new BackupDestinationHealthOutcome
        {
            BackupDestinationId = destination.Id,
            Operation = "verify",
            Succeeded = false,
            ProbeOutcome = "mismatching",
            ErrorCode = "raw-provider-error-secret",
            ObservedAt = DateTimeOffset.UtcNow
        };
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.BackupPools.Add(pool);
            seed.BackupDestinations.Add(destination);
            seed.BackupPoolDestinations.Add(new BackupPoolDestination
            {
                BackupPoolId = pool.Id,
                BackupDestinationId = destination.Id,
                Role = "fallback",
                AllowedOperations = "verify,read",
                Priority = 1,
                Enabled = false,
                Draining = true
            });
            seed.BackupDestinationHealthOutcomes.AddRange(
                new BackupDestinationHealthOutcome
                {
                    BackupDestinationId = destination.Id,
                    Operation = "put",
                    Succeeded = true,
                    ObservedAt = latest.ObservedAt.AddMinutes(-1)
                }, latest);
            await seed.SaveChangesAsync();
        }

        var response = Assert.IsType<PagedResult<BackupDestinationOverviewResponse>>(
            Assert.IsType<OkObjectResult>((await Controller(factory, Path.GetTempPath())
                .BackupDestinations(1, 10, CancellationToken.None)).Result).Value);
        Assert.Equal(1, response.Total);
        var item = Assert.Single(response.Items);
        Assert.Equal(destination.Id, item.Id);
        Assert.True(item.CredentialsConfigured);
        Assert.True(item.FailureDomainConfigured);
        var route = Assert.Single(item.Routes);
        Assert.Equal(pool.Id, route.PoolId);
        Assert.Equal(3, route.PolicyVersion);
        Assert.Equal("fallback", route.Role);
        Assert.True(route.Draining);
        Assert.False(route.Enabled);
        Assert.Equal("mismatching", item.LastOutcome?.ProbeOutcome);
        Assert.Null(item.LastOutcome?.ErrorCode);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("private-account-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-bucket-settings", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-protected-credentials", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-provider-error-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectionDetailIsFailClosedForLegacyOrMissingRuns()
    {
        var factory = Factory($"admin-backup-protection-legacy-{Guid.NewGuid():N}");
        var run = new BackupRun { Status = "completed" };
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.BackupRuns.Add(run);
            await seed.SaveChangesAsync();
        }
        var controller = Controller(factory, Path.GetTempPath());

        Assert.IsType<NotFoundResult>((await controller.BackupProtectionDetail(
            Guid.NewGuid(), CancellationToken.None)).Result);
        var response = Assert.IsType<BackupProtectionDetailResponse>(
            Assert.IsType<OkObjectResult>((await controller.BackupProtectionDetail(
                run.Id, CancellationToken.None)).Result).Value);
        Assert.Equal("legacy_unassessed", response.Assessment);
        Assert.Null(response.State);
        Assert.Null(response.VerifiedIndependentCopies);
        Assert.Empty(response.Copies);
    }

    [Fact]
    public async Task ProtectionDetailShowsDebtWithoutLeakingProviderIdentityOrRawErrors()
    {
        var factory = Factory($"admin-backup-protection-{Guid.NewGuid():N}");
        var evaluator = new BackupProtectionEvaluator(factory, new BackupDestinationRegistry(
            [new MetadataAdapter("s3", true), new MetadataAdapter("telegram", false)]));
        var pool = new BackupPool
        {
            Name = "admin-detail",
            RequiredVerifiedCopies = 1,
            DesiredVerifiedCopies = 2,
            PolicyVersion = 2
        };
        var run = new BackupRun
        {
            Status = "completed",
            BackupPoolId = pool.Id,
            ProtectionPolicyVersion = 2,
            RequiredVerifiedCopies = 1,
            DesiredVerifiedCopies = 2,
            ContentSha256 = new string('a', 64),
            SizeBytes = 5
        };
        var destination = new BackupDestination
        {
            Kind = "s3",
            Name = "S3 Netherlands",
            Enabled = true,
            FailureDomain = "domain-a",
            SettingsJson = "secret-setting-sentinel",
            ProtectedSecrets = "encrypted-secret-sentinel"
        };
        var copy = new BackupCopy
        {
            BackupRunId = run.Id,
            BackupDestinationId = destination.Id,
            State = "verified",
            ContentSha256 = run.ContentSha256,
            SizeBytes = run.SizeBytes,
            PolicyVersion = 2,
            VerifiedAt = DateTimeOffset.UtcNow,
            NativeLocator = "private-account-sentinel/object.phbackup",
            LastErrorCode = "raw-provider-secret-sentinel"
        };
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.AddRange(pool, run, destination, copy,
                new BackupPoolDestination
                {
                    BackupPoolId = pool.Id,
                    BackupDestinationId = destination.Id,
                    Role = "primary",
                    AllowedOperations = "put,verify,read"
                });
            await seed.SaveChangesAsync();
        }
        var controller = Controller(factory, Path.GetTempPath(), evaluator);

        var response = Assert.IsType<BackupProtectionDetailResponse>(
            Assert.IsType<OkObjectResult>((await controller.BackupProtectionDetail(
                run.Id, CancellationToken.None)).Result).Value);
        Assert.Equal("evaluated", response.Assessment);
        Assert.Equal("degraded", response.State);
        Assert.Equal(1, response.VerifiedIndependentCopies);
        Assert.Equal(0, response.RequiredCopyDebt);
        Assert.Equal(1, response.DesiredCopyDebt);
        var detail = Assert.Single(response.Copies);
        Assert.True(detail.HasNativeLocator);
        Assert.Null(detail.ErrorCode);
        Assert.Equal("primary", detail.RouteRole);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("private-account-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted-secret-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-setting-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-provider-secret-sentinel", json, StringComparison.Ordinal);

        await using (var broken = await factory.CreateDbContextAsync())
        {
            (await broken.BackupDestinations.SingleAsync()).Kind = "unregistered";
            await broken.SaveChangesAsync();
        }
        var unknownAdapter = Assert.IsType<BackupProtectionDetailResponse>(
            Assert.IsType<OkObjectResult>((await controller.BackupProtectionDetail(
                run.Id, CancellationToken.None)).Result).Value);
        Assert.Equal("unknown_adapter", unknownAdapter.Assessment);
        Assert.Null(unknownAdapter.VerifiedIndependentCopies);

        await using (var broken = await factory.CreateDbContextAsync())
        {
            (await broken.BackupDestinations.SingleAsync()).Kind = "s3";
            (await broken.BackupRuns.SingleAsync()).ContentSha256 = "not-a-sha256";
            await broken.SaveChangesAsync();
        }
        var invalidPolicy = Assert.IsType<BackupProtectionDetailResponse>(
            Assert.IsType<OkObjectResult>((await controller.BackupProtectionDetail(
                run.Id, CancellationToken.None)).Result).Value);
        Assert.Equal("invalid_policy", invalidPolicy.Assessment);
        Assert.Null(invalidPolicy.VerifiedIndependentCopies);
    }

    [Fact]
    public async Task TelegramRecipientsPutXsenusFirstAndExcludeBlockedChats()
    {
        var factory = Factory($"admin-backup-recipients-{Guid.NewGuid():N}");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.TelegramChats.AddRange(
                new TelegramChat
                {
                    ChatId = 1,
                    TelegramUserId = 1,
                    UserId = Guid.NewGuid(),
                    DisplayName = "Другой администратор",
                    Username = "recent",
                    LastInteractionAt = DateTimeOffset.UtcNow
                },
                new TelegramChat
                {
                    ChatId = 2,
                    TelegramUserId = 2,
                    UserId = Guid.NewGuid(),
                    DisplayName = "Илья Телятников",
                    Username = "Xsenus",
                    LastInteractionAt = DateTimeOffset.UtcNow.AddDays(-1)
                },
                new TelegramChat
                {
                    ChatId = 3,
                    TelegramUserId = 3,
                    UserId = Guid.NewGuid(),
                    DisplayName = "Заблокирован",
                    Username = "blocked",
                    IsBlocked = true
                });
            await db.SaveChangesAsync();
        }
        var controller = Controller(factory, Path.GetTempPath());

        var result = await controller.BackupTelegramRecipients(token: CancellationToken.None);
        var recipients = Assert.IsType<TelegramBackupRecipientResponse[]>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(2, recipients.Length);
        Assert.Equal("Xsenus", recipients[0].Username);
        Assert.True(recipients[0].IsDefault);
        Assert.DoesNotContain(recipients, item => item.Username == "blocked");
    }

    [Fact]
    public async Task TelegramRecipientSearchMatchesNameAndUsername()
    {
        var factory = Factory($"admin-backup-recipient-search-{Guid.NewGuid():N}");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.TelegramChats.AddRange(
                new TelegramChat
                {
                    ChatId = 11,
                    TelegramUserId = 11,
                    UserId = Guid.NewGuid(),
                    DisplayName = "Илья Телятников",
                    Username = null
                },
                new TelegramChat
                {
                    ChatId = 12,
                    TelegramUserId = 12,
                    UserId = Guid.NewGuid(),
                    DisplayName = "Оператор",
                    Username = "Xsenus"
                },
                new TelegramChat
                {
                    ChatId = 13,
                    TelegramUserId = 13,
                    UserId = Guid.NewGuid(),
                    DisplayName = "Не подходит",
                    Username = "someone"
                });
            await db.SaveChangesAsync();
        }
        var controller = Controller(factory, Path.GetTempPath());

        var byName = Assert.IsType<TelegramBackupRecipientResponse[]>(
            Assert.IsType<OkObjectResult>((await controller.BackupTelegramRecipients(
                "Илья", CancellationToken.None)).Result).Value);
        var byUsername = Assert.IsType<TelegramBackupRecipientResponse[]>(
            Assert.IsType<OkObjectResult>((await controller.BackupTelegramRecipients(
                "Xsenus", CancellationToken.None)).Result).Value);

        Assert.Single(byName);
        Assert.Null(byName[0].Username);
        Assert.Single(byUsername);
        Assert.Equal("Xsenus", byUsername[0].Username);
    }

    [Fact]
    public async Task SettingsWithoutCrmDialogReturnNoDefaultRecipient()
    {
        var factory = Factory($"admin-backup-no-recipient-{Guid.NewGuid():N}");
        var controller = Controller(factory, Path.GetTempPath());

        var response = Assert.IsType<BackupSettingsResponse>(
            Assert.IsType<OkObjectResult>((await controller.BackupSettings(
                CancellationToken.None)).Result).Value);

        Assert.Null(response.TelegramRecipientId);
        Assert.False(response.TelegramBotConfigured);
    }

    [Fact]
    public async Task SettingsRejectTelegramWhenRuntimeStoreOrMainBotResolverIsUnavailable()
    {
        var factory = Factory($"admin-backup-unavailable-{Guid.NewGuid():N}");
        var configured = Options.Create(new BackupOptions
        {
            Directory = Path.GetTempPath(),
            EncryptionKey = new string('k', BackupOptions.MinimumEncryptionKeyLength)
        });
        var noStore = new AdminController(factory, null!, null!, null!, null!, configured,
            Options.Create(new CollectorOptions()));
        var unavailable = await noStore.UpdateBackupSettings(new BackupSettingsRequest(
            false, 24, 7, 365, 49, false, null), CancellationToken.None);
        Assert.Equal(503, Assert.IsType<ObjectResult>(unavailable.Result).StatusCode);

        var store = new BackupConfigurationStore(factory, configured,
            DataProtectionProvider.Create(Path.Combine(Path.GetTempPath(), $"backup-no-bot-{Guid.NewGuid():N}")));
        var noResolver = new AdminController(factory, null!, null!, null!, null!, configured,
            Options.Create(new CollectorOptions()), store);
        var noRecipient = await noResolver.UpdateBackupSettings(new BackupSettingsRequest(
            false, 24, 7, 365, 49, true, null), CancellationToken.None);
        Assert.Equal(400, Assert.IsType<ObjectResult>(noRecipient.Result).StatusCode);
        var noBot = await noResolver.UpdateBackupSettings(new BackupSettingsRequest(
            false, 24, 7, 365, 49, true, Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(503, Assert.IsType<ObjectResult>(noBot.Result).StatusCode);
    }

    [Fact]
    public async Task ListDownloadAndDeleteOperateOnlyOnPublishedBackupFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-admin-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string fileName = "proxyharbor-20260826-123456-1234.phbackup";
            var path = Path.Combine(directory, fileName);
            var payload = new byte[] { 0x50, 0x48, 0x42, 0x33, 1, 2, 3, 4 };
            await File.WriteAllBytesAsync(path, payload);
            var factory = Factory($"admin-backups-{Guid.NewGuid():N}");
            var run = new BackupRun
            {
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                FinishedAt = DateTimeOffset.UtcNow,
                Status = "completed",
                FileName = fileName,
                SizeBytes = payload.Length
            };
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.BackupRuns.Add(run);
                await seed.SaveChangesAsync();
            }
            var controller = Controller(factory, directory);

            var listAction = await controller.Backups(token: CancellationToken.None);
            var page = Assert.IsType<PagedResult<BackupFileResponse>>(
                Assert.IsType<OkObjectResult>(listAction.Result).Value);
            Assert.Single(page.Items);
            Assert.True(page.Items[0].Available);

            var download = Assert.IsType<FileStreamResult>(
                await controller.DownloadBackup(run.Id, CancellationToken.None));
            Assert.Equal(fileName, download.FileDownloadName);
            Assert.True(download.EnableRangeProcessing);
            await using (download.FileStream)
            {
                var downloaded = new byte[payload.Length];
                await download.FileStream.ReadExactlyAsync(downloaded);
                Assert.Equal(payload, downloaded);
            }

            Assert.IsType<NoContentResult>(await controller.DeleteBackup(run.Id, CancellationToken.None));
            Assert.False(File.Exists(path));
            await using var verify = await factory.CreateDbContextAsync();
            Assert.False(await verify.BackupRuns.AnyAsync());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("../proxyharbor-20260826-123456-1234.phbackup")]
    [InlineData("proxyharbor-manual.phbackup")]
    [InlineData("proxyharbor-20260826-123456-1234.phbackup.partial")]
    public void ResolverRejectsTraversalAndNonPublishedNames(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-path-check-{Guid.NewGuid():N}");
        Assert.False(BackupService.TryResolvePublishedBackupPath(directory, fileName, out _));
    }

    [Fact]
    public async Task SettingsUseMainBotAndPersistOnlySelectedCrmRecipient()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-backup-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var factory = Factory($"admin-backup-settings-{Guid.NewGuid():N}");
            var configured = Options.Create(new BackupOptions
            {
                Directory = directory,
                EncryptionKey = new string('k', BackupOptions.MinimumEncryptionKeyLength)
            });
            var store = new BackupConfigurationStore(factory, configured, DataProtectionProvider.Create(directory));
            var recipient = await SeedRecipientAsync(factory);
            var resolver = new TelegramResolver(recipient.Id);
            var controller = new AdminController(factory, null!, null!, null!, null!, configured,
                Options.Create(new CollectorOptions()), store, resolver);

            var result = await controller.UpdateBackupSettings(new BackupSettingsRequest(
                Enabled: true, IntervalHours: 12, RetentionDays: 14, HistoryRetentionDays: 180,
                MaxTelegramFileSizeMb: 40, SendToTelegram: true,
                TelegramRecipientId: recipient.Id),
                CancellationToken.None);
            var response = Assert.IsType<BackupSettingsResponse>(
                Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.True(response.Enabled);
            Assert.True(response.TelegramBotConfigured);
            Assert.Equal(recipient.Id, response.TelegramRecipientId);
            Assert.Equal("Илья Телятников", response.TelegramRecipientDisplayName);
            Assert.Equal("Xsenus", response.TelegramRecipientUsername);

            var persisted = await store.GetAsync();
            Assert.Equal(12, persisted.IntervalHours);
            Assert.Equal(recipient.Id, persisted.TelegramRecipientId);
            Assert.Null(persisted.TelegramBotToken);
            Assert.Null(persisted.TelegramChatId);
            await using var db = await factory.CreateDbContextAsync();
            var entity = await db.BackupConfigurations.SingleAsync();
            Assert.DoesNotContain("123456789:abcdefghijklmnopqrstuvwxyz", entity.ProtectedSecrets, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsPersistObjectStorageCredentialsOnlyInProtectedPayload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-backup-s3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var factory = Factory($"admin-backup-s3-{Guid.NewGuid():N}");
            var configured = Options.Create(new BackupOptions
            {
                Directory = directory,
                EncryptionKey = new string('k', BackupOptions.MinimumEncryptionKeyLength)
            });
            var store = new BackupConfigurationStore(factory, configured, DataProtectionProvider.Create(directory));
            var controller = new AdminController(factory, null!, null!, null!, null!, configured,
                Options.Create(new CollectorOptions()), store);
            const string accessKey = "s3-test-access-key";
            const string secretKey = "s3-test-secret-key";

            var result = await controller.UpdateBackupSettings(new BackupSettingsRequest(
                Enabled: true, IntervalHours: 24, RetentionDays: 7, HistoryRetentionDays: 365,
                MaxTelegramFileSizeMb: 49, SendToTelegram: false, TelegramRecipientId: null,
                SendToObjectStorage: true,
                ObjectStorageEndpoint: "https://storage.yandexcloud.net",
                ObjectStorageRegion: "ru-central1",
                ObjectStorageBucket: "proxyharbor-backups",
                ObjectStoragePrefix: "production/backups",
                ObjectStorageUsePathStyle: true,
                ObjectStorageAccessKey: accessKey,
                ObjectStorageSecretKey: secretKey), CancellationToken.None);

            var response = Assert.IsType<BackupSettingsResponse>(
                Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.True(response.SendToObjectStorage);
            Assert.True(response.ObjectStorageCredentialsConfigured);
            var persisted = await store.GetAsync();
            Assert.Equal(accessKey, persisted.ObjectStorageAccessKey);
            Assert.Equal(secretKey, persisted.ObjectStorageSecretKey);
            await using var db = await factory.CreateDbContextAsync();
            var entity = await db.BackupConfigurations.SingleAsync();
            Assert.DoesNotContain(accessKey, entity.ProtectedSecrets, StringComparison.Ordinal);
            Assert.DoesNotContain(secretKey, entity.ProtectedSecrets, StringComparison.Ordinal);
            Assert.DoesNotContain(accessKey, entity.SettingsJson, StringComparison.Ordinal);
            Assert.DoesNotContain(secretKey, entity.SettingsJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsValidateEveryPolicyBoundaryAndAllowClearingTelegram()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-backup-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var factory = Factory($"admin-backup-policy-{Guid.NewGuid():N}");
            var configured = Options.Create(new BackupOptions
            {
                Directory = directory,
                EncryptionKey = new string('k', BackupOptions.MinimumEncryptionKeyLength)
            });
            var store = new BackupConfigurationStore(factory, configured, DataProtectionProvider.Create(directory));
            var recipient = await SeedRecipientAsync(factory);
            var resolver = new TelegramResolver(recipient.Id);
            var controller = new AdminController(factory, null!, null!, null!, null!, configured,
                Options.Create(new CollectorOptions()), store, resolver);

            var initial = Assert.IsType<BackupSettingsResponse>(
                Assert.IsType<OkObjectResult>((await controller.BackupSettings(CancellationToken.None)).Result).Value);
            Assert.False(initial.SendToTelegram);

            var invalid = new[]
            {
                new BackupSettingsRequest(false, 0, 7, 365, 49, false, null),
                new BackupSettingsRequest(false, 24, 0, 365, 49, false, null),
                new BackupSettingsRequest(false, 24, 7, 0, 49, false, null),
                new BackupSettingsRequest(false, 24, 7, 365, 50, false, null),
                new BackupSettingsRequest(true, 24, 7, 365, 49, false, null),
                new BackupSettingsRequest(false, 24, 7, 365, 49, true, Guid.NewGuid())
            };
            foreach (var request in invalid)
            {
                var action = await controller.UpdateBackupSettings(request, CancellationToken.None);
                Assert.Equal(400, Assert.IsType<ObjectResult>(action.Result).StatusCode);
            }

            var configuredResult = await controller.UpdateBackupSettings(new BackupSettingsRequest(
                false, 24, 7, 365, 49, true,
                recipient.Id), CancellationToken.None);
            Assert.IsType<OkObjectResult>(configuredResult.Result);
            var preservedResult = await controller.UpdateBackupSettings(new BackupSettingsRequest(
                false, 12, 14, 180, 40, true, recipient.Id), CancellationToken.None);
            Assert.IsType<OkObjectResult>(preservedResult.Result);
            Assert.Equal(recipient.Id, (await store.GetAsync()).TelegramRecipientId);

            var clearedResult = await controller.UpdateBackupSettings(new BackupSettingsRequest(
                false, 12, 14, 180, 40, false, null),
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(clearedResult.Result);
            Assert.Null((await store.GetAsync()).TelegramRecipientId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteRemovesAuditWhenRetentionAlreadyRemovedLocalArchive()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-admin-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var factory = Factory($"admin-backups-missing-{Guid.NewGuid():N}");
            var run = new BackupRun
            {
                StartedAt = DateTimeOffset.UtcNow.AddDays(-10),
                FinishedAt = DateTimeOffset.UtcNow.AddDays(-10),
                Status = "completed",
                FileName = "proxyharbor-20260816-123456-1234.phbackup"
            };
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.BackupRuns.Add(run);
                await seed.SaveChangesAsync();
            }
            var controller = Controller(factory, directory);
            Assert.IsType<NoContentResult>(await controller.DeleteBackup(run.Id, CancellationToken.None));
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Empty(await verify.BackupRuns.ToListAsync());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static AdminController Controller(IDbContextFactory<ProxyHarborDbContext> factory,
        string directory, BackupProtectionEvaluator? evaluator = null) =>
        new(factory, null!, null!, null!, null!, Options.Create(new BackupOptions { Directory = directory }),
            Options.Create(new CollectorOptions()), backupProtectionEvaluator: evaluator);

    private sealed class MetadataAdapter(string kind, bool canVerify) : IBackupDestinationAdapter
    {
        public string Kind { get; } = kind;
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(true), new(canVerify), new(false), false, false, false);
    }

    private static async Task<TelegramChat> SeedRecipientAsync(InMemoryFactory factory)
    {
        var recipient = new TelegramChat
        {
            ChatId = 123456789,
            TelegramUserId = 123456789,
            UserId = Guid.NewGuid(),
            DisplayName = "Илья Телятников",
            Username = "Xsenus",
            LastInteractionAt = DateTimeOffset.UtcNow
        };
        await using var db = await factory.CreateDbContextAsync();
        db.TelegramChats.Add(recipient);
        await db.SaveChangesAsync();
        return recipient;
    }

    private sealed class TelegramResolver(Guid recipientId) : ITelegramBackupDeliveryResolver
    {
        public Task<TelegramBackupDelivery> ResolveAsync(Guid candidate, CancellationToken token = default) =>
            candidate == recipientId
                ? Task.FromResult(new TelegramBackupDelivery(
                    candidate,
                    "123456789:abcdefghijklmnopqrstuvwxyz",
                    "123456789",
                    "Илья Телятников",
                    "Xsenus"))
                : Task.FromException<TelegramBackupDelivery>(
                    new InvalidOperationException("Диалог не найден."));
    }

    private static InMemoryFactory Factory(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase(databaseName).Options;
        return new InMemoryFactory(options);
    }

    private sealed class InMemoryFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
