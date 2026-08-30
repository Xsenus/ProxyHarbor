using System.Diagnostics;
using System.Net;
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
            return new VpnCollectionResult(0, 0, 0, 0);
        var results = await ParallelFetchAsync(sources, token);
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
        var resultSourceIds = results.Select(result => result.Source.Id).ToArray();
        // В память попадают только источники текущего запуска. Каталог endpoint и provenance
        // может расти без ограничения, поэтому его нельзя материализовать и track'ать целиком.
        var trackedSources = await db.VpnSources.Where(source => resultSourceIds.Contains(source.Id))
            .ToDictionaryAsync(x => x.Id, token);
        var now = DateTimeOffset.UtcNow;
        var acceptedResults = new List<FetchResult>(results.Length);
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
            source.LastItemCount = result.Candidates.Count;
            source.ConsecutiveFailures = 0;
            source.LastError = null;
            source.NextFetchAt = null;
            // Для выбора preferred URI используем актуальный priority из БД, а не снимок,
            // с которым HTTP-запрос стартовал.
            acceptedResults.Add(result with { Source = source });
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
        return new(sourceCount, acceptedResults.Count, acceptedResults.Sum(x => x.Candidates.Count), added);
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
        var endpoints = await db.VpnEndpoints.Where(x => x.NextCheckAt == null || x.NextCheckAt <= now)
            .OrderBy(x => x.NextCheckAt).ThenBy(x => x.LastCheckedAt)
            .Take(Math.Min(options.Value.ValidationBatchSize, 5000)).ToArrayAsync(token);
        var results = new System.Collections.Concurrent.ConcurrentBag<(Guid Id, bool? Reachable, int? Latency, string? Error)>();
        await Parallel.ForEachAsync(endpoints, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(options.Value.ValidationConcurrency, 250),
            CancellationToken = token
        }, async (endpoint, cancellationToken) =>
        {
            if (endpoint.Transport == "udp")
            {
                results.Add((endpoint.Id, null, null, "UDP требует протокольной проверки; credentials не используются"));
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await ConnectPublicAsync(endpoint.Host, endpoint.Port, options.Value.ProbeTimeoutSeconds, cancellationToken);
                results.Add((endpoint.Id, true, (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue), null));
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
            {
                results.Add((endpoint.Id, false, null, exception is OperationCanceledException ? "timeout" : exception.GetType().Name));
            }
        });

        var endpointsById = endpoints.ToDictionary(x => x.Id);
        foreach (var result in results)
        {
            var endpoint = endpointsById[result.Id];
            endpoint.LastCheckedAt = now;
            endpoint.NextCheckAt = now.AddMinutes(result.Reachable == false ? 15 : options.Value.ValidationIntervalMinutes);
            endpoint.LatencyMs = result.Latency;
            endpoint.LastError = result.Error;
            if (result.Reachable is null) endpoint.Status = VpnEndpointStatus.UnsupportedTransport;
            else if (result.Reachable.Value) { endpoint.Status = VpnEndpointStatus.Reachable; endpoint.SuccessfulChecks++; }
            else { endpoint.Status = VpnEndpointStatus.Unreachable; endpoint.FailedChecks++; }
        }
        await db.SaveChangesAsync(token);
        return new(endpoints.Length, results.Count(x => x.Reachable == true), results.Count(x => x.Reachable is null));
    }

    private async Task<FetchResult[]> ParallelFetchAsync(VpnSource[] sources, CancellationToken token)
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<FetchResult>();
        await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = options.Value.SourceConcurrency, CancellationToken = token },
            async (source, cancellationToken) =>
            {
                try
                {
                    if (!await NetworkSafety.IsSafePublicHttpsUrlAsync(source.Url, cancellationToken))
                        throw new HttpRequestException("URL источника не прошёл public HTTPS проверку");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.SourceTimeoutSeconds));
                    using var response = await httpClientFactory.CreateClient("sources").GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaximumFeedBytes) throw new HttpRequestException("VPN feed превышает 32 MiB");
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var memory = new MemoryStream();
                    await CopyBoundedAsync(stream, memory, timeout.Token);
                    var content = System.Text.Encoding.UTF8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
                    var maximumCandidates = Math.Min(options.Value.MaxProxiesPerSource, MaximumCandidatesPerVpnSource);
                    results.Add(new(source, VpnFeedParser.Parse(content, source.DefaultProtocol, maximumCandidates), null));
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    OperationalLogBoundary.Write(() => SourceFailed(logger, source.Id, exception));
                    results.Add(new(source, [], exception.Message));
                }
            });
        return results.ToArray();
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, CancellationToken token)
    {
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) return;
            total += read;
            if (total > MaximumFeedBytes) throw new HttpRequestException("VPN feed превышает 32 MiB");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static async Task ConnectPublicAsync(string host, int port, int timeoutSeconds, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, timeout.Token);
        if (addresses.Length is 0 or > 32 || addresses.Any(x => !NetworkSafety.IsPublicAddress(x)))
            throw new IOException("VPN endpoint разрешён в локальный или служебный адрес");
        Exception? last = null;
        foreach (var address in addresses)
        {
            using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
            try { await client.ConnectAsync(address, port, timeout.Token); return; }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException) { last = exception; }
        }
        throw new IOException("VPN endpoint недоступен", last);
    }

    private sealed record FetchResult(VpnSource Source, IReadOnlyList<VpnCandidate> Candidates, string? Error);
}

