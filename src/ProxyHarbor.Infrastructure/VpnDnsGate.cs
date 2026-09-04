namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Process-wide budget for VPN name resolution, independent of socket concurrency.
/// On Unix the native resolver can occupy a pool thread past caller cancellation.
/// A cancelled waiter must not return its permit until the resolver really completes.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Process-lifetime async-only semaphore; no AvailableWaitHandle is created. It must outlive cancelled probes until native DNS returns.")]
internal sealed class VpnDnsGate
{
    internal static VpnDnsGate Shared { get; } = new(16);
    private readonly SemaphoreSlim slots;

    internal VpnDnsGate(int concurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(concurrency, 128);
        slots = new(concurrency, concurrency);
    }

    internal async Task<Lease> AcquireAsync(CancellationToken token)
    {
        await slots.WaitAsync(token);
        return new Lease(slots);
    }

    internal sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int state;

        internal Task<T> Start<T>(Func<Task<T>> resolve)
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                throw new InvalidOperationException("DNS lease already consumed");
            var operation = CompleteAsync(resolve);
            // Caller may stop awaiting after its deadline. Observe a later failure
            // without logging DNS names, and keep ownership in CompleteAsync.
            _ = operation.ContinueWith(static failed => { _ = failed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return operation;
        }

        private async Task<T> CompleteAsync<T>(Func<Task<T>> resolve)
        {
            try { return await resolve(); }
            finally { slots.Release(); }
        }

        public void Dispose()
        {
            // Disposing the probe releases an unused reservation, not an active
            // native operation. This also makes repeated Dispose harmless.
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0) slots.Release();
        }
    }
}
