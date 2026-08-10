using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет fail-closed shutdown API при потере lifetime-lock session.</summary>
public sealed class RuntimeLeaseMonitorTests
{
    [Fact]
    public async Task LoggingFailureCannotPreventShutdownOrFaultMonitorTask()
    {
        using var lifetime = new TestLifetime();
        var leaseFailure = new IOException("Deterministic lease heartbeat failure.");
        Exception? observedFailure = null;

        var monitorFailure = await Record.ExceptionAsync(() => RuntimeLeaseMonitor.RunCoreAsync(
            _ => Task.FromException(leaseFailure),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(1),
            lifetime,
            NullLogger.Instance,
            (_, failure) =>
            {
                observedFailure = failure;
                throw new InvalidOperationException("Deterministic logging provider failure.");
            }));

        Assert.Null(monitorFailure);
        Assert.Same(leaseFailure, observedFailure);
        Assert.Equal(1, lifetime.StopCalls);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        private int _stopCalls;

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        internal int StopCalls => Volatile.Read(ref _stopCalls);

        public void StopApplication()
        {
            Interlocked.Increment(ref _stopCalls);
            _stopping.Cancel();
        }

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
