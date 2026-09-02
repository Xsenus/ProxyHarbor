using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>
/// Ограниченный operational-срез diagnostics. PostgreSQL возвращает все небольшие
/// журналы и source health одним command/round-trip внутри общего REPEATABLE READ.
/// </summary>
internal sealed record DiagnosticsDatabaseSnapshot(
    long DatabaseBytes,
    IReadOnlyList<ProxySource> Sources,
    IReadOnlyList<CollectionRun> RecentRuns,
    IReadOnlyList<ValidationRun> ValidationRuns,
    IReadOnlyList<ValidationRun> RecentValidationRuns,
    IReadOnlyList<BackupRun> RecentBackups);

internal static class DiagnosticsDatabaseSnapshotReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal const string PostgresSql = """
        WITH recent_validation_ids AS MATERIALIZED (
            SELECT "Id"
            FROM "ValidationRuns"
            ORDER BY "StartedAt" DESC, "Id" DESC
            LIMIT 10
        ),
        validation_scope AS MATERIALIZED (
            SELECT "Id", "LeaseId", "StartedAt", "FinishedAt", "Claimed", "Checked",
                   "Alive", "Deferred", "Status", "Error", "CheckerNodeId"
            FROM "ValidationRuns"
            WHERE "FinishedAt" >= @validation_window_start
               OR ("Status" = 'running' AND "FinishedAt" IS NULL)
               OR "Id" IN (SELECT "Id" FROM recent_validation_ids)
        ),
        source_scope AS MATERIALIZED (
            SELECT "Url", "Enabled", "LastFetchedAt", "LastSucceededAt", "LastItemCount",
                   "LastResultTruncated", "ConsecutiveFailures", "LastError"
            FROM "Sources"
            WHERE "Url" = ANY (@built_in_urls)
        ),
        recent_collection AS MATERIALIZED (
            SELECT "Id", "StartedAt", "FinishedAt", "SourcesProcessed", "SourcesSucceeded",
                   "SourcesFailed", "SourcesSkipped", "SourcesTruncated", "CandidatesFound",
                   "CandidateLimitReached", "NewProxies", "AliveProxies", "Status", "Error"
            FROM "Runs"
            ORDER BY "StartedAt" DESC, "Id" DESC
            LIMIT 10
        ),
        recent_backup AS MATERIALIZED (
            SELECT "Id", "StartedAt", "FinishedAt", "Status", "FileName", "SizeBytes",
                   "TelegramConfigured", "SentToTelegram", "ObjectStorageConfigured",
                   "SentToObjectStorage", "ObjectStorageKey", "Error"
            FROM "BackupRuns"
            ORDER BY "StartedAt" DESC, "Id" DESC
            LIMIT 10
        )
        SELECT pg_database_size(current_database()) AS "DatabaseBytes",
               coalesce((
                   SELECT jsonb_agg(jsonb_build_object(
                       'url', "Url",
                       'enabled', "Enabled",
                       'lastFetchedAt', "LastFetchedAt",
                       'lastSucceededAt', "LastSucceededAt",
                       'lastItemCount', "LastItemCount",
                       'lastResultTruncated', "LastResultTruncated",
                       'consecutiveFailures', "ConsecutiveFailures",
                       'lastError', "LastError") ORDER BY "Url")
                   FROM source_scope), '[]'::jsonb)::text AS "SourcesJson",
               coalesce((
                   SELECT jsonb_agg(jsonb_build_object(
                       'id', "Id",
                       'startedAt', "StartedAt",
                       'finishedAt', "FinishedAt",
                       'sourcesProcessed', "SourcesProcessed",
                       'sourcesSucceeded', "SourcesSucceeded",
                       'sourcesFailed', "SourcesFailed",
                       'sourcesSkipped', "SourcesSkipped",
                       'sourcesTruncated', "SourcesTruncated",
                       'candidatesFound', "CandidatesFound",
                       'candidateLimitReached', "CandidateLimitReached",
                       'newProxies', "NewProxies",
                       'aliveProxies', "AliveProxies",
                       'status', "Status",
                       'error', "Error") ORDER BY "StartedAt" DESC, "Id" DESC)
                   FROM recent_collection), '[]'::jsonb)::text AS "RecentRunsJson",
               coalesce((
                   SELECT jsonb_agg(jsonb_build_object(
                       'id', "Id",
                       'leaseId', "LeaseId",
                       'startedAt', "StartedAt",
                       'finishedAt', "FinishedAt",
                       'claimed', "Claimed",
                       'checked', "Checked",
                       'alive', "Alive",
                       'deferred', "Deferred",
                       'status', "Status",
                       'error', "Error",
                       'checkerNodeId', "CheckerNodeId") ORDER BY "StartedAt" DESC, "Id" DESC)
                   FROM validation_scope), '[]'::jsonb)::text AS "ValidationRunsJson",
               coalesce((
                   SELECT jsonb_agg(jsonb_build_object(
                       'id', "Id",
                       'startedAt', "StartedAt",
                       'finishedAt', "FinishedAt",
                       'status', "Status",
                       'fileName', "FileName",
                       'sizeBytes', "SizeBytes",
                       'telegramConfigured', "TelegramConfigured",
                       'sentToTelegram', "SentToTelegram",
                       'objectStorageConfigured', "ObjectStorageConfigured",
                       'sentToObjectStorage', "SentToObjectStorage",
                       'objectStorageKey', "ObjectStorageKey",
                       'error', "Error") ORDER BY "StartedAt" DESC, "Id" DESC)
                   FROM recent_backup), '[]'::jsonb)::text AS "RecentBackupsJson"
        """;

    internal static async Task<DiagnosticsDatabaseSnapshot> ReadAsync(
        ProxyHarborDbContext db,
        IReadOnlyList<string> builtInUrls,
        DateTimeOffset validationWindowStart,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(builtInUrls);
        return db.Database.IsNpgsql()
            ? await ReadPostgresAsync(db, builtInUrls, validationWindowStart, token)
            : await ReadEfFallbackAsync(db, builtInUrls, validationWindowStart, token);
    }

    private static async Task<DiagnosticsDatabaseSnapshot> ReadPostgresAsync(
        ProxyHarborDbContext db,
        IReadOnlyList<string> builtInUrls,
        DateTimeOffset validationWindowStart,
        CancellationToken token)
    {
        var row = await db.Database.SqlQueryRaw<DiagnosticsDatabaseRow>(
                PostgresSql,
                new NpgsqlParameter<string[]>("built_in_urls", builtInUrls.ToArray()),
                new NpgsqlParameter<DateTimeOffset>("validation_window_start", validationWindowStart))
            .SingleAsync(token);
        var sources = Deserialize<DiagnosticsSourceRow>(row.SourcesJson)
            .Select(source => new ProxySource
            {
                Name = string.Empty,
                Url = source.Url,
                Enabled = source.Enabled,
                LastFetchedAt = source.LastFetchedAt,
                LastSucceededAt = source.LastSucceededAt,
                LastItemCount = source.LastItemCount,
                LastResultTruncated = source.LastResultTruncated,
                ConsecutiveFailures = source.ConsecutiveFailures,
                LastError = source.LastError
            })
            .ToArray();
        var validationRuns = Deserialize<ValidationRun>(row.ValidationRunsJson);
        return new DiagnosticsDatabaseSnapshot(
            row.DatabaseBytes,
            sources,
            Deserialize<CollectionRun>(row.RecentRunsJson),
            validationRuns,
            validationRuns.Take(10).ToArray(),
            Deserialize<BackupRun>(row.RecentBackupsJson));
    }

    /// <summary>Переносимый тестовый путь; production PostgreSQL всегда использует один reader.</summary>
    private static async Task<DiagnosticsDatabaseSnapshot> ReadEfFallbackAsync(
        ProxyHarborDbContext db,
        IReadOnlyList<string> builtInUrls,
        DateTimeOffset validationWindowStart,
        CancellationToken token)
    {
        var validationRuns = await db.ValidationRuns.AsNoTracking()
            .Where(run => run.FinishedAt >= validationWindowStart ||
                (run.Status == "running" && run.FinishedAt == null))
            .ToListAsync(token);
        var recentValidationRuns = await db.ValidationRuns.AsNoTracking()
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .Take(10)
            .ToListAsync(token);
        var telemetryIds = validationRuns.Select(run => run.Id).ToHashSet();
        validationRuns.AddRange(recentValidationRuns.Where(run => telemetryIds.Add(run.Id)));
        return new DiagnosticsDatabaseSnapshot(
            0,
            await db.Sources.AsNoTracking().Where(source => builtInUrls.Contains(source.Url)).ToListAsync(token),
            await db.Runs.AsNoTracking().OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id).Take(10).ToListAsync(token),
            validationRuns,
            recentValidationRuns,
            await db.BackupRuns.AsNoTracking().OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id).Take(10).ToListAsync(token));
    }

    private static T[] Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T[]>(json, JsonOptions) ?? [];

    private sealed record DiagnosticsSourceRow(
        string Url,
        bool Enabled,
        DateTimeOffset? LastFetchedAt,
        DateTimeOffset? LastSucceededAt,
        int LastItemCount,
        bool LastResultTruncated,
        int ConsecutiveFailures,
        string? LastError);

    private sealed class DiagnosticsDatabaseRow
    {
        public long DatabaseBytes { get; init; }
        public string SourcesJson { get; init; } = "[]";
        public string RecentRunsJson { get; init; } = "[]";
        public string ValidationRunsJson { get; init; } = "[]";
        public string RecentBackupsJson { get; init; } = "[]";
    }
}
