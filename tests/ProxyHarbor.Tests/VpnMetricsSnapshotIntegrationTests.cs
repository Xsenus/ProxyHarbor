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
/// Фиксирует production-контракт агрегирования VPN-метрик настоящим PostgreSQL:
/// точные счётчики, страны и ровно один физический доступ к большой таблице.
/// </summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class VpnMetricsSnapshotIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SnapshotPreservesCountersAndPlanReadsVpnEndpointsOnce()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_vpn_metrics_{Guid.NewGuid():N}";
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

            var now = DateTimeOffset.UtcNow;
            now = now.AddTicks(-(now.Ticks % 10)); // PostgreSQL timestamp precision is one microsecond.
            var source = new VpnSource
            {
                Name = "Metrics",
                Provider = "Integration",
                Url = "https://example.test/vpn.txt",
                DefaultProtocol = VpnProtocol.Vless,
                License = "MIT"
            };
            db.VpnSources.Add(source);
            db.VpnEndpoints.AddRange(
                Endpoint(source, "203.0.113.1", VpnEndpointStatus.Reachable, "US", 20, 2,
                    now.AddDays(-3), now.AddMinutes(-2), now.AddMinutes(8)),
                Endpoint(source, "203.0.113.2", VpnEndpointStatus.Reachable, "DE", 40, 1,
                    now.AddDays(-1), now.AddMinutes(-20), now.AddMinutes(-10)),
                Endpoint(source, "203.0.113.3", VpnEndpointStatus.Pending, "US", null, 0, now),
                Endpoint(source, "203.0.113.4", VpnEndpointStatus.Unreachable, null, null, 1,
                    now, now.AddMinutes(-3), now.AddMinutes(27)),
                Endpoint(source, "203.0.113.5", VpnEndpointStatus.UnsupportedTransport, "DE", null, 0,
                    now, now.AddMinutes(-4), now.AddMinutes(356)));
            await db.SaveChangesAsync();

            var snapshot = await VpnMetricsSnapshotReader.ReadAsync(db, now, CancellationToken.None);

            Assert.Equal(5, snapshot.Total);
            Assert.Equal(2, snapshot.Reachable);
            Assert.Equal(1, snapshot.Pending);
            Assert.Equal(1, snapshot.Unreachable);
            Assert.Equal(1, snapshot.Unsupported);
            Assert.Equal(3, snapshot.EverReachable);
            Assert.Equal(60, snapshot.ReachableLatencyTotal);
            Assert.Equal(2, snapshot.ReachableLatencySamples);
            Assert.Equal(now.AddDays(-3), snapshot.OldestReachableAt);
            Assert.Equal(1, snapshot.NeverChecked);
            Assert.Equal(2, snapshot.Due);
            Assert.Equal(1, snapshot.FreshReachable);
            Assert.Equal(1, snapshot.StaleReachable);
            Assert.Equal(3, snapshot.CheckedLastFiveMinutes);
            Assert.Equal(now.AddMinutes(-2), snapshot.LatestCheckedAt);
            Assert.Collection(snapshot.Countries,
                country => { Assert.Equal("DE", country.Code); Assert.Equal(2, country.Count); },
                country => { Assert.Equal("US", country.Code); Assert.Equal(2, country.Count); });
            Assert.Equal(5, snapshot.Facets.Sum(row => row.Count));
            Assert.Equal(2, snapshot.Facets
                .Where(row => row.CountryCode == "US" && row.Protocol == VpnProtocol.Vless &&
                    row.Transport == "tcp")
                .Sum(row => row.Count));
            Assert.Equal(1, snapshot.Facets
                .Where(row => row.Status == VpnEndpointStatus.Unreachable)
                .Sum(row => row.Count));

            await using (var indexCommand = new NpgsqlCommand(
                """
                SELECT indexdef
                FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = 'IX_VpnEndpoints_Admin_LastCheckedAt'
                """,
                (NpgsqlConnection)db.Database.GetDbConnection()))
            {
                var indexDefinition = Convert.ToString(
                    await indexCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
                Assert.False(string.IsNullOrWhiteSpace(indexDefinition));
                Assert.Contains("(\"LastCheckedAt\" IS NULL)", indexDefinition, StringComparison.Ordinal);
                Assert.Contains("\"LastCheckedAt\" DESC", indexDefinition, StringComparison.Ordinal);
                Assert.Contains("INCLUDE (\"Status\", \"Protocol\", \"Transport\", \"CountryCode\")",
                    indexDefinition, StringComparison.Ordinal);
            }

            await using (var freshnessIndexCommand = new NpgsqlCommand(
                """
                SELECT indexdef
                FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = 'IX_VpnEndpoints_PublicFreshness'
                """,
                (NpgsqlConnection)db.Database.GetDbConnection()))
            {
                var indexDefinition = Convert.ToString(
                    await freshnessIndexCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
                Assert.False(string.IsNullOrWhiteSpace(indexDefinition));
                Assert.Contains("(\"LastCheckedAt\")", indexDefinition, StringComparison.Ordinal);
                Assert.Contains("INCLUDE (\"Protocol\", \"CountryCode\")", indexDefinition,
                    StringComparison.Ordinal);
                Assert.Contains("\"Status\" = 1", indexDefinition, StringComparison.Ordinal);
                Assert.Contains("\"ConnectionUri\" IS NOT NULL", indexDefinition,
                    StringComparison.Ordinal);
                Assert.Contains("\"CountryCode\" IS NOT NULL", indexDefinition,
                    StringComparison.Ordinal);
            }

            // Маленькая fixture-таблица естественно предпочитает seq scan. Запрещаем его
            // только в этой сессии, чтобы доказать совпадение предиката production COUNT
            // с partial index; фактический выбор планировщика проверяется на production.
            await using (var disableSeqScan = new NpgsqlCommand(
                "SET enable_seqscan = off",
                (NpgsqlConnection)db.Database.GetDbConnection()))
                await disableSeqScan.ExecuteNonQueryAsync();
            try
            {
                await using var freshnessExplain = new NpgsqlCommand(
                    """
                    EXPLAIN (FORMAT JSON, COSTS OFF)
                    SELECT count(*)
                    FROM "VpnEndpoints"
                    WHERE "ConnectionUri" IS NOT NULL
                      AND "CountryCode" IS NOT NULL
                      AND "Status" = 1
                      AND "LastCheckedAt" >= @fresh_after
                    """,
                    (NpgsqlConnection)db.Database.GetDbConnection());
                freshnessExplain.Parameters.AddWithValue(
                    "fresh_after", NpgsqlDbType.TimestampTz, now.AddMinutes(-15));
                var freshnessPlan = Convert.ToString(
                    await freshnessExplain.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
                Assert.Contains("IX_VpnEndpoints_PublicFreshness", freshnessPlan,
                    StringComparison.Ordinal);
            }
            finally
            {
                await using var restoreSeqScan = new NpgsqlCommand(
                    "RESET enable_seqscan",
                    (NpgsqlConnection)db.Database.GetDbConnection());
                await restoreSeqScan.ExecuteNonQueryAsync();
            }

            await using var explain = new NpgsqlCommand(
                $"EXPLAIN (FORMAT JSON, COSTS OFF) {VpnMetricsSnapshotReader.PostgresSql}",
                (NpgsqlConnection)db.Database.GetDbConnection());
            explain.Parameters.AddWithValue(
                "reachable_status", NpgsqlDbType.Integer, (int)VpnEndpointStatus.Reachable);
            explain.Parameters.AddWithValue("captured_at", NpgsqlDbType.TimestampTz, now);
            explain.Parameters.AddWithValue("fresh_after", NpgsqlDbType.TimestampTz, now.AddMinutes(-15));
            explain.Parameters.AddWithValue("recent_after", NpgsqlDbType.TimestampTz, now.AddMinutes(-5));
            var rawPlan = await explain.ExecuteScalarAsync();
            var planJson = Convert.ToString(rawPlan, CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrWhiteSpace(planJson));
            using var plan = JsonDocument.Parse(planJson!);
            Assert.Equal(1, CountRelationScans(plan.RootElement, "VpnEndpoints"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static VpnEndpoint Endpoint(
        VpnSource source,
        string host,
        VpnEndpointStatus status,
        string? countryCode,
        int? latencyMs,
        int successfulChecks,
        DateTimeOffset firstSeenAt,
        DateTimeOffset? lastCheckedAt = null,
        DateTimeOffset? nextCheckAt = null) => new()
        {
            Host = host,
            Port = 443,
            Protocol = VpnProtocol.Vless,
            Transport = "tcp",
            Status = status,
            CountryCode = countryCode,
            LatencyMs = latencyMs,
            SuccessfulChecks = successfulChecks,
            LastCheckedAt = lastCheckedAt,
            NextCheckAt = nextCheckAt,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = firstSeenAt,
            FirstSource = source,
            FirstSourceId = source.Id
        };

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
