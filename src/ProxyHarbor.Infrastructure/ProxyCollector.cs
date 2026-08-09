using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Параллельно загружает источники, дедуплицирует кандидатов и пакетно сохраняет их.</summary>
public sealed class ProxyCollector(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<CollectorOptions> options,
    ILogger<ProxyCollector> logger) : IDisposable
{
    private static readonly Action<ILogger, string, Exception?> SourceFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1001, "SourceFailed"), "Не удалось получить источник {Source}");
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Запускает один полный цикл сбора и возвращает его аудит.</summary>
    public async Task<CollectionRun> CollectAsync(CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var run = new CollectionRun();
            db.Runs.Add(run);
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var sources = await db.Sources.AsNoTracking().Where(x => x.Enabled).OrderBy(x => x.Priority).ToListAsync(cancellationToken);
                var candidates = new ConcurrentDictionary<string, (string Host, int Port, ProxyProtocol Protocol)>();
                var candidateCount = 0;
                var sourceResults = new ConcurrentBag<(Guid Id, int Count, string? Error)>();
                var client = httpClientFactory.CreateClient("sources");

                await Parallel.ForEachAsync(sources, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Math.Clamp(options.Value.SourceConcurrency, 1, 32), Math.Max(1, sources.Count)),
                    CancellationToken = cancellationToken
                }, async (source, token) =>
                {
                    try
                    {
                        var content = await FetchSourceAsync(client, source.Url, token);
                        var parsed = ProxyParser.Parse(content, source.DefaultProtocol);
                        foreach (var item in parsed.Take(options.Value.MaxProxiesPerSource))
                        {
                            if (Volatile.Read(ref candidateCount) >= options.Value.MaxCandidatesPerRun) break;
                            var key = $"{item.Protocol}:{item.Host}:{item.Port}";
                            if (!candidates.TryAdd(key, item)) continue;
                            if (Interlocked.Increment(ref candidateCount) <= options.Value.MaxCandidatesPerRun) continue;
                            candidates.TryRemove(key, out _);
                            Interlocked.Decrement(ref candidateCount);
                            break;
                        }
                        sourceResults.Add((source.Id, Math.Min(parsed.Count, options.Value.MaxProxiesPerSource), null));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        SourceFailed(logger, source.Name, ex);
                        sourceResults.Add((source.Id, 0, ex.Message));
                    }
                });

                foreach (var result in sourceResults)
                {
                    var source = await db.Sources.FindAsync([result.Id], cancellationToken);
                    if (source is null) continue;
                    source.LastFetchedAt = DateTimeOffset.UtcNow;
                    source.LastItemCount = result.Count;
                    source.LastError = result.Error?[..Math.Min(500, result.Error.Length)];
                    if (result.Error is null)
                    {
                        source.LastSucceededAt = DateTimeOffset.UtcNow;
                        source.ConsecutiveFailures = 0;
                    }
                    else
                    {
                        source.ConsecutiveFailures++;
                    }
                }

                var now = DateTimeOffset.UtcNow;
                var added = await BulkUpsertAsync(db, candidates.Values, now, cancellationToken);

                run.FinishedAt = now;
                run.SourcesProcessed = sourceResults.Count;
                run.SourcesSucceeded = sourceResults.Count(x => x.Error is null);
                run.SourcesFailed = sourceResults.Count(x => x.Error is not null);
                run.CandidatesFound = candidates.Count;
                run.NewProxies = added;
                run.AliveProxies = await db.Proxies.CountAsync(x => x.Status == ProxyStatus.Alive, cancellationToken);
                run.Status = "completed";
                await db.SaveChangesAsync(cancellationToken);
                var deadCutoff = now.AddDays(-Math.Max(1, options.Value.DeadRetentionDays));
                await db.Proxies.Where(x => x.Status == ProxyStatus.Dead && x.LastSeenAt < deadCutoff)
                    .ExecuteDeleteAsync(cancellationToken);
                var runCutoff = now.AddDays(-options.Value.RunRetentionDays);
                await db.Runs.Where(x => x.StartedAt < runCutoff).ExecuteDeleteAsync(cancellationToken);
                return run;
            }
            catch (Exception ex)
            {
                run.FinishedAt = DateTimeOffset.UtcNow;
                run.Status = "failed";
                run.Error = ex.ToString()[..Math.Min(2000, ex.ToString().Length)];
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }
        finally { _runGate.Release(); }
    }

    /// <summary>Освобождает синхронизатор запуска при остановке контейнера DI.</summary>
    public void Dispose() => _runGate.Dispose();

    private async Task<string> FetchSourceAsync(HttpClient client, string url, CancellationToken token)
    {
        var retries = Math.Clamp(options.Value.SourceRetryCount, 0, 5);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, options.Value.SourceTimeoutSeconds)));
                using var response = await GetWithSafeRedirectsAsync(client, url, timeout.Token);
                if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt < retries)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ??
                        response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ??
                        TimeSpan.FromMilliseconds(400 * (attempt + 1));
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5_000, retryAfter.TotalMilliseconds + Random.Shared.Next(50, 250))), token);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 10_000_000)
                    throw new InvalidOperationException("Источник превышает лимит 10 МБ.");
                return await ReadLimitedAsync(response.Content, 10_000_000, timeout.Token);
            }
            catch (Exception ex) when (attempt < retries && !token.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1) + Random.Shared.Next(50, 250)), token);
            }
        }
    }

    private static async Task<HttpResponseMessage> GetWithSafeRedirectsAsync(HttpClient client, string url, CancellationToken token)
    {
        var current = new Uri(url, UriKind.Absolute);
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            if (!await NetworkSafety.IsSafePublicHttpsUrlAsync(current.AbsoluteUri, token))
                throw new HttpRequestException("Источник или его перенаправление ведёт в запрещённую сеть.");

            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("Перенаправление источника не содержит Location.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new HttpRequestException("Источник превысил лимит в три перенаправления.");
    }

    private static async Task<int> BulkUpsertAsync(
        ProxyHarborDbContext db,
        IEnumerable<(string Host, int Port, ProxyProtocol Protocol)> candidates,
        DateTimeOffset now,
        CancellationToken token)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var create = new NpgsqlCommand(
            "CREATE TEMP TABLE proxy_import (host text NOT NULL, port integer NOT NULL, protocol integer NOT NULL, seen_at timestamptz NOT NULL) ON COMMIT DROP",
            connection, transaction))
            await create.ExecuteNonQueryAsync(token);

        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY proxy_import (host, port, protocol, seen_at) FROM STDIN (FORMAT BINARY)", token))
        {
            foreach (var candidate in candidates)
            {
                await writer.StartRowAsync(token);
                await writer.WriteAsync(candidate.Host, NpgsqlDbType.Text, token);
                await writer.WriteAsync(candidate.Port, NpgsqlDbType.Integer, token);
                await writer.WriteAsync((int)candidate.Protocol, NpgsqlDbType.Integer, token);
                await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, token);
            }
            await writer.CompleteAsync(token);
        }

        await using var count = new NpgsqlCommand(
            "SELECT count(*)::integer FROM proxy_import i WHERE NOT EXISTS (SELECT 1 FROM \"Proxies\" p WHERE p.\"Host\" = i.host AND p.\"Port\" = i.port AND p.\"Protocol\" = i.protocol)",
            connection, transaction);
        var added = (int)(await count.ExecuteScalarAsync(token) ?? 0);

        await using var merge = new NpgsqlCommand("""
            INSERT INTO "Proxies" ("Id", "Host", "Port", "Protocol", "Status", "IsAnonymous", "FirstSeenAt", "LastSeenAt", "SuccessfulChecks", "FailedChecks")
            SELECT gen_random_uuid(), host, port, protocol, 0, false, seen_at, seen_at, 0, 0
            FROM proxy_import
            ON CONFLICT ("Host", "Port", "Protocol") DO UPDATE SET "LastSeenAt" = EXCLUDED."LastSeenAt"
            """, connection, transaction);
        await merge.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return added;
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, int maxCharacters, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var builder = new System.Text.StringBuilder(Math.Min(maxCharacters, 64 * 1024));
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, token);
            if (read == 0) return builder.ToString();
            if (builder.Length + read > maxCharacters) throw new InvalidOperationException("Источник превышает лимит 10 млн символов.");
            builder.Append(buffer, 0, read);
        }
    }
}
