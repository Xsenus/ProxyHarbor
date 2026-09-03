using System.Collections.Concurrent;
using System.Diagnostics;
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
    internal const int IndexedRefreshCandidateLimit = 100_000;
    internal const int HashImportCandidateThreshold = 50_000;
    private static readonly TimeSpan AuditWriteTimeout = TimeSpan.FromSeconds(15);
    private static readonly Action<ILogger, string, Exception?> SourceFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1001, "SourceFailed"), "Не удалось получить источник {Source}");
    private static readonly Action<ILogger, Exception?> CollectionAuditFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1002, "CollectionAuditFailed"),
            "Не удалось сохранить итоговый аудит цикла сбора.");
    private static readonly Action<ILogger, int, long, long, long, int, int, Exception?> BulkUpsertCompleted =
        LoggerMessage.Define<int, long, long, long, int, int>(LogLevel.Information,
            new EventId(1003, "BulkUpsertCompleted"),
            "Proxy import: {Candidates} кандидатов; COPY {CopyMs} мс, INSERT {InsertMs} мс, refresh {RefreshMs} мс; добавлено {Added}, обновлено {Refreshed}.");
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
                            source.LastSucceededAt,
                            source.LastItemCount,
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
                    source.LastError = result.Error?[..Math.Min(500, result.Error.Length)];
                    if (result.Error is null)
                    {
                        // Это поля последнего успешного результата, а не последней попытки.
                        // Временная HTTP-ошибка не должна стирать доказательство, которое
                        // позволяет безопасно принять следующий conditional 304.
                        source.LastItemCount = result.Count;
                        source.LastResultTruncated = result.Truncated;
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
                    db, candidates.Items, candidates.Count, now,
                    options.Value.LastSeenRefreshMinutes, cancellationToken);
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

                // now выше является единым timestamp данных каталога.
                // FinishedAt должен отражать конец всей работы цикла, включая импорт,
                // source health и aggregate, иначе duration скрывает ожидание PostgreSQL.
                // Retention намеренно выполняет отдельный cluster-wide maintenance worker:
                // полный поиск устаревших строк не должен тормозить каждый 5-минутный сбор.
                var finishedAt = DateTimeOffset.UtcNow;
                var updated = await db.Runs
                    .Where(item => item.Id == run.Id && item.Status == "running")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.FinishedAt, finishedAt)
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
                run.FinishedAt = finishedAt;
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
                var status = ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? "cancelled"
                    : "failed";
                await FinishUnsuccessfulRunAuditAsync(
                    run.Id,
                    ex,
                    status);
                throw;
            }
        }
        finally { _runGate.Release(); }
    }

    /// <summary>Освобождает синхронизатор запуска при остановке контейнера DI.</summary>
    public void Dispose() => _runGate.Dispose();

    private async Task FinishUnsuccessfulRunAuditAsync(Guid id, Exception exception, string status)
    {
        if (status is not ("cancelled" or "failed"))
            throw new ArgumentOutOfRangeException(nameof(status));
        try
        {
            // Ошибка могла оставить основной DbContext/connection в непригодном состоянии.
            // Отдельный контекст и bounded token не скрывают исходный сбой и не тормозят shutdown.
            using var timeout = new CancellationTokenSource(AuditWriteTimeout);
            await using var auditDb = await dbFactory.CreateDbContextAsync(timeout.Token);
            var error = status == "cancelled"
                ? "Сбор остановлен по сигналу отмены вызывающего процесса."
                : exception.ToString();
            // Исключение не даёт права перезаписывать уже завершённую другим владельцем строку.
            await auditDb.Runs
                .Where(item => item.Id == id && item.Status == "running")
                .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(item => item.Status, status)
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
        => await SourceHttpFetcher.FetchAsync(
            client,
            url,
            httpETag,
            httpLastModifiedAt,
            MaxSourceBytes,
            options.Value.SourceTimeoutSeconds,
            options.Value.SourceRetryCount,
            token,
            SourceFeedParser.EnsureSupportedMediaType,
            delayAsync);

    private async Task<int> BulkUpsertAsync(
        ProxyHarborDbContext db,
        IEnumerable<(string Host, int Port, ProxyProtocol Protocol)> candidates,
        int candidateCount,
        DateTimeOffset now,
        int lastSeenRefreshMinutes,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidateCount);
        // Conditional HTTP feeds commonly produce an entirely unchanged cycle. Without
        // this guard PostgreSQL still plans the empty temporary table as non-empty and
        // may scan the complete proxy registry while refreshing zero rows.
        if (candidateCount == 0) return 0;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var create = new NpgsqlCommand(
            "CREATE TEMP TABLE proxy_import (host text NOT NULL, port integer NOT NULL, protocol integer NOT NULL) ON COMMIT DROP",
            connection, transaction))
            await create.ExecuteNonQueryAsync(token);

        var phaseStarted = Stopwatch.GetTimestamp();
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY proxy_import (host, port, protocol) FROM STDIN (FORMAT BINARY)", token))
        {
            // WriteRowAsync отдаёт Npgsql целую строку за один async-вызов.
            // На сотнях тысяч endpoint'ов это убирает миллионы мелких
            // await-переходов; одинаковый seen_at передаётся ниже SQL-параметром.
            var row = new object[3];
            foreach (var candidate in candidates)
            {
                row[0] = candidate.Host;
                row[1] = candidate.Port;
                row[2] = (int)candidate.Protocol;
                await writer.WriteRowAsync(token, row);
            }
            await writer.CompleteAsync(token);
        }
        var copyMs = (long)Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        // Binary COPY intentionally bypasses PostgreSQL statistics collection. On a
        // large temporary relation the default estimate is therefore far below the
        // real row count and the anti-join below can degrade into one index probe per
        // candidate. Production profiling on a 906k-row registry measured 500k
        // duplicate candidates at 13.08 s with that nested-loop plan versus 3.47 s
        // with one bounded in-memory hash of the registry. Collection is protected by
        // a cluster-wide lock, so this transaction is the only large importer and a
        // 64 MiB work_mem budget cannot multiply across concurrent collection runs.
        // INSERT и LastSeen refresh имеют отдельные crossover: production-партия
        // 62k прочитала 30k buffers hash-планом вместо 240k при index probes, тогда
        // как refresh до 100k всё ещё дешевле выполняется через индекс.
        if (PreferHashImport(candidateCount))
        {
            await using var planner = new NpgsqlCommand("""
                ANALYZE proxy_import;
                SET LOCAL work_mem = '64MB';
                SET LOCAL enable_nestloop = off;
                SET LOCAL enable_mergejoin = off
                """, connection, transaction);
            await planner.ExecuteNonQueryAsync(token);
        }

        // Отдельный INSERT возвращает точное число новых строк и не заставляет PostgreSQL
        // выполнять бесполезный UPDATE каждого существующего proxy на каждом 15-минутном цикле.
        phaseStarted = Stopwatch.GetTimestamp();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO "Proxies" ("Id", "Host", "Port", "Protocol", "Status", "IsAnonymous", "FirstSeenAt", "LastSeenAt", "SuccessfulChecks", "FailedChecks")
            SELECT gen_random_uuid(), i.host, i.port, i.protocol, 0, false, @seen_at, @seen_at, 0, 0
            FROM proxy_import i
            WHERE NOT EXISTS (
                SELECT 1 FROM "Proxies" p
                WHERE p."Host" = i.host AND p."Port" = i.port AND p."Protocol" = i.protocol)
            ON CONFLICT ("Host", "Port", "Protocol") DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("seen_at", NpgsqlDbType.TimestampTz, now);
        var added = await insert.ExecuteNonQueryAsync(token);
        var insertMs = (long)Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        // LastSeenAt не является срочной мутацией: строки, занятые проверкой, безопасно
        // пропускаются и обновятся на следующем collection-цикле. Это не даёт collector'у
        // ждать validator locks и образовывать с ними обратный порядок блокировок.
        // Для небольшого импорта PostgreSQL без статистики временной таблицы склонен
        // строить hash join с полным чтением реестра. На production это означало чтение
        // около 1 ГБ ради 53 тысяч кандидатов. Ограничение действует только на последний
        // statement текущей транзакции; крупные импорты сохраняют свободу выбрать
        // последовательный план, когда он действительно дешевле.
        if (PreferIndexedLastSeenRefresh(candidateCount))
        {
            await using var planner = new NpgsqlCommand(
                "SET LOCAL enable_hashjoin = off; SET LOCAL enable_mergejoin = off",
                connection,
                transaction);
            await planner.ExecuteNonQueryAsync(token);
        }
        phaseStarted = Stopwatch.GetTimestamp();
        await using var refresh = new NpgsqlCommand("""
            WITH locked AS MATERIALIZED (
                SELECT p."Id"
                FROM "Proxies" p
                JOIN proxy_import i
                  ON p."Host" = i.host AND p."Port" = i.port AND p."Protocol" = i.protocol
                WHERE p."LastSeenAt" < @refresh_before
                ORDER BY p."Id"
                FOR UPDATE OF p SKIP LOCKED
            )
            UPDATE "Proxies" p
            SET "LastSeenAt" = @seen_at
            FROM locked
            WHERE p."Id" = locked."Id"
            """, connection, transaction);
        refresh.Parameters.AddWithValue("refresh_before", NpgsqlDbType.TimestampTz,
            now.AddMinutes(-Math.Max(1, lastSeenRefreshMinutes)));
        refresh.Parameters.AddWithValue("seen_at", NpgsqlDbType.TimestampTz, now);
        var refreshed = await refresh.ExecuteNonQueryAsync(token);
        var refreshMs = (long)Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
        await transaction.CommitAsync(token);
        OperationalLogBoundary.Write(() => BulkUpsertCompleted(
            logger, candidateCount, copyMs, insertMs, refreshMs, added, refreshed, null));
        return added;
    }

    internal static bool PreferIndexedLastSeenRefresh(int candidateCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidateCount);
        return candidateCount is > 0 and <= IndexedRefreshCandidateLimit;
    }

    /// <summary>
    /// Средний и крупный staging-набор дешевле сопоставить одним hash anti-join, чем выполнять
    /// отдельный поиск по широкому уникальному индексу для каждого кандидата.
    /// </summary>
    internal static bool PreferHashImport(int candidateCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidateCount);
        return candidateCount > HashImportCandidateThreshold;
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
        DateTimeOffset? lastSucceededAt,
        int lastItemCount,
        DateTimeOffset now,
        int deadRetentionDays)
    {
        if (lastSucceededAt is null || lastItemCount <= 0 ||
            lastContentFetchedAt is null || lastContentFetchedAt > now)
            return false;
        var retention = TimeSpan.FromDays(Math.Clamp(deadRetentionDays, 1, 365));
        var maximumBodyAge = TimeSpan.FromTicks(Math.Min(TimeSpan.FromDays(1).Ticks, retention.Ticks / 2));
        return now - lastContentFetchedAt.Value < maximumBodyAge;
    }
}
