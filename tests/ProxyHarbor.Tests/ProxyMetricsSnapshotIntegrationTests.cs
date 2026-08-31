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
/// точные счётчики и ровно один физический доступ к большой таблице.
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
            db.Proxies.AddRange(
                Endpoint("10.0.0.1", ProxyStatus.Alive, ProxyProtocol.Http,
                    now, now.AddMinutes(-1), now.AddMinutes(-2)),
                Endpoint("10.0.0.2", ProxyStatus.Pending, ProxyProtocol.Socks5,
                    now.AddDays(-10), null, null),
                Endpoint("10.0.0.3", ProxyStatus.Dead, ProxyProtocol.Https,
                    now.AddDays(-10), now.AddMinutes(-1), now.AddMinutes(-3), now.AddMinutes(5)),
                Endpoint("10.0.0.4", ProxyStatus.Alive, ProxyProtocol.Socks5,
                    now, now.AddMinutes(1), now.AddMinutes(-30), lastCheckedAt: now.AddHours(-1)));
            await db.SaveChangesAsync();

            var snapshot = await ProxyMetricsSnapshotReader.ReadAsync(
                db, now, retentionCutoff, freshAfter, CancellationToken.None);

            Assert.Equal(4, snapshot.Groups.Count);
            Assert.Equal(2, snapshot.Due);
            Assert.Equal(1, snapshot.Leased);
            Assert.Equal(1, snapshot.NeverAttempted);
            Assert.Equal(1, snapshot.StaleUnseen);
            Assert.Equal(1, snapshot.Published);
            Assert.Equal(now.AddMinutes(-2), snapshot.LastAttemptAt);
            Assert.Equal(4, snapshot.Groups.Sum(row => row.Count));

            await using var explain = new NpgsqlCommand(
                $"EXPLAIN (FORMAT JSON, COSTS OFF) {ProxyMetricsSnapshotReader.PostgresSql}",
                (NpgsqlConnection)db.Database.GetDbConnection());
            AddParameters(explain, now, retentionCutoff, freshAfter);
            var rawPlan = await explain.ExecuteScalarAsync();
            var planJson = Convert.ToString(rawPlan, CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrWhiteSpace(planJson));
            using var plan = JsonDocument.Parse(planJson!);
            Assert.Equal(1, CountRelationScans(plan.RootElement, "Proxies"));
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
        DateTimeOffset? leaseUntil = null,
        DateTimeOffset? lastCheckedAt = null) => new()
        {
            Host = host,
            Port = 8080,
            Status = status,
            Protocol = protocol,
            LatencyMs = status == ProxyStatus.Alive ? 25 : null,
            SuccessfulChecks = status == ProxyStatus.Alive ? 1 : 0,
            FailedChecks = status == ProxyStatus.Dead ? 1 : 0,
            ConsecutiveFailedChecks = status == ProxyStatus.Dead ? 1 : 0,
            FirstSeenAt = lastSeenAt.AddDays(-1),
            LastSeenAt = lastSeenAt,
            LastCheckedAt = lastCheckedAt ?? lastAttemptAt,
            NextCheckAt = nextCheckAt,
            LastValidationAttemptAt = lastAttemptAt,
            CheckLeaseUntil = leaseUntil,
            CheckLeaseId = leaseUntil.HasValue ? Guid.NewGuid() : null
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
