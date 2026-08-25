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
