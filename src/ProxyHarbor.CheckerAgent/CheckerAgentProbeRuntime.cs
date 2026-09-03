using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.CheckerAgent;

/// <summary>
/// Сохраняет дорогой control-endpoint runtime между последовательными lease одного агента.
/// При изменении переданной центральным сервером конфигурации старый snapshot немедленно
/// уничтожается, поэтому обновлённые host/path/timeout начинают действовать со следующей партии.
/// </summary>
public sealed class CheckerAgentProbeRuntime(IHttpClientFactory clients) : IDisposable
{
    private ProbeConfiguration? configuration;
    private OriginIpProvider? origin;
    private ProxyProbeService? probe;

    internal ProxyProbeService Get(CheckerLeaseResponse lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var requested = new ProbeConfiguration(
            lease.ProbeHost, lease.ProbePort, lease.ProbePath, lease.ProbeTimeoutSeconds);
        if (probe is not null && configuration == requested)
            return probe;

        origin?.Dispose();
        var collectorOptions = Options.Create(new CollectorOptions
        {
            ProbeHost = requested.Host,
            ProbePort = requested.Port,
            ProbePath = requested.Path,
            ProbeFallbackUrls = CollectorOptions.CreateDefaultProbeFallbackUrls(),
            ProbeTimeoutSeconds = requested.TimeoutSeconds
        });
        origin = new OriginIpProvider(clients, collectorOptions, new ProbeControlHealth());
        probe = new ProxyProbeService(collectorOptions, origin);
        configuration = requested;
        return probe;
    }

    /// <summary>Освобождает кэш и сетевые ресурсы последней конфигурации probe.</summary>
    public void Dispose()
    {
        origin?.Dispose();
        origin = null;
        probe = null;
        configuration = null;
    }

    private sealed record ProbeConfiguration(string Host, int Port, string Path, int TimeoutSeconds);
}
