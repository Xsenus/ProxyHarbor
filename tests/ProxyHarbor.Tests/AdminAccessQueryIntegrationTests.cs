using System.Collections.Concurrent;
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

/// <summary>Не допускает возврата отдельных полных aggregate-запросов в access registry.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class AdminAccessQueryIntegrationTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task AccessRegistriesUseOneSummaryAndBoundedAccountLookup()
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

            var reads = new RelationReadCounter();
            var measuredOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .AddInterceptors(reads)
                .Options;
            var factory = new TestDbFactory(measuredOptions);
            await using var db = new ProxyHarborDbContext(measuredOptions);
            var controller = new AdminAccessController(db,
                new ProxyAccessMonitor(factory, NullLogger<ProxyAccessMonitor>.Instance));

            var traffic = Assert.IsType<OkObjectResult>(await controller.List(token: CancellationToken.None));
            Assert.Equal(3, reads.AccessBucketReads);
            Assert.Equal(1, reads.RuleReads);
            Assert.Contains(reads.AccessBucketSql, sql =>
                sql.Contains("ROW_NUMBER()", StringComparison.OrdinalIgnoreCase));
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
            Assert.Equal(3, reads.AccessBucketReads);
            Assert.Equal(1, reads.RuleReads);
            Assert.Contains(reads.AccessBucketSql, sql =>
                sql.Contains("ROW_NUMBER()", StringComparison.OrdinalIgnoreCase));
            using var visitorJson = JsonDocument.Parse(JsonSerializer.Serialize(visitors.Value, WebJsonOptions));
            var visitorRoot = visitorJson.RootElement;
            Assert.Equal(2, visitorRoot.GetProperty("total").GetInt32());
            Assert.Equal(31, visitorRoot.GetProperty("summary").GetProperty("pageViews").GetInt64());
            Assert.Equal(1, visitorRoot.GetProperty("summary").GetProperty("authenticatedVisitors").GetInt32());
            Assert.Equal(1, visitorRoot.GetProperty("summary").GetProperty("active24Hours").GetInt32());
            Assert.Contains(visitorRoot.GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("ipAddress").GetString() == "203.0.113.10" &&
                item.GetProperty("email").GetString() == member.Email);
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

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
    }

    private sealed class RelationReadCounter : DbCommandInterceptor
    {
        private int accessBucketReads;
        private int ruleReads;
        private readonly ConcurrentQueue<string> accessBucketSql = new();

        internal int AccessBucketReads => Volatile.Read(ref accessBucketReads);
        internal int RuleReads => Volatile.Read(ref ruleReads);
        internal IReadOnlyList<string> AccessBucketSql => accessBucketSql.ToArray();

        internal void Reset()
        {
            Volatile.Write(ref accessBucketReads, 0);
            Volatile.Write(ref ruleReads, 0);
            while (accessBucketSql.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"ProxyAccessBuckets\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref accessBucketReads);
                accessBucketSql.Enqueue(command.CommandText);
            }
            if (command.CommandText.Contains("\"AccessBlockRules\"", StringComparison.Ordinal))
                Interlocked.Increment(ref ruleReads);
            return ValueTask.FromResult(result);
        }
    }
}
