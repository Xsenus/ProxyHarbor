using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Перенос опубликованных Gamt-файлов сохраняет настройки операторских лент.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class GamtSourceRelocationIntegrationTests
{
    [Theory]
    [InlineData("socks4", ProxyProtocol.Socks4, false)]
    [InlineData("socks5", ProxyProtocol.Socks5, true)]
    [Trait("Category", "PostgresIntegration")]
    public async Task StartupPreservesMovedOperatorFeedAndIsIdempotent(string kind, ProxyProtocol protocol, bool enabled)
    {
        var connection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;

        var schema = $"proxyharbor_gamt_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>().UseNpgsql(builder.ConnectionString).Options;
            var id = Guid.NewGuid();
            var fetchedAt = DateTimeOffset.UtcNow;
            var movedUrl = $"https://raw.githubusercontent.com/Denisyoya/Proxy-List-Gamt/main/results/txt/{kind}.txt";
            const string customUrl = "https://example.com/operator-feed.txt";
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                first.Sources.AddRange(
                    new ProxySource
                    {
                        Id = id,
                        Name = "Operator Gamt",
                        DefaultProtocol = protocol,
                        Url = $"https://raw.githubusercontent.com/Denisyoya/Proxy-List-Gamt/main/proxy/{kind}.txt",
                        Enabled = enabled,
                        Priority = 4321,
                        LastFetchedAt = fetchedAt,
                        LastSucceededAt = fetchedAt,
                        LastItemCount = 123,
                        HttpETag = "old-resource",
                        HttpLastModifiedAt = fetchedAt,
                        ConsecutiveFailures = 4,
                        NextFetchAt = fetchedAt.AddHours(2),
                        LastError = "HTTP 404"
                    },
                    new ProxySource { Name = "Unrelated operator feed", Url = customUrl, Enabled = false });
                await first.SaveChangesAsync();
            }
            await using (var second = new ProxyHarborDbContext(options))
                await DatabaseSeeder.InitializeAsync(second);
            await using (var third = new ProxyHarborDbContext(options))
                await DatabaseSeeder.InitializeAsync(third);

            await using var verify = new ProxyHarborDbContext(options);
            var migrated = await verify.Sources.SingleAsync(source => source.Id == id);
            Assert.Equal(movedUrl, migrated.Url);
            Assert.Equal("Operator Gamt", migrated.Name);
            Assert.Equal(protocol, migrated.DefaultProtocol);
            Assert.Equal(enabled, migrated.Enabled);
            Assert.Equal(4321, migrated.Priority);
            Assert.Equal(123, migrated.LastItemCount);
            Assert.InRange(Math.Abs((migrated.LastFetchedAt!.Value - fetchedAt).TotalMilliseconds), 0, 0.001);
            Assert.InRange(Math.Abs((migrated.LastSucceededAt!.Value - fetchedAt).TotalMilliseconds), 0, 0.001);
            Assert.Null(migrated.HttpETag);
            Assert.Null(migrated.HttpLastModifiedAt);
            Assert.Equal(0, migrated.ConsecutiveFailures);
            Assert.Null(migrated.NextFetchAt);
            Assert.Null(migrated.LastError);
            Assert.False((await verify.Sources.SingleAsync(source => source.Url == customUrl)).Enabled);
            Assert.Equal(BuiltInSourceCatalog.Sources.Count + 3, await verify.Sources.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
