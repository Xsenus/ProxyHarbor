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
    private static readonly TimeSpan AuditWriteTimeout = TimeSpan.FromSeconds(15);
    private static readonly Action<ILogger, string, Exception?> SourceFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1001, "SourceFailed"), "Не удалось получить источник {Source}");
    private static readonly Action<ILogger, Exception?> CollectionAuditFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1002, "CollectionAuditFailed"),
            "Не удалось сохранить итоговый аудит цикла сбора.");
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Запускает один полный цикл сбора и возвращает его аудит.</summary>
    public async Task<CollectionRun> CollectAsync(CancellationToken cancellationToken, bool forceAllSources = false)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new OperationAlreadyRunningException("сбор источников");
        try
        {
            await using var clusterLock = await PostgresAdvisoryLock.TryAcquireAsync(
                dbFactory, PostgresAdvisoryLock.CollectionKey, cancellationToken)
                ?? throw new OperationAlreadyRunningException("сбор источников");
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            // Cluster lock доказывает, что живого collection-run в общей БД больше нет:
            // незавершённые строки могли остаться только после kill, power loss или обрыва БД.
            var recoveredAt = DateTimeOffset.UtcNow;
            await db.Runs.Where(item => item.Status == "running" && item.FinishedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.FinishedAt, recoveredAt)
                    .SetProperty(item => item.Status, "failed")
                    .SetProperty(item => item.Error,
                        "Сбор был прерван аварийным завершением предыдущего процесса."), cancellationToken);
            var run = new CollectionRun();
            db.Runs.Add(run);
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var collectionStartedAt = DateTimeOffset.UtcNow;
                var allSources = await db.Sources.AsNoTracking().Where(x => x.Enabled)
                    .OrderBy(x => x.Priority).ToListAsync(cancellationToken);
                var sources = allSources.Where(source =>
                    SourceFetchSchedule.IsDue(source.NextFetchAt, collectionStartedAt, forceAllSources)).ToList();
                var candidates = new BoundedProxyCandidateSet(options.Value.MaxCandidatesPerRun);
                var sourceResults = new ConcurrentBag<(Guid Id, int Count, bool Truncated, string? Error)>();
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
                        // Лимит применяется внутри parser: крупный недоверенный feed не может сначала
                        // построить неограниченную коллекцию, которая будет усечена только здесь.
                        var publishCandidates = true;
                        var parsed = SourceFeedParser.ParseBoundedToRequired(
                            content,
                            source.DefaultProtocol,
                            options.Value.MaxProxiesPerSource,
                            candidate =>
                            {
                                if (!publishCandidates) return;
                                _ = candidates.TryAdd(candidate);
                                // После первого реально отброшенного unique endpoint полнота уже
                                // доказанно потеряна. Feed продолжаем сканировать только для точного
                                // per-source count/truncation, без дальнейшей нагрузки на общий set.
                                if (candidates.LimitReached) publishCandidates = false;
                            });
                        sourceResults.Add((source.Id, parsed.Count, parsed.Truncated, null));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        SourceFailed(logger, source.Name, ex);
                        sourceResults.Add((source.Id, 0, false, ex.Message));
                    }
                });

                var sourceResultById = sourceResults.ToDictionary(result => result.Id);
                var sourceIds = sourceResultById.Keys.ToArray();
                var trackedSources = await db.Sources.Where(source => sourceIds.Contains(source.Id)).ToListAsync(cancellationToken);
                foreach (var source in trackedSources)
                {
                    var result = sourceResultById[source.Id];
                    var fetchedAt = DateTimeOffset.UtcNow;
                    source.LastFetchedAt = fetchedAt;
                    source.LastItemCount = result.Count;
                    source.LastResultTruncated = result.Truncated;
                    source.LastError = result.Error?[..Math.Min(500, result.Error.Length)];
                    if (result.Error is null)
                    {
                        source.LastSucceededAt = fetchedAt;
                        source.ConsecutiveFailures = 0;
                        source.NextFetchAt = null;
                    }
                    else
                    {
                        source.ConsecutiveFailures++;
                        source.NextFetchAt = SourceFetchSchedule.NextAttempt(
                            collectionStartedAt,
                            source.ConsecutiveFailures,
                            options.Value.SourceFailureBackoffBaseMinutes,
                            options.Value.SourceFailureBackoffMaxHours);
                    }
                }

                var now = DateTimeOffset.UtcNow;
                var added = await BulkUpsertAsync(
                    db, candidates.Items, now, options.Value.LastSeenRefreshMinutes, cancellationToken);

                run.FinishedAt = now;
                run.SourcesProcessed = sourceResults.Count;
                run.SourcesSucceeded = sourceResults.Count(x => x.Error is null);
                run.SourcesFailed = sourceResults.Count(x => x.Error is not null);
                run.SourcesSkipped = allSources.Count - sources.Count;
                run.SourcesTruncated = sourceResults.Count(x => x.Truncated);
                run.CandidatesFound = candidates.Count;
                run.CandidateLimitReached = candidates.LimitReached;
                run.NewProxies = added;
                run.AliveProxies = await db.Proxies.CountAsync(x => x.Status == ProxyStatus.Alive, cancellationToken);
                run.Status = "completed";
                await db.SaveChangesAsync(cancellationToken);
                var deadCutoff = now.AddDays(-Math.Max(1, options.Value.DeadRetentionDays));
                await db.Proxies.Where(x => x.Status == ProxyStatus.Dead && x.LastSeenAt < deadCutoff)
                    .ExecuteDeleteAsync(cancellationToken);
                var runCutoff = now.AddDays(-options.Value.RunRetentionDays);
                await db.Runs.Where(x => x.StartedAt < runCutoff).ExecuteDeleteAsync(cancellationToken);
                // Долгая активная validation-партия другой реплики не должна потерять
                // ownership своей audit row из-за retention collection-цикла.
                await db.ValidationRuns.Where(x => x.StartedAt < runCutoff && x.Status != "running")
                    .ExecuteDeleteAsync(cancellationToken);
                return run;
            }
            catch (Exception ex)
            {
                await FailRunAuditAsync(run.Id, ex);
                throw;
            }
        }
        finally { _runGate.Release(); }
    }

    /// <summary>Освобождает синхронизатор запуска при остановке контейнера DI.</summary>
    public void Dispose() => _runGate.Dispose();

    private async Task FailRunAuditAsync(Guid id, Exception exception)
    {
        try
        {
            // Ошибка могла оставить основной DbContext/connection в непригодном состоянии.
            // Отдельный контекст и bounded token не скрывают исходный сбой и не тормозят shutdown.
            using var timeout = new CancellationTokenSource(AuditWriteTimeout);
            await using var auditDb = await dbFactory.CreateDbContextAsync(timeout.Token);
            var error = exception.ToString();
            await auditDb.Runs.Where(item => item.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(item => item.Status, "failed")
                .SetProperty(item => item.Error, error[..Math.Min(2000, error.Length)]), timeout.Token);
        }
        catch (Exception auditException)
        {
            // Следующий cluster-lock-владелец восстановит оставшуюся running-строку.
            CollectionAuditFailed(logger, auditException);
        }
    }

    internal async Task<string> FetchSourceAsync(
        HttpClient client,
        string url,
        CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        delayAsync ??= static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);
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
                    var delayMilliseconds = Math.Clamp(retryAfter.TotalMilliseconds, 0, 4_750) + Random.Shared.Next(50, 250);
                    // ResponseHeadersRead не потребляет body. Освобождаем response до backoff,
                    // иначе один transient feed удерживает connection-pool slot всё время паузы.
                    response.Dispose();
                    await delayAsync(TimeSpan.FromMilliseconds(delayMilliseconds), token);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                SourceFeedParser.EnsureSupportedMediaType(response.Content.Headers.ContentType?.MediaType);
                if (response.Content.Headers.ContentLength is > 10_000_000)
                    throw new InvalidOperationException("Источник превышает лимит 10 МБ.");
                return await ReadLimitedAsync(response.Content, 10_000_000, timeout.Token);
            }
            catch (Exception ex) when (attempt < retries && SourceHttpRetry.IsRetryable(ex, token))
            {
                await delayAsync(
                    TimeSpan.FromMilliseconds(400 * (attempt + 1) + Random.Shared.Next(50, 250)), token);
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
        int lastSeenRefreshMinutes,
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

        // Отдельный INSERT возвращает точное число новых строк и не заставляет PostgreSQL
        // выполнять бесполезный UPDATE каждого существующего proxy на каждом 15-минутном цикле.
        await using var insert = new NpgsqlCommand("""
            INSERT INTO "Proxies" ("Id", "Host", "Port", "Protocol", "Status", "IsAnonymous", "FirstSeenAt", "LastSeenAt", "SuccessfulChecks", "FailedChecks")
            SELECT gen_random_uuid(), i.host, i.port, i.protocol, 0, false, i.seen_at, i.seen_at, 0, 0
            FROM proxy_import i
            WHERE NOT EXISTS (
                SELECT 1 FROM "Proxies" p
                WHERE p."Host" = i.host AND p."Port" = i.port AND p."Protocol" = i.protocol)
            ON CONFLICT ("Host", "Port", "Protocol") DO NOTHING
            """, connection, transaction);
        var added = await insert.ExecuteNonQueryAsync(token);

        // LastSeenAt нужен для retention, но точность до каждого цикла не нужна. Ограничение
        // частоты резко сокращает WAL и перезапись индекса на сотнях тысяч строк.
        await using var refresh = new NpgsqlCommand("""
            UPDATE "Proxies" p
            SET "LastSeenAt" = i.seen_at
            FROM proxy_import i
            WHERE p."Host" = i.host AND p."Port" = i.port AND p."Protocol" = i.protocol
              AND p."LastSeenAt" < @refresh_before
            """, connection, transaction);
        refresh.Parameters.AddWithValue("refresh_before", NpgsqlDbType.TimestampTz,
            now.AddMinutes(-Math.Max(1, lastSeenRefreshMinutes)));
        await refresh.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return added;
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            if (output.Length + read > maxBytes) throw new InvalidOperationException("Источник превышает лимит 10 МБ.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }
}

/// <summary>Проверяет семантическую пригодность HTTP-ответа, а не только код 2xx.</summary>
internal static class SourceFeedParser
{
    internal static IReadOnlyCollection<(string Host, int Port, ProxyProtocol Protocol)> ParseRequired(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults = int.MaxValue)
    {
        EnsureNotHtmlEnvelope(content);
        var parsed = ProxyParser.Parse(content, defaultProtocol, maxResults);
        if (parsed.Count == 0)
            throw new InvalidDataException("Источник не содержит распознаваемых прокси.");
        return parsed;
    }

    /// <summary>Проверяет непустой feed и сохраняет точный сигнал индивидуального усечения.</summary>
    internal static ProxyParseResult ParseBoundedRequired(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults)
    {
        EnsureNotHtmlEnvelope(content);
        var parsed = ProxyParser.ParseWithLimitStatus(content, defaultProtocol, maxResults);
        if (parsed.Items.Count == 0)
            throw new InvalidDataException("Источник не содержит распознаваемых прокси.");
        return parsed;
    }

    /// <summary>Collector-path без materialized списка строк для каждого параллельного feed'а.</summary>
    internal static ProxyParseSummary ParseBoundedToRequired(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults,
        Action<ProxyCandidateKey> accept)
    {
        EnsureNotHtmlEnvelope(content);
        var parsed = ProxyParser.ParseTo(content, defaultProtocol, maxResults, accept);
        if (parsed.Count == 0)
            throw new InvalidDataException("Источник не содержит распознаваемых прокси.");
        return parsed;
    }

    /// <summary>HTTP 200 от login/WAF/error страницы не является proxy-feed.</summary>
    internal static void EnsureSupportedMediaType(string? mediaType)
    {
        if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Источник вернул HTML вместо списка прокси.");
    }

    private static void EnsureNotHtmlEnvelope(string content)
    {
        var start = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<body", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Источник вернул HTML вместо списка прокси.");
    }
}

/// <summary>Отделяет временные транспортные сбои от постоянных HTTP-ответов 4xx.</summary>
internal static class SourceHttpRetry
{
    internal static bool IsRetryable(Exception exception, CancellationToken outerToken) =>
        !outerToken.IsCancellationRequested &&
        (exception is HttpRequestException { StatusCode: null } || exception is TaskCanceledException);
}

/// <summary>Рассчитывает bounded exponential backoff для недоступного free-feed.</summary>
internal static class SourceFetchSchedule
{
    internal static bool IsDue(DateTimeOffset? nextFetchAt, DateTimeOffset now, bool forceAllSources) =>
        forceAllSources || nextFetchAt is null || nextFetchAt <= now;

    internal static DateTimeOffset NextAttempt(
        DateTimeOffset failedAt,
        int consecutiveFailures,
        int baseMinutes,
        int maxHours)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 20);
        var delayMinutes = baseMinutes * Math.Pow(2, exponent);
        var boundedMinutes = Math.Min(delayMinutes, TimeSpan.FromHours(maxHours).TotalMinutes);
        return failedAt.AddMinutes(boundedMinutes);
    }
}
