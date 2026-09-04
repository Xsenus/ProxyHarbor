using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class PublicAddressConnectorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static IPAddress[] Addresses(int count) => Enumerable.Range(1, count).Select(i => IPAddress.Parse($"8.8.8.{i}")).ToArray();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<Stream> Connect(PublicAddressConnector connector, IPAddress[] addresses,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect, CancellationToken token = default) =>
        PublicNetworkConnector.ConnectCoreAsync(new DnsEndPoint("feed.example", 443),
            (_, _) => Task.FromResult(addresses), connect, token, connector).AsTask();

    [Fact]
    public async Task HangingFirstAddressIsCancelledBeforeReturningWorkingFallback()
    {
        using var clock = new DelayClock();
        using var connector = new PublicAddressConnector(32, clock);
        using var expected = new TrackedStream();
        var addresses = Addresses(2);
        var stopped = 0;
        var operation = Connect(connector, addresses, async (ip, port, token) =>
        {
            Assert.Equal(443, port);
            if (ip.Equals(addresses[0]))
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Increment(ref stopped); }
            }
            return expected;
        });
        await clock.FireNextAsync();
        Assert.Same(expected, await operation.WaitAsync(Deadline));
        Assert.Equal(1, stopped);
        Assert.Equal(0, expected.Disposals);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task LateSuccessfulLoserIsClosedButWinningStreamStaysUsable()
    {
        using var clock = new DelayClock();
        using var connector = new PublicAddressConnector(32, clock);
        using var winner = new TrackedStream();
        using var loser = new TrackedStream();
        var addresses = Addresses(2);
        var operation = Connect(connector, addresses, async (ip, _, token) =>
        {
            if (!ip.Equals(addresses[0])) return winner;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { }
            return loser; // Kernel connect success races with cancellation.
        });
        await clock.FireNextAsync();
        Assert.Same(winner, await operation.WaitAsync(Deadline));
        Assert.Equal(1, loser.Disposals);
        Assert.Equal(0, winner.Disposals);
        winner.WriteByte(42);
        Assert.Equal(1, winner.Length);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task CallerCancellationDisposesEvenAConcurrentSuccessfulStream()
    {
        using var connector = new PublicAddressConnector();
        using var cancellation = new CancellationTokenSource();
        using var stream = new TrackedStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Connect(connector, Addresses(1),
            (_, _, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult<Stream>(stream);
            }, cancellation.Token));
        Assert.Equal(1, stream.Disposals);
    }

    [Fact]
    public async Task CancellationStopsAllPendingAttemptsAndDoesNotStartTheRest()
    {
        using var clock = new DelayClock();
        using var connector = new PublicAddressConnector(32, clock);
        using var cancellation = new CancellationTokenSource();
        var started = Channel.CreateUnbounded<bool>();
        var stopped = 0;
        var operation = Connect(connector, Addresses(32), async (_, _, token) =>
        {
            started.Writer.TryWrite(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Increment(ref stopped); }
            return new MemoryStream();
        }, cancellation.Token);
        await started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        await clock.FireNextAsync();
        await started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Deadline));
        Assert.False(started.Reader.TryRead(out _));
        Assert.Equal(2, stopped);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task ImmediateFailuresDoNotWaitForStaggerAndPreserveLastError()
    {
        using var clock = new DelayClock();
        using var connector = new PublicAddressConnector(32, clock);
        var failures = new[] { new SocketException((int)SocketError.ConnectionRefused), new SocketException((int)SocketError.HostUnreachable) };
        var attempts = 0;
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Connect(connector, Addresses(2),
            (_, _, _) => ValueTask.FromException<Stream>(failures[attempts++])).WaitAsync(Deadline));
        Assert.Equal(2, attempts);
        Assert.Same(failures[1], exception.InnerException);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task DuplicateAndMappedAddressesAreCoalescedAndFamiliesInterleaved()
    {
        using var connector = new PublicAddressConnector();
        var v6 = IPAddress.Parse("2606:4700:4700::1111");
        var otherV6 = IPAddress.Parse("2606:4700:4700::1001");
        var v4 = IPAddress.Parse("8.8.8.8");
        var attempts = new List<IPAddress>();
        await Assert.ThrowsAsync<HttpRequestException>(() => Connect(connector,
            [v6, otherV6, v6, v4.MapToIPv6(), v4], (ip, _, _) =>
            {
                attempts.Add(ip);
                return ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused));
            }));
        Assert.Equal(new[] { v6, v4, otherV6 }, attempts);
    }

    [Fact]
    public async Task UnexpectedFailureStillDrainsAndClosesSuccessfulLoser()
    {
        using var clock = new DelayClock();
        using var connector = new PublicAddressConnector(32, clock);
        using var loser = new TrackedStream();
        var addresses = Addresses(2);
        var failure = new InvalidOperationException("Injected failure");
        var operation = Connect(connector, addresses, async (ip, _, token) =>
        {
            if (!ip.Equals(addresses[0])) throw failure;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { }
            return loser;
        });
        await clock.FireNextAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => operation.WaitAsync(Deadline)));
        Assert.Equal(1, loser.Disposals);
    }

    [Fact]
    public async Task ThirtySecondAddressCanWinWithoutOrphanedAttempts()
    {
        using var clock = new DelayClock();
        using var connector = new PublicAddressConnector(32, clock);
        using var winner = new TrackedStream();
        var addresses = Addresses(32);
        var started = Channel.CreateUnbounded<IPAddress>();
        var stopped = 0;
        var operation = Connect(connector, addresses, async (ip, _, token) =>
        {
            started.Writer.TryWrite(ip);
            if (ip.Equals(addresses[^1])) return winner;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Increment(ref stopped); }
            return new MemoryStream();
        });
        for (var index = 0; index < addresses.Length; index++)
        {
            Assert.Equal(addresses[index], await started.Reader.ReadAsync().AsTask().WaitAsync(Deadline));
            if (index < addresses.Length - 1) await clock.FireNextAsync();
        }
        Assert.Same(winner, await operation.WaitAsync(Deadline));
        Assert.Equal(31, stopped);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task ConcurrentEndpointsRespectGlobalConnectBudgetAndReleaseIt()
    {
        using var connector = new PublicAddressConnector(32);
        var release = Signal();
        var started = Channel.CreateUnbounded<bool>();
        var active = 0;
        var maximum = 0;
        async ValueTask<Stream> Open(IPAddress _, int port, CancellationToken token)
        {
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximum); }
            while (current > previous && Interlocked.CompareExchange(ref maximum, current, previous) != previous);
            started.Writer.TryWrite(true);
            try { await release.Task.WaitAsync(token); return new TrackedStream(); }
            finally { Interlocked.Decrement(ref active); }
        }
        var operations = Enumerable.Range(0, 48).Select(_ => Connect(connector, Addresses(1), Open)).ToArray();
        for (var index = 0; index < 32; index++) await started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.False(started.Reader.TryRead(out _));
        release.SetResult();
        foreach (var stream in await Task.WhenAll(operations).WaitAsync(Deadline)) stream.Dispose();
        Assert.Equal(32, maximum);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task CancellationWhileQueuedDoesNotOpenSocketOrLeakReservation()
    {
        using var connector = new PublicAddressConnector(32);
        using var cancellation = new CancellationTokenSource();
        var release = Signal();
        var started = Signal();
        var first = Connect(connector, Addresses(32), async (_, _, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new TrackedStream();
        });
        await started.Task.WaitAsync(Deadline);
        var queued = Connect(connector, Addresses(1), (_, _, _) => throw new InvalidOperationException("Must not connect"), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
        release.SetResult();
        using var winner = await first.WaitAsync(Deadline);
        using var next = await Connect(connector, Addresses(32), (_, _, _) => ValueTask.FromResult<Stream>(new MemoryStream())).WaitAsync(Deadline);
    }

    [Fact]
    public async Task ResolverReturningAfterCancellationCannotStartSocket()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublicNetworkConnector.ConnectCoreAsync(
            new DnsEndPoint("feed.example", 443), (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(Addresses(1));
            }, (_, _, _) => throw new InvalidOperationException("Must not connect"), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task RealWinningNetworkStreamRemainsOpenUntilConsumerDisposesIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var connector = new PublicAddressConnector();
        var accepting = listener.AcceptTcpClientAsync();
        using var stream = await Connect(connector, Addresses(1),
            (_, _, token) => PublicNetworkConnector.OpenStreamAsync(IPAddress.Loopback, port, token)).WaitAsync(Deadline);
        using var accepted = await accepting.WaitAsync(Deadline);
        await stream.WriteAsync(new byte[] { 42 });
        var buffer = new byte[1];
        Assert.Equal(1, await accepted.GetStream().ReadAsync(buffer).AsTask().WaitAsync(Deadline));
        Assert.Equal(42, buffer[0]);
        stream.Dispose();
        Assert.Equal(0, await accepted.GetStream().ReadAsync(buffer).AsTask().WaitAsync(Deadline));
    }

    private sealed class TrackedStream : MemoryStream
    {
        internal int Disposals { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    // Only one-shot stagger timers are used here. No real network or wall-clock
    // sleeps: each simulated250ms is advanced explicitly by the test.
    private sealed class DelayClock : TimeProvider, IDisposable
    {
        private readonly Channel<DelayTimer> scheduled = Channel.CreateUnbounded<DelayTimer>();
        private readonly ConcurrentBag<DelayTimer> timers = [];
        internal int ActiveTimers => timers.Count(timer => timer.Active);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new DelayTimer(callback, state);
            timers.Add(timer);
            scheduled.Writer.TryWrite(timer);
            return timer;
        }
        internal async Task FireNextAsync()
        {
            while (true)
            {
                var timer = await scheduled.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
                if (timer.Fire()) return;
            }
        }
        public void Dispose() { foreach (var timer in timers) timer.Dispose(); }
        private sealed class DelayTimer(TimerCallback callback, object? state) : ITimer
        {
            private int active = 1;
            internal bool Active => Volatile.Read(ref active) == 1;
            internal bool Fire()
            {
                if (Interlocked.Exchange(ref active, 0) == 0) return false;
                callback(state);
                return true;
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref active, 0);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
