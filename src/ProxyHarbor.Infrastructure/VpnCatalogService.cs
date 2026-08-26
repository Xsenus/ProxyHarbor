using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    public async Task<VpnCollectionResult> CollectAsync(CancellationToken token = default)
    {
        await using var operationLock = await PostgresAdvisoryLock.TryAcquireAsync(dbFactory, PostgresAdvisoryLock.VpnCollectionKey, token);
        if (operationLock is null) throw new OperationAlreadyRunningException("VPN collection уже выполняется другой репликой.");
        await using var readDb = await dbFactory.CreateDbContextAsync(token);
        var sources = await readDb.VpnSources.AsNoTracking().Where(x => x.Enabled)
            .OrderBy(x => x.Priority).ToArrayAsync(token);
        var results = await ParallelFetchAsync(sources, token);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        // Каталог и provenance загружаются одним проходом. Это устраняет два SQL-запроса
        // на каждый адрес — на больших публичных feed разница составляет десятки тысяч round-trip.
        var trackedSources = await db.VpnSources.ToDictionaryAsync(x => x.Id, token);
        var endpointsByKey = await db.VpnEndpoints.ToDictionaryAsync(
            x => new { x.Host, x.Port, x.Protocol, x.Transport }, token);
        var linksByKey = (await db.VpnEndpointSources.ToArrayAsync(token))
            .ToDictionary(x => (x.VpnEndpointId, x.VpnSourceId));
        var now = DateTimeOffset.UtcNow;
        var added = 0;
        foreach (var result in results)
        {
            if (!trackedSources.TryGetValue(result.Source.Id, out var source)) continue;
            source.LastFetchedAt = now;
            if (result.Error is not null)
            {
                source.ConsecutiveFailures++;
                source.LastError = result.Error[..Math.Min(result.Error.Length, 500)];
                continue;
            }
            source.LastSucceededAt = now;
            source.LastItemCount = result.Candidates.Count;
            source.ConsecutiveFailures = 0;
            source.LastError = null;
            foreach (var candidate in result.Candidates.Take(options.Value.MaxProxiesPerSource))
            {
                var key = new { candidate.Host, candidate.Port, candidate.Protocol, candidate.Transport };
                if (!endpointsByKey.TryGetValue(key, out var endpoint))
                {
                    endpoint = new VpnEndpoint
                    {
                        Host = candidate.Host,
                        Port = candidate.Port,
                        Protocol = candidate.Protocol,
                        Transport = candidate.Transport,
                        ConnectionUri = candidate.ConnectionUri,
                        FirstSourceId = source.Id,
                        FirstSeenAt = now,
                        LastSeenAt = now
                    };
                    db.VpnEndpoints.Add(endpoint);
                    endpointsByKey.Add(key, endpoint);
                    added++;
                }
                else
                {
                    endpoint.LastSeenAt = now;
                    // Feed может исправить параметры существующего endpoint. Сохраняем
                    // последнюю полноценную публичную ссылку, но не затираем её метаданными.
                    if (!string.IsNullOrWhiteSpace(candidate.ConnectionUri))
                        endpoint.ConnectionUri = candidate.ConnectionUri;
                }

                if (!linksByKey.TryGetValue((endpoint.Id, source.Id), out var link))
                {
                    link = new VpnEndpointSource
                    { VpnEndpointId = endpoint.Id, VpnSourceId = source.Id, LastSeenAt = now };
                    db.VpnEndpointSources.Add(link);
                    linksByKey.Add((endpoint.Id, source.Id), link);
                }
                else link.LastSeenAt = now;
            }
        }
        await db.SaveChangesAsync(token);
        return new(sources.Length, results.Count(x => x.Error is null), results.Sum(x => x.Candidates.Count), added);
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
                await service.CollectAsync(stoppingToken);
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
