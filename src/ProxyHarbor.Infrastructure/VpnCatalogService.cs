using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Собирает VPN endpoint, публичные URI подключения и проверяет доступность TCP endpoint.</summary>
public sealed class VpnCatalogService(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<CollectorOptions> options,
    ILogger<VpnCatalogService> logger)
{
    // Один из проверенных встроенных VLESS-feed сейчас имеет размер около 25 MiB.
    // Лимит 32 MiB оставляет небольшой запас, но по-прежнему жёстко ограничивает
    // память при загрузке недоверенного внешнего содержимого.
    private const int MaximumFeedBytes = 32 * 1024 * 1024;
    // Один источник не должен монополизировать память и весь каталог. Даже при общем
    // proxy-лимите 500k для VPN сохраняем до 10k уникальных endpoint с каждого feed;
    // источники обновляются часто, поэтому выборка остаётся широкой и актуальной.
    private const int MaximumCandidatesPerVpnSource = 10_000;
    private static readonly Action<ILogger, Guid, Exception?> SourceFailed =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(1161, "VpnSourceFailed"), "VPN source {SourceId} failed");

    /// <summary>Загружает все включённые feed и сохраняет endpoint вместе с опубликованными URI.</summary>
    public async Task<VpnCollectionResult> CollectAsync(
        bool forceAllSources = false, CancellationToken token = default)
    {
        await using var operationLock = await PostgresAdvisoryLock.TryAcquireAsync(dbFactory, PostgresAdvisoryLock.VpnCollectionKey, token);
        if (operationLock is null) throw new OperationAlreadyRunningException("VPN collection уже выполняется другой репликой.");
        await using var readDb = await dbFactory.CreateDbContextAsync(token);
        var collectionStartedAt = DateTimeOffset.UtcNow;
        var sources = await readDb.VpnSources.AsNoTracking().Where(x => x.Enabled &&
                (forceAllSources || x.NextFetchAt == null || x.NextFetchAt <= collectionStartedAt))
            .OrderBy(x => x.Priority).ToArrayAsync(token);
        if (sources.Length == 0)
            return new VpnCollectionResult(0, 0, 0, 0, 0, 0);
        var results = await ParallelFetchAsync(sources, forceAllSources, collectionStartedAt, token);
        // Production использует NpgsqlRetryingExecutionStrategy. Вся транзакция должна
        // находиться внутри execution scope, а каждая retry-попытка — получать свежий
        // DbContext: после rollback уже сохранённый tracker иначе считал бы source health
        // неизменённым и не повторил UPDATE.
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            () => PersistCollectionAsync(results, sources.Length, collectionStartedAt, token));
    }

    private async Task<VpnCollectionResult> PersistCollectionAsync(
        FetchResult[] results,
        int sourceCount,
        DateTimeOffset collectionStartedAt,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await PostgresAdvisoryLock.AcquireTransactionAsync(
            (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)transaction.GetDbTransaction(),
            PostgresAdvisoryLock.VpnMutationKey,
            token);
        var resultSourceIds = results.Select(result => result.Source.Id).ToArray();
        // В память попадают только источники текущего запуска. Каталог endpoint и provenance
        // может расти без ограничения, поэтому его нельзя материализовать и track'ать целиком.
        var trackedSources = await db.VpnSources.Where(source => resultSourceIds.Contains(source.Id))
            .ToDictionaryAsync(x => x.Id, token);
        var now = DateTimeOffset.UtcNow;
        var acceptedResults = new List<FetchResult>(results.Length);
        var succeededResults = new List<FetchResult>(results.Length);
        foreach (var result in results)
        {
            if (!trackedSources.TryGetValue(result.Source.Id, out var source)) continue;
            // Результат HTTP-запроса относится к снимку конфигурации до загрузки. Если
            // администратор изменил URL или протокол в полёте, старый body нельзя записывать
            // от имени новой настройки.
            if (!source.Enabled ||
                !string.Equals(source.Url, result.Source.Url, StringComparison.Ordinal) ||
                source.DefaultProtocol != result.Source.DefaultProtocol)
                continue;
            source.LastFetchedAt = now;
            if (result.Error is not null)
            {
                source.ConsecutiveFailures++;
                source.LastError = result.Error[..Math.Min(result.Error.Length, 500)];
                source.NextFetchAt = SourceFetchSchedule.NextAttempt(
                    collectionStartedAt,
                    source.ConsecutiveFailures,
                    options.Value.SourceFailureBackoffBaseMinutes,
                    options.Value.SourceFailureBackoffMaxHours);
                continue;
            }
            source.LastSucceededAt = now;
            source.LastItemCount = result.ConfirmedCandidateCount;
            source.HttpETag = result.HttpETag;
            source.HttpLastModifiedAt = result.HttpLastModifiedAt;
            if (result.ContentFetched) source.LastContentFetchedAt = now;
            source.ConsecutiveFailures = 0;
            source.LastError = null;
            source.NextFetchAt = null;
            succeededResults.Add(result);
            // Для выбора preferred URI используем актуальный priority из БД, а не снимок,
            // с которым HTTP-запрос стартовал.
            if (result.ContentFetched) acceptedResults.Add(result with { Source = source });
        }

        // Source health и импорт составляют одну транзакцию: успешный источник не должен
        // отображаться обновлённым, если COPY/upsert каталога не завершился.
        await db.SaveChangesAsync(token);
        var added = await BulkUpsertAsync(
            db,
            acceptedResults,
            now,
            options.Value.LastSeenRefreshMinutes,
            token);
        await transaction.CommitAsync(token);
        var succeeded = succeededResults.Count;
        var contentFetched = succeededResults.Count(result => result.ContentFetched);
        return new(
            sourceCount,
            succeeded,
            succeededResults.Sum(result => result.ConfirmedCandidateCount),
            added,
            contentFetched,
            succeeded - contentFetched);
    }

    /// <summary>
    /// Потоково загружает результаты текущих feed во временную таблицу, строит одну
    /// дедуплицированную проекцию и выполняет три set-based изменения. Стоимость памяти
    /// приложения зависит от текущей партии, а не от всего VPN-каталога и provenance.
    /// </summary>
    private static async Task<int> BulkUpsertAsync(
        ProxyHarborDbContext db,
        IReadOnlyCollection<FetchResult> results,
        DateTimeOffset now,
        int lastSeenRefreshMinutes,
        CancellationToken token)
    {
        if (results.Count == 0) return 0;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(token);
        var transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("VPN bulk import требует активную транзакцию.");

        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE vpn_import (
                source_id uuid NOT NULL,
                source_priority integer NOT NULL,
                host text NOT NULL,
                port integer NOT NULL,
                protocol integer NOT NULL,
                transport text NOT NULL,
                connection_uri text NULL,
                seen_at timestamptz NOT NULL,
                PRIMARY KEY (source_id, host, port, protocol, transport)
            ) ON COMMIT DROP
            """, connection, transaction))
            await create.ExecuteNonQueryAsync(token);

        await using (var writer = await connection.BeginBinaryImportAsync("""
            COPY vpn_import
                (source_id, source_priority, host, port, protocol, transport, connection_uri, seen_at)
            FROM STDIN (FORMAT BINARY)
            """, token))
        {
            foreach (var result in results)
            {
                foreach (var candidate in result.Candidates)
                {
                    await writer.StartRowAsync(token);
                    await writer.WriteAsync(result.Source.Id, NpgsqlDbType.Uuid, token);
                    await writer.WriteAsync(result.Source.Priority, NpgsqlDbType.Integer, token);
                    await writer.WriteAsync(candidate.Host, NpgsqlDbType.Text, token);
                    await writer.WriteAsync(candidate.Port, NpgsqlDbType.Integer, token);
                    await writer.WriteAsync((int)candidate.Protocol, NpgsqlDbType.Integer, token);
                    await writer.WriteAsync(candidate.Transport, NpgsqlDbType.Text, token);
                    if (candidate.ConnectionUri is null)
                        await writer.WriteNullAsync(token);
                    else
                        await writer.WriteAsync(candidate.ConnectionUri, NpgsqlDbType.Text, token);
                    await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, token);
                }
            }
            await writer.CompleteAsync(token);
        }

        // DISTINCT ON выполняется один раз для INSERT и UPDATE. Без этого обе операции
        // независимо сортировали всю партию, что удваивало temp I/O на крупных feed.
        // Полноценная URI приоритетнее метаданных, затем действует priority администратора.
        await using (var prepareEndpoints = new NpgsqlCommand("""
            CREATE TEMP TABLE vpn_import_endpoints ON COMMIT DROP AS
            SELECT DISTINCT ON (host, port, protocol, transport)
                   source_id, host, port, protocol, transport, connection_uri, seen_at
            FROM vpn_import
            ORDER BY host, port, protocol, transport,
                     (connection_uri IS NULL), source_priority, source_id;
            ANALYZE vpn_import;
            ANALYZE vpn_import_endpoints
            """, connection, transaction))
            await prepareEndpoints.ExecuteNonQueryAsync(token);

        await using var insert = new NpgsqlCommand("""
            INSERT INTO "VpnEndpoints"
                ("Id", "Host", "Port", "Protocol", "Transport", "CountryCode", "ConnectionUri",
                 "Status", "LatencyMs", "FirstSeenAt", "LastSeenAt", "LastCheckedAt", "NextCheckAt",
                 "SuccessfulChecks", "FailedChecks", "LastError", "FirstSourceId")
            SELECT gen_random_uuid(), i.host, i.port, i.protocol, i.transport, NULL, i.connection_uri,
                   0, NULL, i.seen_at, i.seen_at, NULL, NULL, 0, 0, NULL, i.source_id
            FROM vpn_import_endpoints i
            ON CONFLICT ("Host", "Port", "Protocol", "Transport") DO NOTHING
            """, connection, transaction);
        var added = await insert.ExecuteNonQueryAsync(token);

        var refreshBefore = now.AddMinutes(-Math.Max(1, lastSeenRefreshMinutes));
        // Не создаём новую MVCC-версию каждые пять минут. ConnectionUri обновляется
        // немедленно при изменении, LastSeenAt — с настроенной bounded-точностью.
        await using (var refreshEndpoints = new NpgsqlCommand("""
            UPDATE "VpnEndpoints" endpoint
            SET "LastSeenAt" = preferred.seen_at,
                "ConnectionUri" = COALESCE(preferred.connection_uri, endpoint."ConnectionUri")
            FROM vpn_import_endpoints preferred
            WHERE endpoint."Host" = preferred.host
              AND endpoint."Port" = preferred.port
              AND endpoint."Protocol" = preferred.protocol
              AND endpoint."Transport" = preferred.transport
              AND (endpoint."LastSeenAt" < @refresh_before OR
                   (preferred.connection_uri IS NOT NULL AND
                    endpoint."ConnectionUri" IS DISTINCT FROM preferred.connection_uri))
            """, connection, transaction))
        {
            refreshEndpoints.Parameters.AddWithValue(
                "refresh_before", NpgsqlDbType.TimestampTz, refreshBefore);
            await refreshEndpoints.ExecuteNonQueryAsync(token);
        }

        await using (var upsertProvenance = new NpgsqlCommand("""
            INSERT INTO "VpnEndpointSources" ("VpnEndpointId", "VpnSourceId", "LastSeenAt")
            SELECT endpoint."Id", import.source_id, import.seen_at
            FROM vpn_import import
            JOIN "VpnEndpoints" endpoint
              ON endpoint."Host" = import.host
             AND endpoint."Port" = import.port
             AND endpoint."Protocol" = import.protocol
             AND endpoint."Transport" = import.transport
            ON CONFLICT ("VpnEndpointId", "VpnSourceId") DO UPDATE
            SET "LastSeenAt" = EXCLUDED."LastSeenAt"
            WHERE "VpnEndpointSources"."LastSeenAt" < @refresh_before
            """, connection, transaction))
        {
            upsertProvenance.Parameters.AddWithValue(
                "refresh_before", NpgsqlDbType.TimestampTz, refreshBefore);
            await upsertProvenance.ExecuteNonQueryAsync(token);
        }
        return added;
    }

    /// <summary>Проверяет TCP-доступность очередной bounded-партии без использования credentials.</summary>
    public async Task<VpnValidationResult> ValidateAsync(CancellationToken token = default)
    {
        await using var operationLock = await PostgresAdvisoryLock.TryAcquireAsync(dbFactory, PostgresAdvisoryLock.VpnValidationKey, token);
        if (operationLock is null) throw new OperationAlreadyRunningException("VPN validation уже выполняется другой репликой.");
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var now = DateTimeOffset.UtcNow;
        // Проверяем detached snapshot: результат сохраняется одним set-based UPDATE,
        // поэтому tracking тысяч entity только раздувал память и DetectChanges CPU.
        var endpoints = await VpnValidationQueue.SelectAsync(
            db,
            Math.Clamp(options.Value.VpnValidationBatchSize, 1, 20_000),
            now,
            token);
        var results = new VpnProbeResult[endpoints.Length];
        var concurrency = Math.Clamp(options.Value.VpnValidationConcurrency, 1, 1_000);
        using var tcpProbe = new VpnTcpProbe(concurrency);
        await Parallel.ForAsync(0, endpoints.Length, new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = token
        }, async (index, cancellationToken) =>
        {
            var endpoint = endpoints[index];
            if (endpoint.Transport == "udp")
            {
                results[index] = new(
                    endpoint.Id,
                    null,
                    null,
                    "UDP требует протокольной проверки; credentials не используются");
                return;
            }
            try
            {
                var latency = await tcpProbe.ProbeAsync(endpoint.Host, endpoint.Port, options.Value.ProbeTimeoutSeconds, cancellationToken);
                results[index] = new(endpoint.Id, true, latency, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (VpnProbeDeferredException exception)
            {
                results[index] = new(endpoint.Id, null, null, exception.Message, IsDeferred: true);
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
            {
                results[index] = new(
                    endpoint.Id,
                    false,
                    null,
                    exception is OperationCanceledException ? "timeout" : exception.GetType().Name);
            }
        });

        var checkedAt = DateTimeOffset.UtcNow;
        var updates = results.Select(result =>
            ToValidationUpdate(
                result,
                checkedAt,
                options.Value.VpnReachableValidationIntervalMinutes,
                options.Value.VpnUnreachableRetryMinutes,
                options.Value.VpnUnsupportedRetryMinutes)).ToArray();
        var persisted = await PersistValidationResultsAsync(updates, token);
        EnsureCompletePersistence(persisted, updates.Length);
        return new(
            persisted - updates.Count(x => x.IsDeferred),
            updates.Count(x => !x.IsDeferred && x.Status == VpnEndpointStatus.Reachable),
            updates.Count(x => !x.IsDeferred && x.Status == VpnEndpointStatus.UnsupportedTransport),
            updates.Count(x => x.IsDeferred));
    }

    /// <summary>Нормализует probe outcome и единообразно назначает следующую проверку.</summary>
    internal static VpnValidationUpdate ToValidationUpdate(
        VpnProbeResult result,
        DateTimeOffset checkedAt,
        int reachableIntervalMinutes,
        int unreachableRetryMinutes,
        int unsupportedRetryMinutes)
    {
        if (result.IsDeferred)
            return new(result.Id, VpnEndpointStatus.Pending, null, result.Error,
                checkedAt, checkedAt.AddMinutes(1), IsDeferred: true);
        var status = result.Reachable switch
        {
            true => VpnEndpointStatus.Reachable,
            false => VpnEndpointStatus.Unreachable,
            null => VpnEndpointStatus.UnsupportedTransport
        };
        var interval = result.Reachable switch
        {
            true => Math.Max(1, reachableIntervalMinutes),
            false => Math.Max(1, unreachableRetryMinutes),
            null => Math.Max(1, unsupportedRetryMinutes)
        };
        return new(result.Id, status, result.Latency, result.Error, checkedAt, checkedAt.AddMinutes(interval));
    }

    /// <summary>Не позволяет молча потерять результат при неожиданном изменении каталога.</summary>
    internal static void EnsureCompletePersistence(int persisted, int expected)
    {
        if (persisted != expected)
            throw new InvalidOperationException(
                $"VPN validation сохранила {persisted} из {expected} результатов.");
    }

    /// <summary>
    /// Передаёт bounded-партию через binary COPY и изменяет каталог одним UPDATE.
    /// Collection может параллельно обновлять LastSeenAt/URI: запрос намеренно касается
    /// только validation-полей и не перезаписывает свежие данные источников.
    /// Возвращает число подтверждённых строк, включая уже записанные/устаревшие результаты.
    /// Одинаковая или более старая попытка не создаёт новую версию строки и не меняет счётчики.
    /// </summary>
    internal Task<int> PersistValidationResultsAsync(
        VpnValidationUpdate[] updates,
        CancellationToken token = default) => PersistValidationResultsAsync(updates, afterCommit: null, token);

    /// <summary>Внутренний overload с fault-injection после настоящего COMMIT для lost-ack тестов.</summary>
    internal async Task<int> PersistValidationResultsAsync(
        VpnValidationUpdate[] updates,
        Func<Task>? afterCommit,
        CancellationToken token = default)
    {
        if (updates.Length == 0) return 0;
        await using var strategyDb = await dbFactory.CreateDbContextAsync(token);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var writeDb = await dbFactory.CreateDbContextAsync(token);
            var connection = (NpgsqlConnection)writeDb.Database.GetDbConnection();
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            // Collection и validation меняют пересекающиеся VPN-строки разными
            // set-based запросами. Короткий transaction lock сохраняет параллельный
            // HTTP/probe этап, но исключает взаимную блокировку write-фазы.
            await PostgresAdvisoryLock.AcquireTransactionAsync(
                connection,
                transaction,
                PostgresAdvisoryLock.VpnMutationKey,
                token);
            await using (var create = new NpgsqlCommand("""
                CREATE TEMP TABLE vpn_check_update (
                    id uuid PRIMARY KEY,
                    status integer NOT NULL,
                    latency_ms integer NULL,
                    error text NULL,
                    checked_at timestamptz NOT NULL,
                    next_check_at timestamptz NOT NULL,
                    is_deferred boolean NOT NULL
                ) ON COMMIT DROP
                """, connection, transaction))
                await create.ExecuteNonQueryAsync(token);

            await using (var writer = await connection.BeginBinaryImportAsync("""
                COPY vpn_check_update
                    (id, status, latency_ms, error, checked_at, next_check_at, is_deferred)
                FROM STDIN (FORMAT BINARY)
                """, token))
            {
                foreach (var update in updates)
                {
                    await writer.StartRowAsync(token);
                    await writer.WriteAsync(update.Id, NpgsqlDbType.Uuid, token);
                    await writer.WriteAsync((int)update.Status, NpgsqlDbType.Integer, token);
                    if (update.LatencyMs is null) await writer.WriteNullAsync(token);
                    else await writer.WriteAsync(update.LatencyMs.Value, NpgsqlDbType.Integer, token);
                    if (update.Error is null) await writer.WriteNullAsync(token);
                    else await writer.WriteAsync(
                        update.Error[..Math.Min(500, update.Error.Length)],
                        NpgsqlDbType.Text,
                        token);
                    await writer.WriteAsync(update.CheckedAt, NpgsqlDbType.TimestampTz, token);
                    await writer.WriteAsync(update.NextCheckAt, NpgsqlDbType.TimestampTz, token);
                    await writer.WriteAsync(update.IsDeferred, NpgsqlDbType.Boolean, token);
                }
                await writer.CompleteAsync(token);
            }

            await using var updateCommand = new NpgsqlCommand("""
                UPDATE "VpnEndpoints" endpoint
                SET "Status" = CASE WHEN result.is_deferred THEN endpoint."Status" ELSE result.status END,
                    "LatencyMs" = CASE WHEN result.is_deferred THEN endpoint."LatencyMs" ELSE result.latency_ms END,
                    "LastError" = result.error,
                    "LastCheckedAt" = CASE WHEN result.is_deferred THEN endpoint."LastCheckedAt" ELSE result.checked_at END,
                    "LastValidationAttemptAt" = result.checked_at,
                    "LastValidationDeferred" = result.is_deferred,
                    "NextCheckAt" = result.next_check_at,
                    "SuccessfulChecks" = LEAST(
                        endpoint."SuccessfulChecks"::bigint + (NOT result.is_deferred AND result.status = 1)::integer,
                        2147483647)::integer,
                    "FailedChecks" = LEAST(
                        endpoint."FailedChecks"::bigint + (NOT result.is_deferred AND result.status = 2)::integer,
                        2147483647)::integer
                FROM vpn_check_update result
                WHERE endpoint."Id" = result.id
                  AND (GREATEST(endpoint."LastValidationAttemptAt", endpoint."LastCheckedAt") IS NULL
                    OR GREATEST(endpoint."LastValidationAttemptAt", endpoint."LastCheckedAt") < result.checked_at)
                """, connection, transaction);
            var persisted = await updateCommand.ExecuteNonQueryAsync(token);
            if (persisted != updates.Length)
            {
                // Обычная новая партия не требует дополнительного чтения каталога.
                // При replay/устаревшей попытке подтверждаем и неизменённые строки.
                await using var countCommand = new NpgsqlCommand("""
                    SELECT count(*)::integer
                    FROM "VpnEndpoints" endpoint
                    JOIN vpn_check_update result ON endpoint."Id" = result.id
                    """, connection, transaction);
                persisted = (int)(await countCommand.ExecuteScalarAsync(token))!;
            }
            // Проверяем ДО COMMIT: отсутствие одного endpoint откатывает всю партию.
            EnsureCompletePersistence(persisted, updates.Length);
            await transaction.CommitAsync(token);
            // Test-only lost-ack hook: моделирует transient failure после реального COMMIT.
            if (afterCommit is not null) await afterCommit();
            return persisted;
        });
    }

    private async Task<FetchResult[]> ParallelFetchAsync(
        VpnSource[] sources,
        bool forceAllSources,
        DateTimeOffset collectionStartedAt,
        CancellationToken token)
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<FetchResult>();
        // HttpClient потокобезопасен, а factory уже управляет сроком жизни handler'а.
        // Один экземпляр на цикл исключает сотни короткоживущих wrapper-объектов.
        var client = httpClientFactory.CreateClient("sources");
        await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = options.Value.SourceConcurrency, CancellationToken = token },
            async (source, cancellationToken) =>
            {
                try
                {
                    var useValidators = !forceAllSources && SourceConditionalFetchPolicy.ShouldUseValidators(
                        source.LastContentFetchedAt,
                        source.LastSucceededAt,
                        source.LastItemCount,
                        collectionStartedAt,
                        options.Value.DeadRetentionDays);
                    var fetched = await SourceHttpFetcher.FetchAsync(
                        client,
                        source.Url,
                        useValidators ? source.HttpETag : null,
                        useValidators ? source.HttpLastModifiedAt : null,
                        MaximumFeedBytes,
                        options.Value.SourceTimeoutSeconds,
                        options.Value.SourceRetryCount,
                        cancellationToken,
                        SourceFeedParser.EnsureSupportedMediaType);
                    if (fetched.NotModified)
                    {
                        if (source.LastSucceededAt is null || source.LastContentFetchedAt is null || source.LastItemCount <= 0)
                            throw new InvalidDataException("VPN feed вернул 304 без подтверждённого полного снимка.");
                        results.Add(new(
                            source,
                            [],
                            source.LastItemCount,
                            ContentFetched: false,
                            fetched.HttpETag,
                            fetched.HttpLastModifiedAt,
                            Error: null));
                        return;
                    }

                    var maximumCandidates = Math.Min(options.Value.MaxProxiesPerSource, MaximumCandidatesPerVpnSource);
                    var candidates = VpnFeedParser.Parse(
                        fetched.Content ?? throw new InvalidDataException("VPN feed не вернул тело."),
                        source.DefaultProtocol,
                        maximumCandidates);
                    results.Add(new(
                        source,
                        candidates,
                        candidates.Count,
                        ContentFetched: true,
                        fetched.HttpETag,
                        fetched.HttpLastModifiedAt,
                        Error: null));
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    OperationalLogBoundary.Write(() => SourceFailed(logger, source.Id, exception));
                    results.Add(new(source, [], 0, false, null, null, exception.Message));
                }
            });
        return results.ToArray();
    }

    private sealed record FetchResult(
        VpnSource Source,
        IReadOnlyList<VpnCandidate> Candidates,
        int ConfirmedCandidateCount,
        bool ContentFetched,
        string? HttpETag,
        DateTimeOffset? HttpLastModifiedAt,
        string? Error);
}

/// <summary>Сводка завершённого VPN-сбора.</summary>
public sealed record VpnCollectionResult
{
    /// <summary>Создаёт сводку сбора.</summary>
    public VpnCollectionResult(
        int sources,
        int succeeded,
        int candidates,
        int added,
        int contentFetched,
        int notModified) =>
        (Sources, Succeeded, Candidates, Added, ContentFetched, NotModified) =
        (sources, succeeded, candidates, added, contentFetched, notModified);
    /// <summary>Обработано источников.</summary>
    public int Sources { get; }
    /// <summary>Успешных источников.</summary>
    public int Succeeded { get; }
    /// <summary>Найдено кандидатов.</summary>
    public int Candidates { get; }
    /// <summary>Добавлено новых endpoint.</summary>
    public int Added { get; }
    /// <summary>Источников, вернувших и заново разобравших полное тело.</summary>
    public int ContentFetched { get; }
    /// <summary>Источников, подтверждённых HTTP 304 без импорта каталога.</summary>
    public int NotModified { get; }
}

/// <summary>Сводка проверки VPN endpoint.</summary>
public sealed record VpnValidationResult
{
    /// <summary>Создаёт сводку проверки.</summary>
    public VpnValidationResult(int checkedCount, int reachable, int unsupportedTransport, int deferred = 0) =>
        (Checked, Reachable, UnsupportedTransport, Deferred) = (checkedCount, reachable, unsupportedTransport, deferred);
    /// <summary>Всего обработано.</summary>
    public int Checked { get; }
    /// <summary>Доступных TCP endpoint.</summary>
    public int Reachable { get; }
    /// <summary>UDP endpoint без небезопасной протокольной проверки.</summary>
    public int UnsupportedTransport { get; }
    /// <summary>Нейтральные попытки, не изменившие последнюю оценку состояния endpoint.</summary>
    public int Deferred { get; }
}

/// <summary>Нормализованный результат одной VPN-проверки для set-based persistence.</summary>
internal sealed record VpnValidationUpdate(
    Guid Id,
    VpnEndpointStatus Status,
    int? LatencyMs,
    string? Error,
    DateTimeOffset CheckedAt,
    DateTimeOffset NextCheckAt,
    bool IsDeferred = false);

/// <summary>Результат сетевого probe до нормализации статуса и расписания.</summary>
internal readonly record struct VpnProbeResult(Guid Id, bool? Reachable, int? Latency, string? Error, bool IsDeferred = false);

/// <summary>Запускает сбор VPN feed независимо от более частой проверки endpoint.</summary>
public sealed class VpnCollectorWorker(VpnCatalogService service, IOptions<CollectorOptions> options, ILogger<VpnCollectorWorker> logger) : BackgroundService
{
    internal enum CycleOutcome { Succeeded, PeerOwned, Failed }
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OverrunCooldown = TimeSpan.FromSeconds(30);
    private static readonly Action<ILogger, int, int, string, int, int, double, Exception?> CycleCompleted =
        LoggerMessage.Define<int, int, string, int, int, double>(
            LogLevel.Information,
            new EventId(1160, "VpnCycleCompleted"),
            "VPN catalog cycle завершён: источников {SourceCount}, успешно {SucceededCount}, " +
            "HTTP {HttpOutcome}, кандидатов {CandidateCount}, " +
            "добавлено {AddedCount}, время {ElapsedMilliseconds:F0} мс");
    private static readonly Action<ILogger, Exception?> CycleFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1162, "VpnCycleFailed"), "VPN catalog cycle failed");
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundWorkersEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = Stopwatch.GetTimestamp();
            var outcome = CycleOutcome.Failed;
            try
            {
                var result = await service.CollectAsync(token: stoppingToken);
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                CycleCompleted(
                    logger,
                    result.Sources,
                    result.Succeeded,
                    $"body={result.ContentFetched}, not-modified={result.NotModified}",
                    result.Candidates,
                    result.Added,
                    elapsed.TotalMilliseconds,
                    null);
                outcome = CycleOutcome.Succeeded;
            }
            catch (OperationAlreadyRunningException)
            {
                // Ручной запуск или другая API-реплика уже владеет advisory lock.
                // Это штатная координация, а не ошибка production-цикла.
                outcome = CycleOutcome.PeerOwned;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                OperationalLogBoundary.Write(() => CycleFailed(logger, exception));
            }
            var elapsedAfterCycle = Stopwatch.GetElapsedTime(startedAt);
            await Task.Delay(
                NextDelay(options.Value.CollectionIntervalMinutes, outcome, elapsedAfterCycle),
                stoppingToken);
        }
    }

    /// <summary>
    /// Поддерживает start-to-start cadence успешного сбора, не создаёт lock storm при
    /// другой реплике и быстрее восстанавливается после настоящего transient-сбоя.
    /// </summary>
    internal static TimeSpan NextDelay(int intervalMinutes, CycleOutcome outcome, TimeSpan elapsed)
    {
        var regularDelay = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        return outcome switch
        {
            CycleOutcome.Succeeded when elapsed < regularDelay => regularDelay - elapsed,
            CycleOutcome.Succeeded => OverrunCooldown,
            CycleOutcome.PeerOwned => regularDelay,
            CycleOutcome.Failed => regularDelay <= FailureRetryDelay ? regularDelay : FailureRetryDelay,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }
}

/// <summary>Проверяет очередную VPN-партию с интервалом валидатора, не ожидая следующего сбора.</summary>
public sealed class VpnValidatorWorker(VpnCatalogService service, IOptions<CollectorOptions> options, ILogger<VpnValidatorWorker> logger) : BackgroundService
{
    private static readonly TimeSpan QueueDrainDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(15);
    private static readonly Action<ILogger, Exception?> CycleFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1163, "VpnValidationCycleFailed"), "VPN validation cycle failed");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundWorkersEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = FailureDelay;
            try
            {
                var result = await service.ValidateAsync(stoppingToken);
                delay = NextDelay(result.Checked + result.Deferred);
            }
            catch (OperationAlreadyRunningException)
            {
                // Другая реплика уже обрабатывает очередь. Короткая пауза не даёт
                // простаивать после освобождения advisory lock и не создаёт spin-loop.
                delay = QueueDrainDelay;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                OperationalLogBoundary.Write(() => CycleFailed(logger, exception));
            }
            await Task.Delay(delay, stoppingToken);
        }
    }

    /// <summary>
    /// NextCheckAt задаёт частоту повторной проверки отдельного endpoint, поэтому worker
    /// должен быстро выгребать непустые bounded-партии и замедляться только на пустой очереди.
    /// </summary>
    internal static TimeSpan NextDelay(int checkedCount) => checkedCount > 0 ? QueueDrainDelay : IdleDelay;
}
