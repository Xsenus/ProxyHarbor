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
    public async Task SettingsPersistProtectedTelegramCredentialsAndCanBeReadWithoutToken()
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
            var controller = new AdminController(factory, null!, null!, null!, null!, configured,
                Options.Create(new CollectorOptions()), store);

            var result = await controller.UpdateBackupSettings(new BackupSettingsRequest(
                Enabled: true, IntervalHours: 12, RetentionDays: 14, HistoryRetentionDays: 180,
                MaxTelegramFileSizeMb: 40, SendToTelegram: true,
                TelegramBotToken: "123456789:abcdefghijklmnopqrstuvwxyz", TelegramChatId: "-1001234567890"),
                CancellationToken.None);
            var response = Assert.IsType<BackupSettingsResponse>(
                Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.True(response.Enabled);
            Assert.True(response.TelegramBotTokenConfigured);
            Assert.Equal("-1001234567890", response.TelegramChatId);

            var persisted = await store.GetAsync();
            Assert.Equal(12, persisted.IntervalHours);
            Assert.Equal("123456789:abcdefghijklmnopqrstuvwxyz", persisted.TelegramBotToken);
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
            var controller = new AdminController(factory, null!, null!, null!, null!, configured,
                Options.Create(new CollectorOptions()), store);

            var initial = Assert.IsType<BackupSettingsResponse>(
                Assert.IsType<OkObjectResult>((await controller.BackupSettings(CancellationToken.None)).Result).Value);
            Assert.False(initial.SendToTelegram);

            var invalid = new[]
            {
                new BackupSettingsRequest(false, 0, 7, 365, 49, false, null, null),
                new BackupSettingsRequest(false, 24, 0, 365, 49, false, null, null),
                new BackupSettingsRequest(false, 24, 7, 0, 49, false, null, null),
                new BackupSettingsRequest(false, 24, 7, 365, 50, false, null, null),
                new BackupSettingsRequest(true, 24, 7, 365, 49, false, null, null),
                new BackupSettingsRequest(false, 24, 7, 365, 49, true, "bad", "bad")
            };
            foreach (var request in invalid)
            {
                var action = await controller.UpdateBackupSettings(request, CancellationToken.None);
                Assert.Equal(400, Assert.IsType<ObjectResult>(action.Result).StatusCode);
            }

            var configuredResult = await controller.UpdateBackupSettings(new BackupSettingsRequest(
                false, 24, 7, 365, 49, true,
                "123456789:abcdefghijklmnopqrstuvwxyz", "-1001234567890"), CancellationToken.None);
            Assert.IsType<OkObjectResult>(configuredResult.Result);
            var preservedResult = await controller.UpdateBackupSettings(new BackupSettingsRequest(
                false, 12, 14, 180, 40, true, null, null), CancellationToken.None);
            Assert.IsType<OkObjectResult>(preservedResult.Result);
            Assert.Equal("123456789:abcdefghijklmnopqrstuvwxyz", (await store.GetAsync()).TelegramBotToken);

            var clearedResult = await controller.UpdateBackupSettings(new BackupSettingsRequest(
                false, 12, 14, 180, 40, false, null, null, ClearTelegramCredentials: true),
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(clearedResult.Result);
            Assert.Null((await store.GetAsync()).TelegramBotToken);
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
