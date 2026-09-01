using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Keeps the built-in validator as a fail-open reserve while external checker nodes
/// have enough recently confirmed capacity to drain the shared queue themselves.
/// </summary>
public sealed class LocalValidationStandbyGate(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<CollectorOptions> options,
    ILogger<LocalValidationStandbyGate> logger,
    TimeProvider timeProvider) : IDisposable
{
    internal static readonly TimeSpan HealthyHeartbeatWindow = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan StandbyPollInterval = TimeSpan.FromSeconds(15);

    private static readonly Action<ILogger, Exception?> CapacityReadFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1103, "ExternalCheckerCapacityReadFailed"),
            "Не удалось проверить доступную ёмкость внешних checker-узлов; локальная проверка продолжит работу.");

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Snapshot? _snapshot;

    /// <summary>
    /// Returns true only when enabled nodes with a fresh heartbeat provide at least
    /// the configured local concurrency. Any database failure deliberately returns
    /// false, so validation never stops because the standby decision could not be made.
    /// </summary>
    public async ValueTask<bool> ShouldStandByAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null && snapshot.ExpiresAt > now)
            return snapshot.ShouldStandBy;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            snapshot = Volatile.Read(ref _snapshot);
            if (snapshot is not null && snapshot.ExpiresAt > now)
                return snapshot.ShouldStandBy;

            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var heartbeatCutoff = now.Subtract(HealthyHeartbeatWindow);
                var externalConcurrency = await db.CheckerNodes
                    .AsNoTracking()
                    .Where(node => node.Enabled &&
                        node.DeploymentStatus == "online" &&
                        node.LastHeartbeatAt >= heartbeatCutoff)
                    .SumAsync(node => (long)node.Concurrency, cancellationToken);
                var requiredConcurrency = Math.Clamp(options.Value.ValidationConcurrency, 1, 1_000);
                var shouldStandBy = externalConcurrency >= requiredConcurrency;
                Volatile.Write(ref _snapshot, new Snapshot(shouldStandBy, now.Add(SnapshotLifetime)));
                return shouldStandBy;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                OperationalLogBoundary.Write(() => CapacityReadFailed(logger, exception));
                Volatile.Write(ref _snapshot, new Snapshot(false, now.Add(FailureRetryDelay)));
                return false;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Releases the single-flight refresh synchronizer.</summary>
    public void Dispose() => _refreshGate.Dispose();

    private sealed record Snapshot(bool ShouldStandBy, DateTimeOffset ExpiresAt);
}
