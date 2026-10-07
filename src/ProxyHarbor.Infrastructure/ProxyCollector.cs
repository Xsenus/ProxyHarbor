using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
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
    ValidationWakeSignal? validationWakeSignal = null,
    IDataProtectionProvider? credentialProtectionProvider = null) : IDisposable
{
    private const int MaxSourceBytes = 10_000_000;
    internal const int HashImportCandidateThreshold = 10_000;
    internal const int LastSeenRefreshBatchSize = 1_000;
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
    private static readonly Action<ILogger, Exception?> ImportCleanupFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1004, "ImportCleanupFailed"),
            "Не удалось удалить временную таблицу proxy_import; она будет удалена при закрытии соединения.");
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly IDataProtector? _credentialProtector = credentialProtectionProvider is null
        ? null
        : ProxySourceCredentialProtection.Create(credentialProtectionProvider);

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
                var importStore = new ProxySourceImportStore(dbFactory);
                var apiStore = new SourceApiCaptureStore(dbFactory);
                await apiStore.CleanupAsync(cancellationToken);
                var completeApiSources = (await db.SourceApiCaptureStates.AsNoTracking()
                    .Where(state => state.Complete && state.ProxySourceId != null)
                    .Select(state => state.ProxySourceId!.Value).ToArrayAsync(cancellationToken)).ToHashSet();
                // Старые snapshots отключённых/изменённых feed'ов не должны занимать
                // storage-квоту постоянно; отсутствие state требует полного re-fetch.
                await db.ProxySourceImportStates.Where(state => !db.Sources.Any(source =>
                        source.Id == state.ProxySourceId && source.Enabled && source.Url == state.SourceUrl &&
                        source.DefaultProtocol == state.SourceProtocol))
                    .ExecuteDeleteAsync(cancellationToken);
                var importMetadata = await db.ProxySourceImportStates.AsNoTracking()
                    .Select(state => new
                    {
                        state.ProxySourceId,
                        state.SourceUrl,
                        state.SourceProtocol,
                        state.NextIndex,
                        state.CandidateCount,
                        state.LastProgressAt
                    }).ToDictionaryAsync(state => state.ProxySourceId, cancellationToken);
                var sources = allSources.Where(source =>
                        SourceFetchSchedule.IsDue(source.NextFetchAt, collectionStartedAt, forceAllSources, source.Url) ||
                        completeApiSources.Contains(source.Id) ||
                        (importMetadata.TryGetValue(source.Id, out var state) && state.SourceUrl == source.Url &&
                            state.SourceProtocol == source.DefaultProtocol && state.NextIndex < state.CandidateCount))
                    .OrderBy(source => importMetadata.TryGetValue(source.Id, out var state) &&
                        state.SourceUrl == source.Url && state.SourceProtocol == source.DefaultProtocol
                            ? state.LastProgressAt : null)
                    .ThenBy(source => source.Priority).ThenBy(source => source.Id).ToList();
                var candidates = new BoundedProxyCandidateSet(options.Value.MaxCandidatesPerRun);
                var sourceResults = new ConcurrentBag<SourceCollectionResult>();
                var importProgress = new ConcurrentBag<SourceImportProgress>();
                var importFailures = new ConcurrentBag<Exception>();
                var eligibleSourceIds = sources.Select(source => source.Id).ToArray();
                var credentials = await db.ProxySourceCredentials.AsNoTracking()
                    .Where(item => eligibleSourceIds.Contains(item.ProxySourceId))
                    .ToDictionaryAsync(item => item.ProxySourceId, cancellationToken);
                var paidSources = sources.Where(PaidProxySourceCatalog.IsPaid).ToArray();
                var publicSources = sources.Where(source => !PaidProxySourceCatalog.IsPaid(source)).ToArray();

                // Платный набор получает bounded-квоту первым и не может быть вытеснен
                // миллионами кандидатов бесплатных feed'ов текущего цикла.
                await CollectSourcesAsync(
                    paidSources, credentials, candidates, sourceResults,
                    importStore, importProgress, importFailures, collectionStartedAt, forceAllSources, cancellationToken);
                await CollectSourcesAsync(
                    publicSources, credentials, candidates, sourceResults,
                    importStore, importProgress, importFailures, collectionStartedAt, forceAllSources, cancellationToken);

                var sourceResultById = sourceResults.ToDictionary(result => result.Id);
                var sourceIds = sourceResultById.Keys.ToArray();
                var trackedSources = await db.Sources.Where(source => sourceIds.Contains(source.Id)).ToListAsync(cancellationToken);
                foreach (var source in trackedSources)
                {
                    var result = sourceResultById[source.Id];
                    // Cached-only импорт не является новой HTTP-проверкой источника.
                    // Admin мог заменить endpoint, пока старый HTTP-запрос находился в полёте.
                    // Результат старой конфигурации нельзя приписывать новой: особенно ETag и
                    // LastContentFetchedAt, иначе новый feed способен получать ложные 304.
                    if (!string.Equals(source.Url, result.SourceUrl, StringComparison.Ordinal) ||
                        source.DefaultProtocol != result.SourceProtocol)
                        continue;
                    if (!result.FetchObserved)
                    {
                        if (result.RetryNotBefore > source.NextFetchAt ||
                            source.NextFetchAt is null && result.RetryNotBefore is not null)
                            source.NextFetchAt = result.RetryNotBefore;
                        continue;
                    }
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
                        source.NextFetchAt = SourceFetchSchedule.NextSuccessAttempt(source.Url, fetchedAt);
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
                        if (result.RetryNotBefore > source.NextFetchAt)
                            source.NextFetchAt = result.RetryNotBefore;
                    }
                }

                var paidResults = sourceResultById.Values
                    .Where(result => result.CredentialStatus is not null).ToArray();
                if (paidResults.Length > 0)
                {
                    var paidIds = paidResults.Select(result => result.Id).ToArray();
                    var trackedCredentials = await db.ProxySourceCredentials
                        .Where(item => paidIds.Contains(item.ProxySourceId)).ToDictionaryAsync(
                            item => item.ProxySourceId, cancellationToken);
                    foreach (var result in paidResults)
                    {
                        if (!trackedCredentials.TryGetValue(result.Id, out var credential)) continue;
                        // Older versions marked every 403 as expired and wrote the run
                        // timestamp as ExpiresAt. That date was never provider evidence.
                        if (credential.Status == "expired") credential.ExpiresAt = null;
                        credential.Status = result.CredentialStatus!;
                        credential.CheckedAt = result.CredentialCheckedAt;
                        // HTTP-отказ или сбой TTL не доказывает новый срок действия.
                        // Замена самого ключа сбрасывает предыдущую дату в admin endpoint.
                        if (result.CredentialExpiresAt is not null)
                            credential.ExpiresAt = result.CredentialExpiresAt;
                        credential.LastError = result.CredentialError;
                    }
                }

                var now = DateTimeOffset.UtcNow;
                // Доступность feed и импорт endpoint'ов — разные факты. Успешный HTTP/parser
                // аудит не должен откатываться из-за последующего сбоя PostgreSQL bulk import.
                await db.SaveChangesAsync(cancellationToken);
                var added = await BulkUpsertAsync(
                    db, candidates.ImportItems, candidates.Count, now,
                    options.Value.LastSeenRefreshMinutes, cancellationToken);
                // Snapshot сохранялся до admission, но cursor подтверждается только
                // после commit endpoint'ов. Crash между ними приводит к безопасному replay.
                foreach (var progress in importProgress)
                    _ = await importStore.AcknowledgeCommittedImportAsync(
                        progress.State, progress.NextIndex, DateTimeOffset.UtcNow, cancellationToken,
                        progress.FreshBodyHash, progress.PreferFresh);
                // Bounded signal не накапливает по событию на каждый feed/endpoint:
                // одного wake достаточно, чтобы validator немедленно начал draining due-очереди.
                if (candidates.Count > 0) validationWakeSignal?.Pulse();
                // Сначала подтверждаем остальные snapshots и освобождаем место:
                // storage pressure не должен создавать deadlock с заполненным cache.
                if (!importFailures.IsEmpty)
                    throw new InvalidOperationException("Не удалось завершить возобновляемый импорт proxy-источников.",
                        new AggregateException(importFailures));

                var sourcesProcessed = sourceResults.Count;
                var sourcesSucceeded = sourceResults.Count(x => x.Error is null);
                var sourcesFailed = sourceResults.Count(x => x.Error is not null);
                var sourcesSkipped = allSources.Count - sources.Count;
                var sourcesTruncated = sourceResults.Count(x => x.Truncated);
                var aliveProxies = await db.Proxies.CountAsync(
                    x => x.Status == ProxyStatus.Alive, cancellationToken);

                // Source health уже зафиксирован до bulk import. Здесь сохраняем только возможные
                // изменения остальных tracked entities, затем завершаем принадлежащий циклу audit.
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

    private async Task CollectSourcesAsync(
        ProxySource[] sources,
        Dictionary<Guid, ProxySourceCredential> credentials,
        BoundedProxyCandidateSet candidates,
        ConcurrentBag<SourceCollectionResult> sourceResults,
        ProxySourceImportStore importStore,
        ConcurrentBag<SourceImportProgress> importProgress,
        ConcurrentBag<Exception> importFailures,
        DateTimeOffset collectionStartedAt,
        bool forceAllSources,
        CancellationToken cancellationToken)
    {
        if (sources.Length == 0) return;
        var phaseIsPaid = PaidProxySourceCatalog.IsPaid(sources[0]);
        var client = httpClientFactory.CreateClient(phaseIsPaid ? "paid-sources" : "sources");
        await Parallel.ForEachAsync(sources, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(
                Math.Clamp(options.Value.SourceConcurrency, 1, 32), sources.Length),
            CancellationToken = cancellationToken
        }, async (source, token) =>
        {
            var paid = PaidProxySourceCatalog.IsPaid(source);
            ProxySourceImportState? importState = null;
            ProxyCandidateSnapshot? newSnapshot = null;
            List<ProxyCandidateKey>? freshCandidates = null;
            byte[]? freshBodyHash = null;
            SourceApiFetchResult? apiFetch = null;
            try
            {
                importState = await importStore.LoadAsync(source, token);
                var apiOwner = FreeProxyDbPageCapture.SupportsHttp(source.Url) ? SourceApiCaptureOwner.From(source) : null;
                var apiCheckpoint = apiOwner is null ? null : await new SourceApiCaptureStore(dbFactory).LoadAsync(apiOwner, token);
                var apiComplete = apiCheckpoint?.Capture.Inspect(MaxSourceBytes).Complete == true;
                if (!apiComplete && !SourceFetchSchedule.IsDue(source.NextFetchAt, collectionStartedAt, forceAllSources, source.Url))
                {
                    sourceResults.Add(new SourceCollectionResult(
                        source.Id, source.Url, source.DefaultProtocol,
                        source.LastItemCount, source.LastResultTruncated,
                        source.HttpETag, source.HttpLastModifiedAt,
                        ContentFetched: false,
                        Error: importState is null ? "Снимок импорта утрачен; требуется повторная загрузка по расписанию." : null,
                        FetchObserved: false));
                    return;
                }
                var credentialStatus = (string?)null;
                var credentialCheckedAt = (DateTimeOffset?)null;
                var credentialExpiresAt = (DateTimeOffset?)null;
                var credentialError = (string?)null;
                SourceFetchResult fetched;
                if (paid)
                {
                    if (_credentialProtector is null ||
                        !credentials.TryGetValue(source.Id, out var credential))
                        throw new PaidSourceException(
                            "not_configured", "Ключ платного источника не настроен.");
                    string apiKey;
                    try { apiKey = _credentialProtector.Unprotect(credential.ProtectedApiKey); }
                    catch (CryptographicException exception)
                    {
                        throw new PaidSourceException(
                            "invalid", "Сохранённый ключ не удалось расшифровать.", exception);
                    }
                    if (!ProxySourceApiKeyPolicy.IsValid(apiKey))
                        throw new PaidSourceException(
                            "invalid", "Сохранённый ключ имеет недопустимый формат.");

                    try
                    {
                        fetched = await FetchPaidSourceStateAsync(
                            client, source, apiKey, forceAllSources || importState is null, collectionStartedAt, token);
                        credentialCheckedAt = DateTimeOffset.UtcNow;
                        credentialStatus = "active";
                        try
                        {
                            var status = await SourceHttpFetcher.FetchAsync(
                                client,
                                PaidProxySourceCatalog.BuildStatusUrl(apiKey),
                                null,
                                null,
                                128,
                                options.Value.SourceTimeoutSeconds,
                                options.Value.SourceRetryCount,
                                token,
                                delayAsync: null,
                                sameOriginRedirectsOnly: true);
                            credentialExpiresAt = PaidProxySourceCatalog.ParseExpiration(
                                status.Content ?? string.Empty, credentialCheckedAt.Value);
                        }
                        catch (Exception exception) when (
                            exception is not OperationCanceledException || !token.IsCancellationRequested)
                        {
                            // Успешный proxylist уже доказывает активность ключа. Сбой
                            // вспомогательного TTL endpoint не отменяет полезный список.
                            credentialError = "Не удалось обновить срок действия ключа.";
                        }
                    }
                    catch (HttpRequestException exception)
                    {
                        var failure = PaidSourceHttpFailure.From(exception.StatusCode);
                        throw new PaidSourceException(failure.Status, failure.Message, exception);
                    }
                }
                else
                {
                    // Admin force-run является полным аудитом и требует новый body.
                    var useValidators = !forceAllSources && importState is not null && SourceConditionalFetchPolicy.ShouldUseValidators(
                        source.LastContentFetchedAt,
                        source.LastSucceededAt,
                        source.LastItemCount,
                        collectionStartedAt,
                        options.Value.DeadRetentionDays);
                    if (apiOwner is not null)
                    {
                        apiFetch = await new FreeProxyDbSourceApiFetcher(dbFactory).FetchAsync(apiOwner,
                            (url, pageToken) => SourceHttpFetcher.FetchAsync(client, url, null, null,
                                FreeProxyDbPageCapture.MaximumPageBytes, options.Value.SourceTimeoutSeconds,
                                options.Value.SourceRetryCount, pageToken, SourceFeedParser.EnsureSupportedMediaType,
                                sameOriginRedirectsOnly: true, respectRateLimit: true, sourceApiRequest: true), token);
                        fetched = apiFetch.Fetch;
                    }
                    else fetched = await FetchSourceStateAsync(
                        client,
                        source.Url,
                        useValidators ? source.HttpETag : null,
                        useValidators ? source.HttpLastModifiedAt : null,
                        token);
                }

                if (fetched.NotModified)
                {
                    if (source.LastSucceededAt is null || source.LastItemCount <= 0 || importState is null)
                        throw new InvalidDataException(
                            "Источник вернул 304 без сохранённого успешного результата и import state.");
                    sourceResults.Add(new SourceCollectionResult(
                        source.Id, source.Url, source.DefaultProtocol,
                        source.LastItemCount, source.LastResultTruncated,
                        fetched.HttpETag, fetched.HttpLastModifiedAt,
                        ContentFetched: false, Error: null,
                        credentialStatus, credentialCheckedAt, credentialExpiresAt, credentialError));
                    return;
                }

                var content = fetched.Content ??
                    throw new InvalidDataException("Успешный ответ источника не содержит body.");
                ProxyParseSummary parsed;
                if (importState is not null && importState.NextIndex < importState.CandidateCount)
                {
                    // Health описывает текущий body; bounded свежий prefix получает
                    // долю той же source-квоты после проверки актуальности конфигурации.
                    var hash = ProxyCandidateSnapshotCodec.HashBody(content);
                    var unchanged = hash.AsSpan().SequenceEqual(importState.FreshBodyHash);
                    // Не сохраняем validators неизвестного свежего окна до его commit.
                    // Иначе следующий 304 потеряет повтор при global pressure/смене lane.
                    // Исходный body уже целиком сохранён в snapshot и допускает 304.
                    if (!unchanged)
                        fetched = fetched with { HttpETag = null, HttpLastModifiedAt = null };
                    var freshLimit = Math.Min(options.Value.MaxCandidatesPerRun,
                        Math.Max(1, options.Value.MaxProxiesPerSource / 2));
                    if (importState.PreferFresh && !unchanged)
                    {
                        freshBodyHash = hash;
                        freshCandidates = new List<ProxyCandidateKey>(Math.Min(freshLimit, 4_096));
                    }
                    parsed = SourceFeedParser.ParseBoundedToRequired(content,
                        source.DefaultProtocol, options.Value.MaxProxiesPerSource, candidate =>
                        {
                            if (freshCandidates is not null && freshCandidates.Count < freshLimit)
                                freshCandidates.Add(candidate);
                        });
                }
                else
                {
                    newSnapshot = ProxyCandidateSnapshotCodec.Encode(content, source.DefaultProtocol);
                    parsed = new ProxyParseSummary(Math.Min(newSnapshot.Count, options.Value.MaxProxiesPerSource),
                        newSnapshot.Count > options.Value.MaxProxiesPerSource);
                }
                sourceResults.Add(new SourceCollectionResult(
                    source.Id, source.Url, source.DefaultProtocol,
                    parsed.Count, parsed.Truncated,
                    fetched.HttpETag, fetched.HttpLastModifiedAt,
                    ContentFetched: apiFetch?.NetworkObserved ?? true, Error: null,
                    credentialStatus, credentialCheckedAt, credentialExpiresAt, credentialError,
                    FetchObserved: apiFetch?.NetworkObserved ?? true,
                    RetryNotBefore: apiFetch?.NextRefreshAt));
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                freshCandidates = null;
                freshBodyHash = null;
                if (apiFetch is not null && exception is InvalidDataException)
                    await new SourceApiCaptureStore(dbFactory).DiscardAsync(apiFetch.Checkpoint, token);
                var paidException = exception as PaidSourceException;
                var safeError = paidException?.Message ?? exception.Message;
                if (exception is not SourceApiDeferredException)
                    OperationalLogBoundary.Write(() => SourceFailed(logger, source.Name, exception));
                sourceResults.Add(new SourceCollectionResult(
                    source.Id, source.Url, source.DefaultProtocol,
                    0, false, null, null, ContentFetched: false,
                    exception is SourceApiDeferredException ? null : safeError,
                    paidException?.Status,
                    paid ? DateTimeOffset.UtcNow : null,
                    null,
                    paidException?.Message,
                    FetchObserved: exception is not SourceApiDeferredException,
                    RetryNotBefore: (exception as SourceRateLimitException)?.RetryNotBefore ??
                        (exception as SourceApiDeferredException)?.NotBefore));
            }
            finally
            {
                try
                {
                    if (newSnapshot is not null)
                    {
                        importState = await importStore.BeginAsync(source, newSnapshot, token);
                        if (apiFetch is not null && importState is not null &&
                            importState.CandidateCount == newSnapshot.Count &&
                            importState.PayloadHash.AsSpan().SequenceEqual(SHA256.HashData(newSnapshot.Payload)))
                            await new SourceApiCaptureStore(dbFactory).DiscardAsync(apiFetch.Checkpoint, token);
                    }
                    if (importState is not null && importState.NextIndex < importState.CandidateCount &&
                        await importStore.IsCurrentOrDiscardAsync(importState, token))
                    {
                        var acceptedFresh = 0;
                        if (freshCandidates is not null)
                            foreach (var candidate in freshCandidates)
                            {
                                if (!candidates.TryAccept(candidate, paid)) break;
                                acceptedFresh++;
                            }
                        var remaining = options.Value.MaxProxiesPerSource - acceptedFresh;
                        var nextIndex = importState.NextIndex;
                        if (remaining > 0)
                            nextIndex = ProxySourceImportStore.ReadWindow(importState,
                                remaining, candidate => candidates.TryAccept(candidate, paid)).NextIndex;
                        if (nextIndex > importState.NextIndex || acceptedFresh > 0)
                            importProgress.Add(new SourceImportProgress(
                                ProxySourceImportCheckpoint.Capture(importState), nextIndex,
                                acceptedFresh > 0 && acceptedFresh == freshCandidates?.Count ? freshBodyHash : null,
                                nextIndex > importState.NextIndex));
                    }
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    importFailures.Add(exception);
                }
            }
        });
    }

    private async Task<SourceFetchResult> FetchPaidSourceStateAsync(
        HttpClient client,
        ProxySource source,
        string apiKey,
        bool forceAllSources,
        DateTimeOffset collectionStartedAt,
        CancellationToken token)
    {
        var useValidators = !forceAllSources && SourceConditionalFetchPolicy.ShouldUseValidators(
            source.LastContentFetchedAt,
            source.LastSucceededAt,
            source.LastItemCount,
            collectionStartedAt,
            options.Value.DeadRetentionDays);
        return await SourceHttpFetcher.FetchAsync(
            client,
            PaidProxySourceCatalog.BuildListUrl(apiKey),
            useValidators ? source.HttpETag : null,
            useValidators ? source.HttpLastModifiedAt : null,
            MaxSourceBytes,
            options.Value.SourceTimeoutSeconds,
            options.Value.SourceRetryCount,
            token,
            SourceFeedParser.EnsureSupportedMediaType,
            delayAsync: null,
            sameOriginRedirectsOnly: true);
    }

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
    {
        if (FreeProxyDbPageCapture.IsSearchUrl(url) || ProxiwarePublicApi.IsOriginUrl(url) || RoundProxiesPublicApi.IsApiUrl(url) || Socks5ProxiesPublicApi.IsApiUrl(url))
            throw new InvalidDataException("Постраничный API требует канонический URL зарегистрированного источника и сохраняемую очередь страниц.");
        var htmlList = MyProxyHtmlFeedAdapter.Supports(url);
        var result = await SourceHttpFetcher.FetchAsync(
            client,
            url,
            httpETag,
            httpLastModifiedAt,
            MaxSourceBytes,
            options.Value.SourceTimeoutSeconds,
            options.Value.SourceRetryCount,
            token,
            htmlList ? MyProxyHtmlFeedAdapter.EnsureSupportedMediaType : SourceFeedParser.EnsureSupportedMediaType,
            delayAsync,
            sameOriginRedirectsOnly: htmlList,
            respectRateLimit: htmlList);
        return htmlList && !result.NotModified
            ? result with { Content = MyProxyHtmlFeedAdapter.Extract(url, result.Content ?? throw new InvalidDataException("Источник не содержит body.")) }
            : result;
    }

    private async Task<int> BulkUpsertAsync(
        ProxyHarborDbContext db,
        IEnumerable<(string Host, int Port, ProxyProtocol Protocol, bool Preferred)> candidates,
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
        var copyMs = 0L;
        var insertMs = 0L;
        var refreshMs = 0L;
        var added = 0;
        var refreshed = 0;
        try
        {
            // PRESERVE ROWS позволяет отпускать row locks после каждой небольшой refresh-
            // партии. Раньше один UPDATE сотен тысяч строк превышал Npgsql timeout и на всё
            // время удерживал validator в transactionid wait.
            await using (var importTransaction = await connection.BeginTransactionAsync(token))
            {
                await using (var create = new NpgsqlCommand(
                    "CREATE TEMP TABLE proxy_import (host text NOT NULL, port integer NOT NULL, protocol integer NOT NULL, preferred boolean NOT NULL, proxy_id uuid) ON COMMIT PRESERVE ROWS",
                    connection, importTransaction))
                    await create.ExecuteNonQueryAsync(token);

                var phaseStarted = Stopwatch.GetTimestamp();
                await using (var writer = await connection.BeginBinaryImportAsync(
                    "COPY proxy_import (host, port, protocol, preferred) FROM STDIN (FORMAT BINARY)", token))
                {
                    var row = new object[4];
                    foreach (var candidate in candidates)
                    {
                        row[0] = candidate.Host;
                        row[1] = candidate.Port;
                        row[2] = (int)candidate.Protocol;
                        row[3] = candidate.Preferred;
                        await writer.WriteRowAsync(token, row);
                    }
                    await writer.CompleteAsync(token);
                }
                copyMs = (long)Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

                // Binary COPY не собирает статистику. Для крупного staging явно выбираем
                // один bounded hash registry вместо сотен тысяч отдельных index probes.
                if (PreferHashImport(candidateCount))
                {
                    await using var planner = new NpgsqlCommand("""
                        ANALYZE proxy_import;
                        SET LOCAL work_mem = '64MB';
                        SET LOCAL enable_nestloop = off;
                        SET LOCAL enable_mergejoin = off
                        """, connection, importTransaction);
                    await planner.ExecuteNonQueryAsync(token);
                }

                // Отдельный INSERT возвращает точное число новых строк.
                phaseStarted = Stopwatch.GetTimestamp();
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO "Proxies" ("Id", "Host", "Port", "Protocol", "Status", "IsAnonymous", "FirstSeenAt", "LastSeenAt", "NextCheckAt", "SuccessfulChecks", "FailedChecks")
                    SELECT gen_random_uuid(), i.host, i.port, i.protocol, 0, false, @seen_at, @seen_at,
                           CASE WHEN i.preferred THEN @priority_at ELSE @seen_at END, 0, 0
                    FROM proxy_import i
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "Proxies" p
                        WHERE p."Host" = i.host AND p."Port" = i.port AND p."Protocol" = i.protocol)
                    ON CONFLICT ("Host", "Port", "Protocol") DO NOTHING
                    """, connection, importTransaction);
                insert.Parameters.AddWithValue("seen_at", NpgsqlDbType.TimestampTz, now);
                insert.Parameters.AddWithValue("priority_at", NpgsqlDbType.TimestampTz,
                    PaidProxySourceCatalog.ImmediateValidationMarker);
                added = await insert.ExecuteNonQueryAsync(token);
                insertMs = (long)Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

                // Один read-only hash/index join сопоставляет staging с UUID. Последующие
                // UPDATE используют только PK и не перечитывают широкий registry на каждую партию.
                await using var map = new NpgsqlCommand("""
                    UPDATE proxy_import i
                    SET proxy_id = p."Id"
                    FROM "Proxies" p
                    WHERE p."Host" = i.host AND p."Port" = i.port AND p."Protocol" = i.protocol
                    """, connection, importTransaction);
                await map.ExecuteNonQueryAsync(token);

                await using var prune = new NpgsqlCommand("""
                    DELETE FROM proxy_import i
                    USING "Proxies" p
                    WHERE p."Id" = i.proxy_id AND NOT i.preferred AND p."LastSeenAt" >= @refresh_before
                    """, connection, importTransaction);
                prune.Parameters.AddWithValue("refresh_before", NpgsqlDbType.TimestampTz,
                    now.AddMinutes(-Math.Max(1, lastSeenRefreshMinutes)));
                await prune.ExecuteNonQueryAsync(token);

                // Индекс строится уже после удаления свежих строк. Каждая следующая партия
                // читает следующие UUID по порядку без повторной сортировки всего staging.
                await using var index = new NpgsqlCommand("""
                    CREATE INDEX proxy_import_proxy_id_idx ON proxy_import (proxy_id);
                    ANALYZE proxy_import
                    """, connection, importTransaction);
                await index.ExecuteNonQueryAsync(token);
                await importTransaction.CommitAsync(token);
            }

            // Занятые валидатором строки безопасно пропускаются и обновятся при следующем
            // появлении в feed. Коммит каждой bounded-партии быстро освобождает остальные locks.
            var refreshStarted = Stopwatch.GetTimestamp();
            var refreshBatchSize = Math.Min(LastSeenRefreshBatchSize, candidateCount);
            while (true)
            {
                try
                {
                    await using var refreshTransaction = await connection.BeginTransactionAsync(token);
                    await using (var timeout = new NpgsqlCommand(
                        "SET LOCAL statement_timeout = '3s'", connection, refreshTransaction))
                        await timeout.ExecuteNonQueryAsync(token);
                    await using var refresh = new NpgsqlCommand("""
                    WITH candidates AS MATERIALIZED (
                        SELECT proxy_id, preferred
                        FROM proxy_import
                        ORDER BY proxy_id
                        LIMIT @batch_size
                    ), locked AS MATERIALIZED (
                        SELECT endpoint."Id", candidate.preferred
                        FROM candidates candidate
                        CROSS JOIN LATERAL (
                            SELECT p."Id"
                            FROM "Proxies" p
                            WHERE p."Id" = candidate.proxy_id
                            FOR UPDATE SKIP LOCKED
                        ) endpoint
                    ), updated AS (
                        UPDATE "Proxies" p
                        SET "LastSeenAt" = @seen_at,
                            "NextCheckAt" = CASE WHEN locked.preferred THEN @priority_at ELSE p."NextCheckAt" END
                        FROM locked
                        WHERE p."Id" = locked."Id"
                        RETURNING p."Id"
                    ), removed AS (
                        DELETE FROM proxy_import i
                        USING candidates
                        WHERE i.proxy_id = candidates.proxy_id
                        RETURNING i.proxy_id
                    )
                    SELECT (SELECT count(*) FROM updated), (SELECT count(*) FROM removed)
                    """, connection, refreshTransaction);
                    refresh.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, refreshBatchSize);
                    refresh.Parameters.AddWithValue("seen_at", NpgsqlDbType.TimestampTz, now);
                    refresh.Parameters.AddWithValue("priority_at", NpgsqlDbType.TimestampTz,
                        PaidProxySourceCatalog.ImmediateValidationMarker);
                    long batchRefreshed;
                    long batchProcessed;
                    await using (var reader = await refresh.ExecuteReaderAsync(token))
                    {
                        await reader.ReadAsync(token);
                        batchRefreshed = reader.GetInt64(0);
                        batchProcessed = reader.GetInt64(1);
                    }
                    await refreshTransaction.CommitAsync(token);
                    refreshed += checked((int)batchRefreshed);
                    // Locked/deleted endpoints leave this staging batch as well: they
                    // retry next feed cycle without starving later unlocked candidates.
                    if (batchProcessed < refreshBatchSize) break;
                }
                catch (PostgresException exception) when (
                    !token.IsCancellationRequested && refreshBatchSize > 1 &&
                    exception.SqlState == PostgresErrorCodes.QueryCanceled &&
                    exception.MessageText.Contains("statement timeout", StringComparison.OrdinalIgnoreCase))
                {
                    // The failed transaction rolled back both registry and staging.
                    // Retry its candidates in a smaller transaction on slow VPS disks.
                    refreshBatchSize = Math.Max(1, refreshBatchSize / 2);
                }
            }
            refreshMs = (long)Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds;
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    await using var drop = new NpgsqlCommand("DROP TABLE IF EXISTS proxy_import", connection);
                    await drop.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    OperationalLogBoundary.Write(() => ImportCleanupFailed(logger, cleanupException));
                }
            }
        }
        OperationalLogBoundary.Write(() => BulkUpsertCompleted(
            logger, candidateCount, copyMs, insertMs, refreshMs, added, refreshed, null));
        return added;
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

    private sealed record SourceImportProgress(
        ProxySourceImportCheckpoint State, int NextIndex, byte[]? FreshBodyHash, bool PreferFresh);

    private sealed record SourceCollectionResult(
        Guid Id,
        string SourceUrl,
        ProxyProtocol SourceProtocol,
        int Count,
        bool Truncated,
        string? HttpETag,
        DateTimeOffset? HttpLastModifiedAt,
        bool ContentFetched,
        string? Error,
        string? CredentialStatus = null,
        DateTimeOffset? CredentialCheckedAt = null,
        DateTimeOffset? CredentialExpiresAt = null,
        string? CredentialError = null,
        bool FetchObserved = true,
        DateTimeOffset? RetryNotBefore = null);

    private sealed class PaidSourceException(
        string status,
        string message,
        Exception? innerException = null) : Exception(message, innerException)
    {
        internal string Status { get; } = status;
    }
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
        return ParseBoundedRequired(content, defaultProtocol, maxResults).Items;
    }

    /// <summary>Проверяет непустой feed и сохраняет точный сигнал индивидуального усечения.</summary>
    internal static ProxyParseResult ParseBoundedRequired(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults)
    {
        var items = new List<(string Host, int Port, ProxyProtocol Protocol)>(Math.Min(maxResults, 4_096));
        var parsed = ParseBoundedToRequired(content, defaultProtocol, maxResults, candidate => items.Add(candidate.ToEndpoint()));
        return new ProxyParseResult(items, parsed.Truncated);
    }

    /// <summary>Collector-path без materialized списка строк для каждого параллельного feed'а.</summary>
    internal static ProxyParseSummary ParseBoundedToRequired(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults,
        Action<ProxyCandidateKey> accept)
    {
        EnsureNotHtmlEnvelope(content);
        var parsed = JsonProxyFeedParser.TryParseTo(content, defaultProtocol, maxResults, accept)
            ?? ProxyParser.ParseTo(content, defaultProtocol, maxResults, accept);
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
    /// <summary>Соблюдает документированную нижнюю границу polling нового публичного провайдера.</summary>
    internal static DateTimeOffset? NextSuccessAttempt(string url, DateTimeOffset fetchedAt)
    {
        // Public search documents per-IP/record quotas without numeric caps.
        // Keep successful full refreshes conservative; cached imports continue.
        if (ProxiwarePublicApi.Supports(url)) return fetchedAt.AddMinutes(10);
        if (RoundProxiesPublicApi.Supports(url)) return fetchedAt.AddMinutes(5);
        if (Socks5ProxiesPublicApi.Supports(url)) return fetchedAt.AddMinutes(30);
        if (FreeProxyDbPageCapture.Supports(url)) return fetchedAt.AddHours(6);
        if (MyProxyHtmlFeedAdapter.Supports(url)) return fetchedAt.AddHours(1);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/litportnet/free-proxy-list/", StringComparison.OrdinalIgnoreCase))
            return fetchedAt.AddMinutes(5);
        return null;
    }

    internal static bool IsDue(DateTimeOffset? nextFetchAt, DateTimeOffset now, bool forceAllSources, string? url = null) =>
        (forceAllSources && (url is null || !FreeProxyDbPageCapture.Supports(url))) ||
        nextFetchAt is null || nextFetchAt <= now;

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
