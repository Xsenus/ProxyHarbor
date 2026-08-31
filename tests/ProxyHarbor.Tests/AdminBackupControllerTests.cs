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

    private static AdminController Controller(IDbContextFactory<ProxyHarborDbContext> factory, string directory) =>
        new(factory, null!, null!, null!, null!, Options.Create(new BackupOptions { Directory = directory }),
            Options.Create(new CollectorOptions()));

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
