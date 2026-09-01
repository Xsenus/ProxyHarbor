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
                Endpoint(source, "203.0.113.1", VpnEndpointStatus.Reachable, "US", 20, 2, now.AddDays(-3)),
                Endpoint(source, "203.0.113.2", VpnEndpointStatus.Reachable, "DE", 40, 1, now.AddDays(-1)),
                Endpoint(source, "203.0.113.3", VpnEndpointStatus.Pending, "US", null, 0, now),
                Endpoint(source, "203.0.113.4", VpnEndpointStatus.Unreachable, null, null, 1, now),
                Endpoint(source, "203.0.113.5", VpnEndpointStatus.UnsupportedTransport, "DE", null, 0, now));
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
            Assert.Collection(snapshot.Countries,
                country => { Assert.Equal("DE", country.Code); Assert.Equal(2, country.Count); },
                country => { Assert.Equal("US", country.Code); Assert.Equal(2, country.Count); });

            await using var explain = new NpgsqlCommand(
                $"EXPLAIN (FORMAT JSON, COSTS OFF) {VpnMetricsSnapshotReader.PostgresSql}",
                (NpgsqlConnection)db.Database.GetDbConnection());
            explain.Parameters.AddWithValue(
                "reachable_status", NpgsqlDbType.Integer, (int)VpnEndpointStatus.Reachable);
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
        DateTimeOffset firstSeenAt) => new()
        {
            Host = host,
            Port = 443,
            Protocol = VpnProtocol.Vless,
            Transport = "tcp",
            Status = status,
            CountryCode = countryCode,
            LatencyMs = latencyMs,
            SuccessfulChecks = successfulChecks,
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
