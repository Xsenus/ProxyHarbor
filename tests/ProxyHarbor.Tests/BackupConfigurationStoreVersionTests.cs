using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class BackupConfigurationStoreVersionTests
{
    [Fact]
    public async Task ReadsLegacyVersionOneAndRejectsUnknownFutureVersion()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"backup-settings-version-{Guid.NewGuid():N}")
            .Options;
        var factory = new TestDbFactory(options);
        var store = new BackupConfigurationStore(
            factory,
            Options.Create(new BackupOptions()),
            new EphemeralDataProtectionProvider());
        await store.SaveAsync(new BackupOptions { IntervalHours = 12 });

        await using (var db = new ProxyHarborDbContext(options))
        {
            var entity = await db.BackupConfigurations.SingleAsync();
            entity.SettingsJson = entity.SettingsJson.Replace(
                ",\"schemaVersion\":2", string.Empty, StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }
        Assert.Equal(12, (await store.GetAsync()).IntervalHours);

        await using (var db = new ProxyHarborDbContext(options))
        {
            var entity = await db.BackupConfigurations.SingleAsync();
            entity.SettingsJson = entity.SettingsJson.TrimEnd('}') + ",\"schemaVersion\":99}";
            await db.SaveChangesAsync();
        }
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync());
        Assert.Contains("повреждены или больше не расшифровываются", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
