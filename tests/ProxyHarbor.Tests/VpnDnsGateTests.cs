using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnDnsGateTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NativeConcurrencyStaysBoundedAcrossManyWaiters()
    {
        var gate = new VpnDnsGate(2);
        var started = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var operations = Enumerable.Range(0, 40).Select(async _ =>
        {
            using var reservation = await gate.AcquireAsync(CancellationToken.None);
            return await reservation.Start(async () =>
            {
                Assert.InRange(Interlocked.Increment(ref active), 1, 2);
                started.Writer.TryWrite(true);
                try { await release.Task; return 1; }
                finally { Interlocked.Decrement(ref active); }
            });
        }).ToArray();
        try
        {
            await started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.False(started.Reader.TryRead(out _));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(40, (await Task.WhenAll(operations).WaitAsync(Deadline)).Sum());
        Assert.Equal(0, active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedWaitKeepsPermitUntilRealOperationCompletes(bool fail)
    {
        var gate = new VpnDnsGate(1);
        var native = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = await gate.AcquireAsync(CancellationToken.None);
        var operation = first.Start(() => native.Task);
        first.Dispose();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(cancelled.Token));
        var next = gate.AcquireAsync(CancellationToken.None);
        Assert.False(next.IsCompleted);
        if (fail) native.SetException(new IOException("late native failure"));
        else native.SetResult(7);
        using var acquired = await next.WaitAsync(Deadline);
        if (fail) await Assert.ThrowsAsync<IOException>(() => operation);
        else Assert.Equal(7, await operation);
    }

    [Fact]
    public async Task SynchronousResolverFailureReturnsPermit()
    {
        var gate = new VpnDnsGate(1);
        using var first = await gate.AcquireAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => first.Start<int>(() => throw new IOException("resolver failed")));
        using var next = await gate.AcquireAsync(CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal(3, await next.Start(() => Task.FromResult(3)));
        Assert.Throws<InvalidOperationException>(() => { _ = next.Start(() => Task.FromResult(4)); });
    }

    [Fact]
    public async Task UnusedReservationReturnsExactlyOnePermit()
    {
        var gate = new VpnDnsGate(1);
        var first = await gate.AcquireAsync(CancellationToken.None);
        first.Dispose();
        first.Dispose();
        Assert.Throws<InvalidOperationException>(() => { _ = first.Start(() => Task.FromResult(4)); });
        using var next = await gate.AcquireAsync(CancellationToken.None).WaitAsync(Deadline);
        using var cancelled = new CancellationTokenSource();
        var third = gate.AcquireAsync(cancelled.Token);
        Assert.False(third.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
    }
}