/// <summary>Сводка завершённого VPN-сбора.</summary>
public sealed record VpnCollectionResult
{
    /// <summary>Создаёт сводку сбора.</summary>
    public VpnCollectionResult(int sources, int succeeded, int candidates, int added) =>
        (Sources, Succeeded, Candidates, Added) = (sources, succeeded, candidates, added);
    /// <summary>Обработано источников.</summary>
    public int Sources { get; }
    /// <summary>Успешных источников.</summary>
    public int Succeeded { get; }
    /// <summary>Найдено кандидатов.</summary>
    public int Candidates { get; }
    /// <summary>Добавлено новых endpoint.</summary>
    public int Added { get; }
}

/// <summary>Сводка проверки VPN endpoint.</summary>
public sealed record VpnValidationResult
{
    /// <summary>Создаёт сводку проверки.</summary>
    public VpnValidationResult(int checkedCount, int reachable, int unsupportedTransport) =>
        (Checked, Reachable, UnsupportedTransport) = (checkedCount, reachable, unsupportedTransport);
    /// <summary>Всего обработано.</summary>
    public int Checked { get; }
    /// <summary>Доступных TCP endpoint.</summary>
    public int Reachable { get; }
    /// <summary>UDP endpoint без небезопасной протокольной проверки.</summary>
    public int UnsupportedTransport { get; }
}

/// <summary>Запускает сбор VPN feed независимо от более частой проверки endpoint.</summary>
public sealed class VpnCollectorWorker(VpnCatalogService service, IOptions<CollectorOptions> options, ILogger<VpnCollectorWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CycleFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1162, "VpnCycleFailed"), "VPN catalog cycle failed");
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundWorkersEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await service.CollectAsync(token: stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                OperationalLogBoundary.Write(() => CycleFailed(logger, exception));
            }
            await Task.Delay(TimeSpan.FromMinutes(options.Value.CollectionIntervalMinutes), stoppingToken);
        }
    }
}

/// <summary>Проверяет очередную VPN-партию с интервалом валидатора, не ожидая следующего сбора.</summary>
public sealed class VpnValidatorWorker(VpnCatalogService service, IOptions<CollectorOptions> options, ILogger<VpnValidatorWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CycleFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1163, "VpnValidationCycleFailed"), "VPN validation cycle failed");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundWorkersEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await service.ValidateAsync(stoppingToken); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                OperationalLogBoundary.Write(() => CycleFailed(logger, exception));
            }
            await Task.Delay(TimeSpan.FromMinutes(options.Value.ValidationIntervalMinutes), stoppingToken);
        }
    }
}
