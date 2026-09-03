using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Хранилище двух компактных operational snapshots между restart API.</summary>
public interface IMetricsSnapshotStore
{
    /// <summary>Загружает сохранённый JSON либо null, если снимок ещё не создавался.</summary>
    Task<string?> LoadAsync(string key, CancellationToken token);
    /// <summary>Атомарно заменяет снимок указанного типа.</summary>
    Task SaveAsync(string key, string payload, DateTimeOffset capturedAt, CancellationToken token);
}

/// <summary>
/// Сохраняет только агрегированные несекретные значения. Atomic upsert безопасен
/// для нескольких API-реплик и не оставляет частично записанный JSON.
/// </summary>
internal sealed class MetricsSnapshotStore(
    IDbContextFactory<ProxyHarborDbContext> dbFactory) : IMetricsSnapshotStore
{
    internal const string ProxyKey = "proxy";
    internal const string VpnKey = "vpn";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string SerializeProxy(ProxyMetricsSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Json);

    internal static ProxyMetricsSnapshot? DeserializeProxy(string payload) =>
        JsonSerializer.Deserialize<ProxyMetricsSnapshot>(payload, Json);

    internal static string SerializeVpn(VpnMetricsSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Json);

    internal static VpnMetricsSnapshot? DeserializeVpn(string payload) =>
        JsonSerializer.Deserialize<VpnMetricsSnapshot>(payload, Json);

    public async Task<string?> LoadAsync(string key, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var state = await db.MetricsSnapshotStates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == key, token);
        return state?.PayloadJson;
    }

    public async Task SaveAsync(
        string key,
        string payload,
        DateTimeOffset capturedAt,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var updatedAt = DateTimeOffset.UtcNow;
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MetricsSnapshotStates" ("Key", "PayloadJson", "CapturedAt", "UpdatedAt")
                VALUES ({key}, {payload}::jsonb, {capturedAt}, {updatedAt})
                ON CONFLICT ("Key") DO UPDATE SET
                    "PayloadJson" = EXCLUDED."PayloadJson",
                    "CapturedAt" = EXCLUDED."CapturedAt",
                    "UpdatedAt" = EXCLUDED."UpdatedAt"
                WHERE EXCLUDED."CapturedAt" >= "MetricsSnapshotStates"."CapturedAt"
                """, token);
            return;
        }

        var state = await db.MetricsSnapshotStates.SingleOrDefaultAsync(item => item.Key == key, token);
        if (state is null)
        {
            state = new MetricsSnapshotState { Key = key };
            db.MetricsSnapshotStates.Add(state);
        }
        else if (capturedAt < state.CapturedAt)
        {
            return;
        }
        state.PayloadJson = payload;
        state.CapturedAt = capturedAt;
        state.UpdatedAt = updatedAt;
        await db.SaveChangesAsync(token);
    }
}
