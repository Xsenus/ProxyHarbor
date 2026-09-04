using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;

namespace ProxyHarbor.CheckerAgent;

/// <summary>Connection and polling settings for a remote checker agent.</summary>
public sealed class CheckerAgentOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "CheckerAgent";
    /// <summary>HTTPS address of the central ProxyHarbor API.</summary>
    public string ControlPlaneBaseUrl { get; set; } = "";
    /// <summary>Identifier issued by the central service.</summary>
    public Guid NodeId { get; set; }
    /// <summary>Read-only file containing the agent bearer token.</summary>
    public string TokenFile { get; set; } = "/run/secrets/agent_token";
    /// <summary>Delay between empty queue polls.</summary>
    public int EmptyPollSeconds { get; set; } = 5;
}

/// <summary>Pull-agent: берёт одну аренду, полностью исполняет её и подтверждает результат.</summary>
public sealed class CheckerAgentWorker(
    IHttpClientFactory clients,
    CheckerAgentProbeRuntime probeRuntime,
    IOptions<CheckerAgentOptions> configured,
    ILogger<CheckerAgentWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly Action<ILogger, Exception?> CycleFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(1001, nameof(CycleFailed)),
        "Distributed checker cycle failed; the central lease will recover automatically.");
    private static readonly Action<ILogger, Guid, int, Exception?> LeaseCompleted = LoggerMessage.Define<Guid, int>(
        LogLevel.Information, new EventId(1002, nameof(LeaseCompleted)),
        "Lease {LeaseId} completed with {Count} checks.");
    private static readonly Action<ILogger, Exception?> HeartbeatFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1003, nameof(HeartbeatFailed)),
        "Lease heartbeat failed; the next cycle will retry.");
    private static readonly Action<ILogger, Exception?> StatusHeartbeatFailed = LoggerMessage.Define(
        LogLevel.Debug, new EventId(1004, nameof(StatusHeartbeatFailed)),
        "Status heartbeat was not delivered.");
    private static readonly Action<ILogger, Exception?> DrainTimedOut = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1005, nameof(DrainTimedOut)),
        "Checker shutdown drain reached its deadline; unfinished work will recover through its central lease.");
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(25);
    private readonly string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    private readonly TimeProvider clock = TimeProvider.System;
    private readonly CancellationTokenSource pollingStop = new();
    private int drainRequested;
    private string token = "";

    internal CheckerAgentWorker(
        IHttpClientFactory clients,
        CheckerAgentProbeRuntime probeRuntime,
        IOptions<CheckerAgentOptions> configured,
        ILogger<CheckerAgentWorker> logger,
        TimeProvider clock) : this(clients, probeRuntime, configured, logger)
    {
        this.clock = clock;
    }

    /// <summary>
    /// Stops acquiring work, allowing an issued claim/current batch and its acknowledgement
    /// to finish before the host deadline (at most 25 seconds). Heartbeats keep running.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref drainRequested, 1);
        await pollingStop.CancelAsync();
        try
        {
            if (ExecuteTask is { } execution)
                // The host observes background faults; stopping must not rethrow them
                // as a new shutdown failure (same behavior as BackgroundService).
                await Task.WhenAny(execution).WaitAsync(DrainTimeout, clock, cancellationToken);
        }
        catch (TimeoutException) { DrainTimedOut(logger, null); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            // Do not introduce a second, unbounded wait after the drain deadline.
            // BackgroundService still cancels the actual probe/upload token here.
            using var forceStop = new CancellationTokenSource();
            await forceStop.CancelAsync();
            await base.StopAsync(forceStop.Token);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        pollingStop.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var pollWait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, pollingStop.Token);
        var options = configured.Value;
        token = (await File.ReadAllTextAsync(options.TokenFile, stoppingToken)).Trim();
        if (token.Length < 32) throw new InvalidOperationException("Checker agent token is missing or invalid.");
        // Недоступность control plane в момент старта не должна завершать контейнер:
        // основной цикл сам продолжит переподключение с bounded backoff.
        await SendHeartbeatBestEffortAsync(null, stoppingToken);
        while (!stoppingToken.IsCancellationRequested && Volatile.Read(ref drainRequested) == 0)
        {
            try
            {
                var lease = await ClaimAsync(stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.EmptyPollSeconds, 1, 60)), pollWait.Token);
                    continue;
                }
                await ProcessAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (pollWait.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                CycleFailed(logger, exception);
                if (Volatile.Read(ref drainRequested) != 0) return;
                await SendHeartbeatBestEffortAsync(exception.Message, stoppingToken);
                try { await Task.Delay(TimeSpan.FromSeconds(10), pollWait.Token); }
                catch (OperationCanceledException) when (pollWait.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task<CheckerLeaseResponse?> ClaimAsync(CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "api/v1/checker-agent/lease");
        using var response = await clients.CreateClient("control-plane").SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CheckerLeaseResponse>(JsonOptions, token)
            ?? throw new InvalidOperationException("Central service returned an empty lease.");
    }

    internal async Task ProcessAsync(CheckerLeaseResponse lease, CancellationToken stoppingToken)
    {
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var checksStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(lease.LeaseId, lease.Items.Count, checksStop, heartbeatStop.Token);
        var results = new ConcurrentBag<CheckerProxyResult>();
        try
        {
            // Runtime сохраняет согласованный origin snapshot между партиями. Раньше новый
            // OriginIpProvider создавался для каждого lease и фактически обнулял собственный
            // 60-секундный cache, добавляя внешний HTTPS-запрос перед каждой партией.
            var probe = probeRuntime.Get(lease);
            await Parallel.ForEachAsync(lease.Items, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(lease.Concurrency, 1, 1_000),
                CancellationToken = checksStop.Token
            }, async (item, itemToken) =>
            {
                try
                {
                    var endpoint = new ProxyEndpoint
                    {
                        Id = item.Id,
                        Host = item.Host,
                        Port = item.Port,
                        Protocol = item.Protocol
                    };
                    var result = await probe.CheckAsync(endpoint, itemToken);
                    results.Add(new CheckerProxyResult(result.ProxyId, result.IsAlive, result.LatencyMs,
                        result.ExitIp, result.IsAnonymous, result.Error, result.IsDeferred));
                }
                catch (OperationCanceledException) when (itemToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    results.Add(new CheckerProxyResult(item.Id, false, null, null, false,
                        "internal probe error: " + exception.GetType().Name, IsDeferred: true));
                }
            });

            checksStop.Token.ThrowIfCancellationRequested();
            if (results.Count != lease.Items.Count)
                throw new InvalidOperationException($"Incomplete local result: {results.Count}/{lease.Items.Count}.");
            // Heartbeat продолжает продлевать ownership и во время повторных попыток
            // отправки: кратковременный сбой API не превращает готовую партию в просроченную.
            await UploadWithRetryAsync(
                lease.LeaseId, new CheckerLeaseResultRequest(results.ToArray()), stoppingToken);
        }
        catch (OperationCanceledException) when (checksStop.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            throw new LeaseLostException("Central service no longer accepts probes for this lease.");
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }
        LeaseCompleted(logger, lease.LeaseId, results.Count, null);
    }

    private async Task MaintainLeaseAsync(
        Guid leaseId, int activeChecks, CancellationTokenSource checksStop, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    using var request = Request(HttpMethod.Post, $"api/v1/checker-agent/leases/{leaseId}/heartbeat");
                    request.Content = JsonContent.Create(new CheckerHeartbeatRequest(version, activeChecks), options: JsonOptions);
                    using var response = await clients.CreateClient("control-plane").SendAsync(request, token);
                    if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        // Stop unfinished probes promptly once ownership/access is
                        // explicitly revoked. Do not cancel an upload already in
                        // progress: a committed completion also clears its lease,
                        // and its lost acknowledgement must still be retried.
                        await checksStop.CancelAsync();
                        return;
                    }
                    response.EnsureSuccessStatusCode();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    // Одиночный сетевой сбой не останавливает heartbeat навсегда. Если
                    // центральный сервис действительно недоступен дольше TTL, lease сам
                    // вернётся в очередь и поздний upload будет безопасно отклонён.
                    HeartbeatFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task UploadWithRetryAsync(Guid leaseId, CheckerLeaseResultRequest results, CancellationToken token)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var request = Request(HttpMethod.Post, $"api/v1/checker-agent/leases/{leaseId}/results");
                request.Content = JsonContent.Create(results, options: JsonOptions);
                using var response = await clients.CreateClient("control-plane").SendAsync(request, token);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    var detail = await ReadBoundedErrorAsync(response.Content, token);
                    throw new LeaseLostException(
                        string.IsNullOrWhiteSpace(detail)
                            ? "Central service rejected the lease."
                            : $"Central service rejected the lease: {detail}");
                }
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (LeaseLostException) { throw; }
            catch (Exception exception)
            {
                last = exception;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)), token);
            }
        }
        throw new InvalidOperationException("Could not upload lease results after retries.", last);
    }

    private static async Task<string?> ReadBoundedErrorAsync(HttpContent content, CancellationToken token)
    {
        var raw = await content.ReadAsStringAsync(token);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
                raw = message.GetString() ?? raw;
        }
        catch (JsonException)
        {
            // Non-JSON gateway errors are still useful, but remain strictly bounded.
        }
        return raw.Trim()[..Math.Min(500, raw.Trim().Length)];
    }

    private async Task SendHeartbeatAsync(string? error, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "api/v1/checker-agent/heartbeat");
        request.Content = JsonContent.Create(new CheckerHeartbeatRequest(version, Error: error), options: JsonOptions);
        using var response = await clients.CreateClient("control-plane").SendAsync(request, token);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendHeartbeatBestEffortAsync(string? error, CancellationToken token)
    {
        try { await SendHeartbeatAsync(error is null ? null : error[..Math.Min(500, error.Length)], token); }
        catch (Exception exception) { StatusHeartbeatFailed(logger, exception); }
    }

    private HttpRequestMessage Request(HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method,
            new Uri(new Uri(configured.Value.ControlPlaneBaseUrl.TrimEnd('/') + "/"), relative));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Checker-Node", configured.Value.NodeId.ToString("D"));
        return request;
    }
}

internal sealed class LeaseLostException(string message) : InvalidOperationException(message);
