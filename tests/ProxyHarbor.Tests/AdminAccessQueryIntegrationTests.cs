using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует двухзапросный snapshot и bounded account lookup в access registry.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class AdminAccessQueryIntegrationTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task AccessRegistriesUseTwoSnapshotReadersAndBoundedAccountLookup()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_access_queries_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var seedOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var administrator = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "administrator",
                NormalizedUserName = "ADMINISTRATOR",
                Email = "administrator@example.test",
                NormalizedEmail = "ADMINISTRATOR@EXAMPLE.TEST",
                ReferralCode = "adminq1"
            };
            var member = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "latest-member",
                NormalizedUserName = "LATEST-MEMBER",
                Email = "latest@example.test",
                NormalizedEmail = "LATEST@EXAMPLE.TEST",
                ReferralCode = "memberq1"
            };
            var now = DateTimeOffset.UtcNow;
            await using (var seed = new ProxyHarborDbContext(seedOptions))
            {
                await seed.Database.MigrateAsync();
                seed.Users.AddRange(administrator, member);
                seed.ProxyAccessBuckets.AddRange(
                    Bucket("203.0.113.10", "catalog", 2, 20, now.AddMinutes(-10)),
                    Bucket("203.0.113.10", "export", 3, 30, now.AddMinutes(-5), member.Id),
                    Bucket("203.0.113.20", "catalog", 5, 50, now.AddMinutes(-4)),
                    Bucket("203.0.113.10", "page:home", 7, 0, now.AddMinutes(-10)),
                    Bucket("203.0.113.10", "page:account", 11, 0, now.AddMinutes(-3), member.Id),
                    Bucket("203.0.113.20", "page:home", 13, 0, now.AddHours(-25)));
                seed.AccessBlockRules.AddRange(
                    new AccessBlockRule
                    {
                        Kind = AccessBlockKinds.Ip,
                        Value = "203.0.113.10",
                        Reason = "integration test",
                        AdministratorId = administrator.Id
                    },
                    new AccessBlockRule
                    {
                        Kind = AccessBlockKinds.User,
                        Value = member.Id.ToString(),
                        UserId = member.Id,
                        Reason = "integration test",
                        AdministratorId = administrator.Id
                    });
                await seed.SaveChangesAsync();
            }

            await using (var indexConnection = new NpgsqlConnection(builder.ConnectionString))
            {
                await indexConnection.OpenAsync();
                await using var indexCommand = new NpgsqlCommand("""
                    SELECT indexdef
                    FROM pg_indexes
                    WHERE schemaname = current_schema()
                      AND indexname = 'IX_ProxyAccessBuckets_IpAddress_LastSeenAt_Id'
                    """, indexConnection);
                var indexDefinition = Assert.IsType<string>(await indexCommand.ExecuteScalarAsync());
                Assert.Contains("\"IpAddress\", \"LastSeenAt\" DESC, \"Id\" DESC", indexDefinition, StringComparison.Ordinal);
                Assert.Contains("INCLUDE (\"UserId\", \"Endpoint\")", indexDefinition, StringComparison.Ordinal);
                Assert.Contains("WHERE (\"UserId\" IS NOT NULL)", indexDefinition, StringComparison.Ordinal);
            }

            var reads = new RelationReadCounter();
            var measuredOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Throw(
                    CoreEventId.RowLimitingOperationWithoutOrderByWarning))
                .AddInterceptors(reads)
                .Options;
            var factory = new TestDbFactory(measuredOptions);
            await using var db = new ProxyHarborDbContext(measuredOptions);
            var controller = new AdminAccessController(db,
                new ProxyAccessMonitor(factory, NullLogger<ProxyAccessMonitor>.Instance));

            reads.Reset();
            var traffic = Assert.IsType<OkObjectResult>(await controller.List(token: CancellationToken.None));
            AssertAccessBudget(reads, expectedRuleReaders: 2);
            using (var json = JsonDocument.Parse(JsonSerializer.Serialize(traffic.Value, WebJsonOptions)))
            {
                var root = json.RootElement;
                Assert.Equal(2, root.GetProperty("total").GetInt32());
                Assert.Equal(10, root.GetProperty("summary").GetProperty("requests").GetInt64());
                Assert.Equal(100, root.GetProperty("summary").GetProperty("proxyItems").GetInt64());
                Assert.Equal(2, root.GetProperty("summary").GetProperty("activeRules").GetInt32());
                Assert.Contains(root.GetProperty("items").EnumerateArray(), item =>
                    item.GetProperty("ipAddress").GetString() == "203.0.113.10" &&
                    item.GetProperty("email").GetString() == member.Email &&
                    item.GetProperty("isBlocked").GetBoolean());
            }

            reads.Reset();
            var visitors = Assert.IsType<OkObjectResult>(await controller.Visitors(token: CancellationToken.None));
            AssertAccessBudget(reads, expectedRuleReaders: 1);
            using var visitorJson = JsonDocument.Parse(JsonSerializer.Serialize(visitors.Value, WebJsonOptions));
            var visitorRoot = visitorJson.RootElement;
            Assert.Equal(2, visitorRoot.GetProperty("total").GetInt32());
            Assert.Equal(31, visitorRoot.GetProperty("summary").GetProperty("pageViews").GetInt64());
            Assert.Equal(1, visitorRoot.GetProperty("summary").GetProperty("authenticatedVisitors").GetInt32());
            Assert.Equal(1, visitorRoot.GetProperty("summary").GetProperty("active24Hours").GetInt32());
            Assert.Contains(visitorRoot.GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("ipAddress").GetString() == "203.0.113.10" &&
                item.GetProperty("email").GetString() == member.Email &&
                item.GetProperty("isBlocked").GetBoolean());

            await db.ProxyAccessBuckets.ExecuteDeleteAsync();
            reads.Reset();
            var empty = Assert.IsType<OkObjectResult>(await controller.List(token: CancellationToken.None));
            AssertAccessBudget(reads, expectedRuleReaders: 2);
            using var emptyJson = JsonDocument.Parse(JsonSerializer.Serialize(empty.Value, WebJsonOptions));
            Assert.Equal(0, emptyJson.RootElement.GetProperty("total").GetInt32());
            Assert.Empty(emptyJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(2, emptyJson.RootElement.GetProperty("summary").GetProperty("activeRules").GetInt32());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ProxyAccessBucket Bucket(string ip, string endpoint, int requests, long proxyItems,
        DateTimeOffset lastSeenAt, Guid? userId = null) => new()
        {
            BucketStartedAt = lastSeenAt.AddMinutes(-5),
            IpAddress = ip,
            UserId = userId,
            Endpoint = endpoint,
            Requests = requests,
            ProxyItems = proxyItems,
            LastSeenAt = lastSeenAt
        };

    private static void AssertAccessBudget(RelationReadCounter reads, int expectedRuleReaders)
    {
        Assert.Equal(2, reads.Reads.Length);
        Assert.Equal(2, reads.AccessBucketReads);
        Assert.Equal(expectedRuleReaders, reads.RuleReads);
        Assert.All(reads.Reads, read => Assert.Equal(IsolationLevel.RepeatableRead, read.IsolationLevel));
        var page = Assert.Single(reads.Reads, read =>
            read.Sql.Contains("AspNetUsers", StringComparison.Ordinal));
        Assert.Contains("AccessBlockRules", page.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", page.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordHash", page.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SecurityStamp", page.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrencyStamp", page.Sql, StringComparison.Ordinal);
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
    }

    private sealed class RelationReadCounter : DbCommandInterceptor
    {
        private int accessBucketReads;
        private int ruleReads;
        private readonly ConcurrentQueue<AccessRead> reads = new();

        internal int AccessBucketReads => Volatile.Read(ref accessBucketReads);
        internal int RuleReads => Volatile.Read(ref ruleReads);
        internal AccessRead[] Reads => reads.ToArray();

        internal void Reset()
        {
            Volatile.Write(ref accessBucketReads, 0);
            Volatile.Write(ref ruleReads, 0);
            while (reads.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                reads.Enqueue(new AccessRead(command.CommandText, command.Transaction?.IsolationLevel));
            if (command.CommandText.Contains("\"ProxyAccessBuckets\"", StringComparison.Ordinal))
                Interlocked.Increment(ref accessBucketReads);
            if (command.CommandText.Contains("\"AccessBlockRules\"", StringComparison.Ordinal))
                Interlocked.Increment(ref ruleReads);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record AccessRead(string Sql, IsolationLevel? IsolationLevel);
}
