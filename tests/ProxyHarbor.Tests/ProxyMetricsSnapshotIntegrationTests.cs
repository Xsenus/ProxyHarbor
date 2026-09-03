using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>
/// Фиксирует production-контракт агрегирования proxy-метрик настоящим PostgreSQL:
/// точные счётчики, один полный проход и отдельные PK lookup только для активных lease.
/// </summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyMetricsSnapshotIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SnapshotPreservesCountersAndPlanReadsProxiesOnce()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_metrics_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            await using var db = new ProxyHarborDbContext(options);
            await db.Database.MigrateAsync();

            var now = new DateTimeOffset(2026, 8, 31, 7, 0, 0, TimeSpan.Zero);
            var retentionCutoff = now.AddDays(-2);
            var freshAfter = now.AddMinutes(-15);
            var leased = Endpoint("10.0.0.3", ProxyStatus.Dead, ProxyProtocol.Https,
                now.AddDays(-10), now.AddMinutes(-1), now.AddMinutes(-3), countryCode: "DE");
            var historicalDead = Endpoint("10.0.0.6", ProxyStatus.Dead, ProxyProtocol.Https,
                now.AddDays(-10), now.AddMinutes(-1), now.AddMinutes(-3), countryCode: "DE");
            historicalDead.SuccessfulChecks = 3;
            historicalDead.FirstAliveAt = now.AddDays(-11);
            historicalDead.LastAliveAt = now.AddDays(-10);
            db.Proxies.AddRange(
                Endpoint("10.0.0.1", ProxyStatus.Alive, ProxyProtocol.Http,
                    now, now.AddMinutes(-1), now.AddMinutes(-2), countryCode: "US"),
                Endpoint("10.0.0.2", ProxyStatus.Pending, ProxyProtocol.Socks5,
                    now.AddDays(-10), null, null, countryCode: "US"),
                leased,
                Endpoint("10.0.0.4", ProxyStatus.Alive, ProxyProtocol.Socks5,
                    now, now.AddMinutes(1), now.AddMinutes(-30), lastCheckedAt: now.AddHours(-1)),
                Endpoint("10.0.0.5", ProxyStatus.Alive, ProxyProtocol.Http,
                    now, now.AddMinutes(5), now.AddMinutes(-4), lastCheckedAt: now.AddMinutes(-5), countryCode: "DE"),
                historicalDead);
            db.ProxyValidationLeases.Add(new ProxyValidationLease
            {
                ProxyId = leased.Id,
                LeaseId = Guid.NewGuid(),
                LeaseUntil = now.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var snapshot = await ProxyMetricsSnapshotReader.ReadAsync(
                db, now, retentionCutoff, freshAfter, CancellationToken.None);

            Assert.Equal(4, snapshot.Groups.Count);
            Assert.Equal(3, snapshot.Due);
            Assert.Equal(1, snapshot.Leased);
            Assert.Equal(1, snapshot.NeverAttempted);
            Assert.Equal(1, snapshot.StaleUnseen);
            Assert.Equal(2, snapshot.Published);
            Assert.Equal(now.AddMinutes(-2), snapshot.LastAttemptAt);
            Assert.Equal(now.AddHours(-2), snapshot.OldestActiveAt);
            Assert.Equal(6, snapshot.Groups.Sum(row => row.Count));
            Assert.Equal(6, snapshot.Facets.Sum(row => row.Count));
            Assert.Equal(3, snapshot.Facets
                .Where(row => row.CountryCode == "DE")
                .Sum(row => row.Count));
            Assert.Equal(2, snapshot.Facets
                .Where(row => row.Status == ProxyStatus.Alive && row.Protocol == ProxyProtocol.Http)
                .Sum(row => row.Count));
            Assert.Collection(snapshot.Countries,
                country => { Assert.Equal("DE", country.Code); Assert.Equal(3, country.Count); },
                country => { Assert.Equal("US", country.Code); Assert.Equal(2, country.Count); });
            Assert.Equal(2, Assert.Single(snapshot.Groups,
                row => row.Status == ProxyStatus.Alive && row.Protocol == ProxyProtocol.Http).Count);
            var deadHttps = Assert.Single(snapshot.Groups,
                row => row.Status == ProxyStatus.Dead && row.Protocol == ProxyProtocol.Https);
            Assert.Equal(2, deadHttps.Count);
            Assert.Equal(1, deadHttps.EverAlive);
            Assert.Equal(1, deadHttps.HistoricalDead);
            Assert.Equal(0, deadHttps.StaleUnseen);

            await using (var indexCommand = new NpgsqlCommand(
                """
                SELECT indexdef
                FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = 'IX_Proxies_Admin_LastCheckedAt'
                """,
                (NpgsqlConnection)db.Database.GetDbConnection()))
            {
                var indexDefinition = Convert.ToString(
                    await indexCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
                Assert.False(string.IsNullOrWhiteSpace(indexDefinition));
                Assert.Contains("(\"LastCheckedAt\" IS NULL)", indexDefinition, StringComparison.Ordinal);
                Assert.Contains("\"LastCheckedAt\" DESC", indexDefinition, StringComparison.Ordinal);
                Assert.Contains("INCLUDE (\"Status\", \"Protocol\", \"CountryCode\")",
                    indexDefinition, StringComparison.Ordinal);
            }

            await using var explain = new NpgsqlCommand(
                $"EXPLAIN (FORMAT JSON, COSTS OFF) {ProxyMetricsSnapshotReader.PostgresSql}",
                (NpgsqlConnection)db.Database.GetDbConnection());
            AddParameters(explain, now, retentionCutoff, freshAfter);
            var rawPlan = await explain.ExecuteScalarAsync();
            var planJson = Convert.ToString(rawPlan, CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrWhiteSpace(planJson));
            using var plan = JsonDocument.Parse(planJson!);
            Assert.InRange(CountRelationScans(plan.RootElement, "Proxies"), 1, 2);
            Assert.DoesNotContain(
                "LEFT JOIN \"ProxyValidationLeases\"",
                ProxyMetricsSnapshotReader.PostgresSql,
                StringComparison.Ordinal);
            Assert.Contains(
                "WHERE lease.\"LeaseUntil\" >= @now",
                ProxyMetricsSnapshotReader.PostgresSql,
                StringComparison.Ordinal);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ProxyEndpoint Endpoint(
        string host,
        ProxyStatus status,
        ProxyProtocol protocol,
        DateTimeOffset lastSeenAt,
        DateTimeOffset? nextCheckAt,
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset? lastCheckedAt = null,
        string? countryCode = null) => new()
        {
            Host = host,
            Port = 8080,
            Status = status,
            Protocol = protocol,
            LatencyMs = status == ProxyStatus.Alive ? 25 : null,
            SuccessfulChecks = status == ProxyStatus.Alive ? 1 : 0,
            FailedChecks = status == ProxyStatus.Dead ? 1 : 0,
            ConsecutiveFailedChecks = status == ProxyStatus.Dead ? 1 : 0,
            CountryCode = countryCode,
            CurrentAliveSince = status == ProxyStatus.Alive ? lastSeenAt.AddHours(-2) : null,
            FirstAliveAt = status == ProxyStatus.Alive ? lastSeenAt.AddHours(-3) : null,
            LastAliveAt = status == ProxyStatus.Alive ? lastCheckedAt ?? lastAttemptAt ?? lastSeenAt : null,
            FirstSeenAt = lastSeenAt.AddDays(-1),
            LastSeenAt = lastSeenAt,
            LastCheckedAt = lastCheckedAt ?? lastAttemptAt,
            NextCheckAt = nextCheckAt,
            LastValidationAttemptAt = lastAttemptAt
        };

    private static void AddParameters(
        NpgsqlCommand command,
        DateTimeOffset now,
        DateTimeOffset retentionCutoff,
        DateTimeOffset freshAfter)
    {
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("retention_cutoff", NpgsqlDbType.TimestampTz, retentionCutoff);
        command.Parameters.AddWithValue("fresh_after", NpgsqlDbType.TimestampTz, freshAfter);
        command.Parameters.AddWithValue("pending_status", NpgsqlDbType.Integer, (int)ProxyStatus.Pending);
        command.Parameters.AddWithValue("dead_status", NpgsqlDbType.Integer, (int)ProxyStatus.Dead);
        command.Parameters.AddWithValue("alive_status", NpgsqlDbType.Integer, (int)ProxyStatus.Alive);
    }

    private static int CountRelationScans(JsonElement element, string relation)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Sum(item => CountRelationScans(item, relation));
        if (element.ValueKind != JsonValueKind.Object) return 0;

        var scans = element.TryGetProperty("Relation Name", out var name) &&
            name.GetString() == relation ? 1 : 0;
        return scans + element.EnumerateObject()
            .Sum(property => CountRelationScans(property.Value, relation));
    }
}
