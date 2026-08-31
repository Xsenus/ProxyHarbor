using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

public sealed class PaymentReconciliationWorkerTests
{
    [Fact]
    public async Task BoundedProcessorUsesConfiguredParallelismAndCountsOnlyCompletedItems()
    {
        var active = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processing = PaymentReconciliationWorker.ProcessBoundedAsync(
            Enumerable.Range(1, 24).ToArray(), 4, async item =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref peak, current);
                if (current == 4) started.TrySetResult();
                await release.Task;
                Interlocked.Decrement(ref active);
                return item % 2 == 0;
            }, CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, Volatile.Read(ref peak));
        Assert.Equal(4, Volatile.Read(ref active));
        release.SetResult();

        Assert.Equal(12, await processing);
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task BoundedProcessorRejectsInvalidParallelism()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PaymentReconciliationWorker.ProcessBoundedAsync(
                Array.Empty<int>(), 0, _ => Task.FromResult(true), CancellationToken.None));
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
