using System.Collections.Concurrent;
using System.Net.Http.Headers;
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
    ILogger<ProxyCollector> logger,
    ValidationWakeSignal? validationWakeSignal = null) : IDisposable
{
    private const int MaxSourceBytes = 10_000_000;
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
            await using var databaseLease = await DatabaseRuntimeGate.TryAcquireOperationLeaseAsync(
                dbFactory, cancellationToken)
                ?? throw new OperationAlreadyRunningException("восстановление базы данных");
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
                var sourceResults = new ConcurrentBag<SourceCollectionResult>();
                var client = httpClientFactory.CreateClient("sources");

                await Parallel.ForEachAsync(sources, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Math.Clamp(options.Value.SourceConcurrency, 1, 32), Math.Max(1, sources.Count)),
                    CancellationToken = cancellationToken
                }, async (source, token) =>
                {
                    try
                    {
                        // Admin force-run является доказательным полным аудитом, а не только
                        // обходом backoff-расписания: каждый feed обязан вернуть body и заново
                        // пройти parser. Иначе 304 повторно выдаёт старый LastItemCount за
                        // результат текущего ручного запуска.
                        var useValidators = !forceAllSources && SourceConditionalFetchPolicy.ShouldUseValidators(
                            source.LastContentFetchedAt,
                            collectionStartedAt,
                            options.Value.DeadRetentionDays);
                        var fetched = await FetchSourceStateAsync(
                            client,
                            source.Url,
                            useValidators ? source.HttpETag : null,
                            useValidators ? source.HttpLastModifiedAt : null,
                            token);
                        if (fetched.NotModified)
                        {
                            if (source.LastSucceededAt is null || source.LastItemCount <= 0)
                                throw new InvalidDataException(
                                    "Источник вернул 304 без сохранённого успешного непустого результата.");
                            sourceResults.Add(new SourceCollectionResult(
                                source.Id,
                                source.Url,
                                source.DefaultProtocol,
                                source.LastItemCount,
                                source.LastResultTruncated,
                                fetched.HttpETag,
                                fetched.HttpLastModifiedAt,
                                ContentFetched: false,
                                Error: null));
                            return;
                        }
                        var content = fetched.Content ??
                            throw new InvalidDataException("Успешный ответ источника не содержит body.");
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
                        sourceResults.Add(new SourceCollectionResult(
                            source.Id,
                            source.Url,
                            source.DefaultProtocol,
                            parsed.Count,
                            parsed.Truncated,
                            fetched.HttpETag,
                            fetched.HttpLastModifiedAt,
                            ContentFetched: true,
                            Error: null));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        OperationalLogBoundary.Write(() => SourceFailed(logger, source.Name, ex));
                        sourceResults.Add(new SourceCollectionResult(
                            source.Id, source.Url, source.DefaultProtocol,
                            0, false, null, null, ContentFetched: false, ex.Message));
                    }
                });

                var sourceResultById = sourceResults.ToDictionary(result => result.Id);
                var sourceIds = sourceResultById.Keys.ToArray();
                var trackedSources = await db.Sources.Where(source => sourceIds.Contains(source.Id)).ToListAsync(cancellationToken);
                foreach (var source in trackedSources)
                {
                    var result = sourceResultById[source.Id];
                    // Admin мог заменить endpoint, пока старый HTTP-запрос находился в полёте.
                    // Результат старой конфигурации нельзя приписывать новой: особенно ETag и
                    // LastContentFetchedAt, иначе новый feed способен получать ложные 304.
                    if (!string.Equals(source.Url, result.SourceUrl, StringComparison.Ordinal) ||
                        source.DefaultProtocol != result.SourceProtocol)
                        continue;
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
                        source.HttpETag = result.HttpETag;
                        source.HttpLastModifiedAt = result.HttpLastModifiedAt;
                        if (result.ContentFetched) source.LastContentFetchedAt = fetchedAt;
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
                // Bounded signal не накапливает по событию на каждый feed/endpoint:
                // одного wake достаточно, чтобы validator немедленно начал draining due-очереди.
                if (candidates.Count > 0) validationWakeSignal?.Pulse();

                var sourcesProcessed = sourceResults.Count;
                var sourcesSucceeded = sourceResults.Count(x => x.Error is null);
                var sourcesFailed = sourceResults.Count(x => x.Error is not null);
                var sourcesSkipped = allSources.Count - sources.Count;
                var sourcesTruncated = sourceResults.Count(x => x.Truncated);
                var aliveProxies = await db.Proxies.CountAsync(
                    x => x.Status == ProxyStatus.Alive, cancellationToken);

                // Сначала фиксируем source health, затем отдельным conditional UPDATE завершаем
                // только принадлежащую этому циклу running-строку. Обычный tracked UPDATE по ID
                // мог бы затереть параллельный administrative/restore результат.
                await db.SaveChangesAsync(cancellationToken);

                // Retention является частью collection pipeline, поэтому выполняется до
                // completed-аудита. Ошибка DELETE/cancellation тогда оставляет честный failed,
                // а не возвращает клиенту исключение при сохранённом ложном успехе.
                // Активная validation lease владеет строкой до сохранения результата.
                // Просроченная аренда ownership уже не даёт и не должна блокировать retention.
                // Помимо Dead удаляем так и не проверенные Pending: при длительной проблеме
                // control endpoint они иначе навсегда накапливаются после исчезновения из feed'ов.
                // Подтверждённые Alive сохраняются до очередной объективной проверки.
                await OperationalRetention.PruneProxyMembershipAsync(
                    db, now, options.Value.DeadRetentionDays, cancellationToken);
                // Долгая активная validation-партия другой реплики не должна потерять
                // ownership своей audit row из-за retention collection-цикла.
                await OperationalRetention.PruneRunHistoryAsync(
                    db, now, options.Value.RunRetentionDays, cancellationToken);

                var updated = await db.Runs
                    .Where(item => item.Id == run.Id && item.Status == "running")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.FinishedAt, now)
                        .SetProperty(item => item.SourcesProcessed, sourcesProcessed)
                        .SetProperty(item => item.SourcesSucceeded, sourcesSucceeded)
                        .SetProperty(item => item.SourcesFailed, sourcesFailed)
                        .SetProperty(item => item.SourcesSkipped, sourcesSkipped)
                        .SetProperty(item => item.SourcesTruncated, sourcesTruncated)
                        .SetProperty(item => item.CandidatesFound, candidates.Count)
                        .SetProperty(item => item.CandidateLimitReached, candidates.LimitReached)
                        .SetProperty(item => item.NewProxies, added)
                        .SetProperty(item => item.AliveProxies, aliveProxies)
                        .SetProperty(item => item.Status, "completed")
                        .SetProperty(item => item.Error, (string?)null), cancellationToken);
                if (updated != 1)
                    throw new InvalidOperationException(
                        "Collection-аудит потерял ownership своей running-строки.");

                // Возвращаемый объект отслеживался до ExecuteUpdate, поэтому синхронизируем
                // его явно без дополнительного UPDATE и второй точки отказа.
                run.FinishedAt = now;
                run.SourcesProcessed = sourcesProcessed;
                run.SourcesSucceeded = sourcesSucceeded;
                run.SourcesFailed = sourcesFailed;
                run.SourcesSkipped = sourcesSkipped;
                run.SourcesTruncated = sourcesTruncated;
                run.CandidatesFound = candidates.Count;
                run.CandidateLimitReached = candidates.LimitReached;
                run.NewProxies = added;
                run.AliveProxies = aliveProxies;
                run.Status = "completed";
                run.Error = null;

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
            // Исключение не даёт права перезаписывать уже завершённую другим владельцем строку.
            await auditDb.Runs
                .Where(item => item.Id == id && item.Status == "running")
                .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(item => item.Status, "failed")
                .SetProperty(item => item.Error, error[..Math.Min(2000, error.Length)]), timeout.Token);
        }
        catch (Exception auditException)
        {
            // Следующий cluster-lock-владелец восстановит оставшуюся running-строку.
            OperationalLogBoundary.Write(() => CollectionAuditFailed(logger, auditException));
        }
    }

    internal async Task<string> FetchSourceAsync(
        HttpClient client,
        string url,
        CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        var result = await FetchSourceStateAsync(
            client, url, httpETag: null, httpLastModifiedAt: null, token, delayAsync);
        return result.Content ?? throw new InvalidDataException("Источник неожиданно вернул 304 без validators.");
    }

    internal async Task<SourceFetchResult> FetchSourceStateAsync(
        HttpClient client,
        string url,
        string? httpETag,
        DateTimeOffset? httpLastModifiedAt,
        CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        delayAsync ??= static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);
        var retries = Math.Clamp(options.Value.SourceRetryCount, 0, 5);
        // Старые версии могли сохранить PostgreSQL infinity, а недоверенный feed —
        // прислать далёкое будущее. Такое значение не имеет права авторизовать 304.
        var requestLastModifiedAt = NormalizeLastModified(httpLastModifiedAt, DateTimeOffset.UtcNow);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, options.Value.SourceTimeoutSeconds)));
                using var response = await GetWithSafeRedirectsAsync(
                    client, url, httpETag, requestLastModifiedAt, timeout.Token);
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

                var responseETag = GetBoundedETag(response);
                var responseLastModifiedAt = NormalizeLastModified(
                    response.Content.Headers.LastModified, DateTimeOffset.UtcNow);
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    if (httpETag is null && requestLastModifiedAt is null)
                        throw new InvalidDataException("Источник вернул 304 без отправленного conditional validator.");
                    return new SourceFetchResult(
                        Content: null,
                        NotModified: true,
                        responseETag ?? httpETag,
                        responseLastModifiedAt ?? requestLastModifiedAt);
                }

                response.EnsureSuccessStatusCode();
                SourceFeedParser.EnsureSupportedMediaType(response.Content.Headers.ContentType?.MediaType);
                if (response.Content.Headers.ContentLength is > MaxSourceBytes)
                    throw new InvalidOperationException("Источник превышает лимит 10 МБ.");
                return new SourceFetchResult(
                    await ReadLimitedAsync(response.Content, MaxSourceBytes, timeout.Token),
                    NotModified: false,
                    responseETag,
                    responseLastModifiedAt);
            }
            catch (Exception ex) when (attempt < retries && SourceHttpRetry.IsRetryable(ex, token))
            {
                await delayAsync(
                    TimeSpan.FromMilliseconds(400 * (attempt + 1) + Random.Shared.Next(50, 250)), token);
            }
        }
    }

    private static async Task<HttpResponseMessage> GetWithSafeRedirectsAsync(
        HttpClient client,
        string url,
        string? httpETag,
        DateTimeOffset? httpLastModifiedAt,
        CancellationToken token)
    {
        EntityTagHeaderValue? parsedETag = null;
        if (httpETag is not null && !EntityTagHeaderValue.TryParse(httpETag, out parsedETag))
            throw new InvalidDataException("Сохранённый ETag источника имеет некорректный формат.");
        var current = new Uri(url, UriKind.Absolute);
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            if (!await NetworkSafety.IsSafePublicHttpsUrlAsync(current.AbsoluteUri, token))
                throw new HttpRequestException("Источник или его перенаправление ведёт в запрещённую сеть.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            // Conditional validators принадлежат representation исходного URI. Их
            // перенос на redirect-target способен раскрыть cross-origin ETag и дать
            // ложный 304, если владелец позже сменит Location на другой feed.
            if (redirect == 0)
            {
                if (parsedETag is not null) request.Headers.IfNoneMatch.Add(parsedETag);
                if (httpLastModifiedAt is not null) request.Headers.IfModifiedSince = httpLastModifiedAt;
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                if (redirect == 0) return response;
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    response.Dispose();
                    throw new InvalidDataException(
                        "Redirect-target вернул 304 без принадлежащего ему conditional validator.");
                }

                // Модель хранит validators по исходному Source.Url и не хранит effective
                // redirect URI. Не сохраняем ETag/Last-Modified другой representation.
                response.Headers.ETag = null;
                response.Content.Headers.LastModified = null;
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("Перенаправление источника не содержит Location.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new HttpRequestException("Источник превысил лимит в три перенаправления.");
    }

    private static string? GetBoundedETag(HttpResponseMessage response)
    {
        var value = response.Headers.ETag?.ToString();
        if (value is not null && (value.Length > 512 || value.Any(char.IsControl)))
            throw new InvalidDataException("ETag источника превышает лимит или содержит управляющие символы.");
        return value;
    }

    /// <summary>
    /// HTTP cache date хранится только в UTC и не может быть PostgreSQL infinity,
    /// доэпохальным либо более чем на сутки опережать часы collector'а.
    /// </summary>
    private static DateTimeOffset? NormalizeLastModified(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is null) return null;
        var utc = value.Value.ToUniversalTime();
        var latest = now.ToUniversalTime().AddDays(1);
        return utc >= DateTimeOffset.UnixEpoch && utc <= latest ? utc : null;
    }

    private static async Task<int> BulkUpsertAsync(
        ProxyHarborDbContext db,
        IEnumerable<(string Host, int Port, ProxyProtocol Protocol)> candidates,
        DateTimeOffset now,
        int lastSeenRefreshMinutes,
        CancellationToken token)
    {
        var candidateSnapshot = candidates.ToArray();
        // Production включает retry execution strategy. Вся временная таблица и обе
        // мутации входят в повторяемую транзакцию, поэтому transient PostgreSQL-сбой
        // не теряет целиком уже загруженный collection-цикл.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
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
                foreach (var candidate in candidateSnapshot)
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

            // LastSeenAt не является срочной мутацией: строки, занятые проверкой, безопасно
            // пропускаются и обновятся на следующем collection-цикле. Это не даёт collector'у
            // ждать validator locks и образовывать с ними обратный порядок блокировок.
            await using var refresh = new NpgsqlCommand("""
                WITH locked AS MATERIALIZED (
                    SELECT p."Id", i.seen_at
                    FROM "Proxies" p
                    JOIN proxy_import i
                      ON p."Host" = i.host AND p."Port" = i.port AND p."Protocol" = i.protocol
                    WHERE p."LastSeenAt" < @refresh_before
                    ORDER BY p."Id"
                    FOR UPDATE OF p SKIP LOCKED
                )
                UPDATE "Proxies" p
                SET "LastSeenAt" = locked.seen_at
                FROM locked
                WHERE p."Id" = locked."Id"
                """, connection, transaction);
            refresh.Parameters.AddWithValue("refresh_before", NpgsqlDbType.TimestampTz,
                now.AddMinutes(-Math.Max(1, lastSeenRefreshMinutes)));
            await refresh.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
            return added;
        });
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

    private sealed record SourceCollectionResult(
        Guid Id,
        string SourceUrl,
        ProxyProtocol SourceProtocol,
        int Count,
        bool Truncated,
        string? HttpETag,
        DateTimeOffset? HttpLastModifiedAt,
        bool ContentFetched,
        string? Error);
}

