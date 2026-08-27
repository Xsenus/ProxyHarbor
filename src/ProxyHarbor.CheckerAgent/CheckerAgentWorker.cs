using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

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
    private readonly string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    private string token = "";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configured.Value;
        token = (await File.ReadAllTextAsync(options.TokenFile, stoppingToken)).Trim();
        if (token.Length < 32) throw new InvalidOperationException("Checker agent token is missing or invalid.");
        // Недоступность control plane в момент старта не должна завершать контейнер:
        // основной цикл сам продолжит переподключение с bounded backoff.
        await SendHeartbeatBestEffortAsync(null, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lease = await ClaimAsync(stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.EmptyPollSeconds, 1, 60)), stoppingToken);
                    continue;
                }
                await ProcessAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                CycleFailed(logger, exception);
                await SendHeartbeatBestEffortAsync(exception.Message, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
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

    private async Task ProcessAsync(CheckerLeaseResponse lease, CancellationToken stoppingToken)
    {
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(lease.LeaseId, lease.Items.Count, heartbeatStop.Token);
        var results = new ConcurrentBag<CheckerProxyResult>();
        var collector = new CollectorOptions
        {
            ProbeHost = lease.ProbeHost,
            ProbePort = lease.ProbePort,
            ProbePath = lease.ProbePath,
            ProbeTimeoutSeconds = lease.ProbeTimeoutSeconds
        };
        var collectorOptions = Options.Create(collector);
        var health = new ProbeControlHealth();
        using var origin = new OriginIpProvider(clients, collectorOptions, health);
        var probe = new ProxyProbeService(collectorOptions, origin);
        try
        {
            await Parallel.ForEachAsync(lease.Items, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(lease.Concurrency, 1, 1_000),
                CancellationToken = stoppingToken
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

            if (results.Count != lease.Items.Count)
                throw new InvalidOperationException($"Incomplete local result: {results.Count}/{lease.Items.Count}.");
            // Heartbeat продолжает продлевать ownership и во время повторных попыток
            // отправки: кратковременный сбой API не превращает готовую партию в просроченную.
            await UploadWithRetryAsync(
                lease.LeaseId, new CheckerLeaseResultRequest(results.ToArray()), stoppingToken);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }
        LeaseCompleted(logger, lease.LeaseId, results.Count, null);
    }

    private async Task MaintainLeaseAsync(Guid leaseId, int activeChecks, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    using var request = Request(HttpMethod.Post, $"api/v1/checker-agent/leases/{leaseId}/heartbeat");
                    request.Content = JsonContent.Create(new CheckerHeartbeatRequest(version, activeChecks), options: JsonOptions);
                    using var response = await clients.CreateClient("control-plane").SendAsync(request, token);
                    if (response.StatusCode == HttpStatusCode.Conflict) return;
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
                    throw new InvalidOperationException("Central service rejected an expired lease.");
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                last = exception;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)), token);
            }
        }
        throw new InvalidOperationException("Could not upload lease results after retries.", last);
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
