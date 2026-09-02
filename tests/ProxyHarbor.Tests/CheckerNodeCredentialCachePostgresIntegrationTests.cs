using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class CheckerNodeCredentialCachePostgresIntegrationTests
{
    private const string FirstToken = "postgres-checker-token-1234567890-abcdefghijkl";
    private const string SecondToken = "postgres-checker-token-0987654321-abcdefghijkl";

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ConcurrentAuthenticationUsesOneNarrowPostgresReaderPerGeneration()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_checker_credentials_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var seedOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString).Options;
            var first = Node("first", FirstToken);
            var second = Node("second", SecondToken);
            await using (var seed = new ProxyHarborDbContext(seedOptions))
            {
                await seed.Database.MigrateAsync();
                seed.CheckerNodes.AddRange(first, second, Node("disabled", FirstToken, enabled: false));
                await seed.SaveChangesAsync();
            }

            var commandBudget = new CredentialReadInterceptor();
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString, npgsql =>
                    npgsql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), null))
                .AddInterceptors(commandBudget)
                .Options;
            using var cache = new CheckerNodeCredentialCache(new TestDbFactory(options), TimeProvider.System);

            var accepted = await Task.WhenAll(Enumerable.Range(0, 64).Select(index =>
                cache.AuthenticateAsync(
                    index % 2 == 0 ? first.Id : second.Id,
                    index % 2 == 0 ? FirstToken : SecondToken,
                    default).AsTask()));
            Assert.All(accepted, Assert.True);
            Assert.False(await cache.AuthenticateAsync(first.Id, SecondToken, default));
            Assert.Equal(1, commandBudget.Reads);
            Assert.Contains("\"Id\"", commandBudget.LastCommand, StringComparison.Ordinal);
            Assert.Contains("\"TokenHash\"", commandBudget.LastCommand, StringComparison.Ordinal);
            Assert.Contains("\"Enabled\"", commandBudget.LastCommand, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT *", commandBudget.LastCommand, StringComparison.OrdinalIgnoreCase);

            cache.Invalidate();
            Assert.True(await cache.AuthenticateAsync(first.Id, FirstToken, default));
            Assert.Equal(2, commandBudget.Reads);
            Assert.Equal(2, cache.DatabaseReads);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static CheckerNode Node(string name, string token, bool enabled = true) => new()
    {
        Name = name,
        Host = "203.0.113.20",
        SshUsername = "root",
        TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
        Enabled = enabled
    };

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class CredentialReadInterceptor : DbCommandInterceptor
    {
        private int reads;
        private string? lastCommand;
        internal int Reads => Volatile.Read(ref reads);
        internal string LastCommand => Volatile.Read(ref lastCommand) ?? string.Empty;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"CheckerNodes\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref reads);
                Volatile.Write(ref lastCommand, command.CommandText);
            }
            return ValueTask.FromResult(result);
        }
    }
}