/// <summary>HTTP payload либо подтверждение неизменности feed'а вместе с новыми validators.</summary>
internal sealed record SourceFetchResult(
    string? Content,
    bool NotModified,
    string? HttpETag,
    DateTimeOffset? HttpLastModifiedAt);

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
        if (start.StartsWith("<!--", StringComparison.Ordinal) ||
            start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<body", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<meta", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<title", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<script", StringComparison.OrdinalIgnoreCase))
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

/// <summary>
/// Периодически отключает conditional validators, чтобы неизменившийся feed всё же
/// отдал полный body и восстановил кандидатов, удалённых локальной retention-политикой.
/// </summary>
internal static class SourceConditionalFetchPolicy
{
    internal static bool ShouldUseValidators(
        DateTimeOffset? lastContentFetchedAt,
        DateTimeOffset now,
        int deadRetentionDays)
    {
        if (lastContentFetchedAt is null || lastContentFetchedAt > now) return false;
        var retention = TimeSpan.FromDays(Math.Clamp(deadRetentionDays, 1, 365));
        var maximumBodyAge = TimeSpan.FromTicks(Math.Min(TimeSpan.FromDays(1).Ticks, retention.Ticks / 2));
        return now - lastContentFetchedAt.Value < maximumBodyAge;
    }
}
