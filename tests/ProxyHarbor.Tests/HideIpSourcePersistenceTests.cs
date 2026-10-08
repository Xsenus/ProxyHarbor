using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class HideIpSourcePersistenceTests
{
    [Theory]
    [Trait("Category", "PostgresIntegration")]
    [InlineData("http.txt", ProxyProtocol.Http)]
    [InlineData("https.txt", ProxyProtocol.HttpTls)]
    [InlineData("socks4.txt", ProxyProtocol.Socks4)]
    [InlineData("socks5.txt", ProxyProtocol.Socks5)]
    public async Task RepeatedStartupPreservesSupportedSourceHistoryAndPendingSnapshot(string file, ProxyProtocol protocol)
    {
        var connection = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var schema = $"proxyharbor_hideip_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>().UseNpgsql(builder.ConnectionString).Options;
            var observed = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
            var source = new ProxySource
            {
                Name = "Custom hideip",
                Url = "https://raw.githubusercontent.com/zloi-user/hideip.me/main/" + file,
                DefaultProtocol = protocol,
                Enabled = false,
                Priority = 999,
                LastFetchedAt = observed,
                LastSucceededAt = observed,
                HttpETag = "\"saved\"",
                LastError = "temporary failure",
                ConsecutiveFailures = 2,
                NextFetchAt = observed.AddHours(1)
            };
            var snapshot = ProxyCandidateSnapshotCodec.Encode("8.8.8.8:8080\n1.1.1.1:1080", protocol);
            var state = new ProxySourceImportState
            {
                ProxySourceId = source.Id,
                SourceUrl = source.Url,
                SourceProtocol = protocol,
                CandidateCount = snapshot.Count,
                NextIndex = 1,
                Payload = snapshot.Payload,
                PayloadHash = System.Security.Cryptography.SHA256.HashData(snapshot.Payload),
                CreatedAt = observed,
                LastProgressAt = observed
            };
            await using (var first = new ProxyHarborDbContext(options))
            {
                await DatabaseSeeder.InitializeAsync(first);
                first.Sources.Add(source);
                first.ProxySourceImportStates.Add(state);
                await first.SaveChangesAsync();
            }
            for (var pass = 0; pass < 2; pass++)
            {
                await using var restart = new ProxyHarborDbContext(options);
                await DatabaseSeeder.InitializeAsync(restart);
                var saved = await restart.Sources.SingleAsync(row => row.Url == source.Url);
                Assert.Equal(source.Id, saved.Id);
                Assert.Equal(source.Name, saved.Name);
                Assert.Equal(protocol, saved.DefaultProtocol);
                Assert.False(saved.Enabled);
                Assert.Equal(999, saved.Priority);
                Assert.Equal(observed, saved.LastFetchedAt);
                Assert.Equal(observed, saved.LastSucceededAt);
                Assert.Equal("\"saved\"", saved.HttpETag);
                Assert.Equal("temporary failure", saved.LastError);
                Assert.Equal(2, saved.ConsecutiveFailures);
                Assert.Equal(observed.AddHours(1), saved.NextFetchAt);
                var pending = await restart.ProxySourceImportStates.SingleAsync(row => row.ProxySourceId == source.Id);
                Assert.Equal(state.SnapshotId, pending.SnapshotId);
                Assert.Equal(source.Url, pending.SourceUrl);
                Assert.Equal(protocol, pending.SourceProtocol);
                Assert.Equal(2, pending.CandidateCount);
                Assert.Equal(1, pending.NextIndex);
                Assert.Equal(snapshot.Payload, pending.Payload);
                Assert.Equal(state.PayloadHash, pending.PayloadHash);
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
