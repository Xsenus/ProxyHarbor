using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет bounded wake-up между collection и validation worker.</summary>
public sealed class ValidationWakeSignalTests
{
    [Fact]
    public async Task PendingPulseSkipsLongIdleDelay()
    {
        var signal = new ValidationWakeSignal();
        signal.Pulse();

        await signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MultiplePulsesAreCoalescedIntoOnePendingWake()
    {
        var signal = new ValidationWakeSignal();
        signal.Pulse();
        signal.Pulse();
        signal.Pulse();

        await signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        using var stopping = new CancellationTokenSource();
        var secondWait = signal.WaitAsync(TimeSpan.FromMinutes(1), stopping.Token);
        Assert.False(secondWait.IsCompleted);
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondWait);
    }

    [Fact]
    public async Task TimeoutReturnsNormallyWithoutSyntheticPulse()
    {
        var signal = new ValidationWakeSignal();

        await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ShutdownCancellationIsNotSwallowed()
    {
        var signal = new ValidationWakeSignal();
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            signal.WaitAsync(TimeSpan.FromMinutes(1), stopping.Token));
    }

    [Fact]
    public async Task ZeroDelayDoesNotConsumePendingWake()
    {
        var signal = new ValidationWakeSignal();
        signal.Pulse();

        await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        await signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }
}
