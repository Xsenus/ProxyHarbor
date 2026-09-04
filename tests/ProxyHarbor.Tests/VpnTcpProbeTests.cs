using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnTcpProbeTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);
    private static IPAddress[] Addresses(int count) => Enumerable.Range(1, count)
        .Select(index => IPAddress.Parse($"8.8.8.{index}")).ToArray();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task HangingFirstAddressDoesNotPreventHealthyFallback()
    {
        var first = IPAddress.Parse("2606:4700:4700::1111");
        var second = IPAddress.Parse("1.1.1.1");
        var fallbackConnected = false;
        var firstStopped = false;
        using var probe = new VpnTcpProbe(32);
        await probe.ProbeCoreAsync("vpn.example", 443, TimeSpan.FromSeconds(3),
            (_, _) => Task.FromResult(new[] { first, second }),
            async (address, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (address.Equals(second)) { fallbackConnected = true; return; }
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { firstStopped = true; }
            }, CancellationToken.None);
        Assert.True(fallbackConnected);
        Assert.True(firstStopped);
    }

    [Fact]
    public async Task OppositeAddressFamilyIsTriedBeforeAnotherHangingIpv6()
    {
        using var clock = new ProbeClock();
        using var probe = new VpnTcpProbe(32, clock);
        var attempted = Channel.CreateUnbounded<IPAddress>();
        var ipv6 = IPAddress.Parse("2606:4700:4700::1111");
        var ipv4 = IPAddress.Parse("1.1.1.1");
        var operation = probe.ProbeCoreAsync("vpn.example", 443, TimeSpan.FromSeconds(8),
            (_, _) => Task.FromResult(new[] { ipv6, IPAddress.Parse("2606:4700:4700::1001"), ipv4 }),
            async (address, _, token) =>
            {
                attempted.Writer.TryWrite(address);
                if (!address.Equals(ipv4)) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, CancellationToken.None);
        Assert.Equal(ipv6, await attempted.Reader.ReadAsync().AsTask().WaitAsync(TestDeadline));
        await clock.WaitForAttemptTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(ipv4, await attempted.Reader.ReadAsync().AsTask().WaitAsync(TestDeadline));
        Assert.Equal(250, await operation.WaitAsync(TestDeadline));
        Assert.False(attempted.Reader.TryRead(out _));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task ImmediateFailureDoesNotWaitForTheStaggerTimer()
    {
        using var clock = new ProbeClock();
        using var probe = new VpnTcpProbe(32, clock);
        var attempts = 0;
        var latency = await probe.ProbeCoreAsync("vpn.example", 443, TimeSpan.FromSeconds(8),
            (_, _) => Task.FromResult(Addresses(2)),
            (_, _, _) => ++attempts == 1
                ? Task.FromException(new SocketException((int)SocketError.ConnectionRefused)) : Task.CompletedTask,
            CancellationToken.None).WaitAsync(TestDeadline);
        Assert.Equal(2, attempts);
        Assert.Equal(0, latency);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task LaterFailureDoesNotCancelSlowerSuccessfulFirstAddress()
    {
        using var clock = new ProbeClock();
        using var probe = new VpnTcpProbe(32, clock);
        var firstMayFinish = Signal();
        var secondFailed = Signal();
        var operation = probe.ProbeCoreAsync("vpn.example", 443, TimeSpan.FromSeconds(8),
            (_, _) => Task.FromResult(Addresses(2)),
            async (address, _, token) =>
            {
                if (address.Equals(Addresses(1)[0])) await firstMayFinish.Task.WaitAsync(token);
                else { secondFailed.TrySetResult(); throw new SocketException((int)SocketError.ConnectionRefused); }
            }, CancellationToken.None);
        await clock.WaitForAttemptTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await secondFailed.Task.WaitAsync(TestDeadline);
        firstMayFinish.SetResult();
        await operation.WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task SharedDeadlineCancelsAllAttemptsAndLeavesNoTimers()
    {
        using var clock = new ProbeClock();
        using var probe = new VpnTcpProbe(32, clock);
        var started = Channel.CreateUnbounded<bool>();
        var stopped = 0;
        var operation = probe.ProbeCoreAsync("vpn.example", 443, TimeSpan.FromSeconds(8),
            (_, _) => Task.FromResult(Addresses(3)),
            async (_, _, token) =>
            {
                started.Writer.TryWrite(true);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Increment(ref stopped); }
            }, CancellationToken.None);
        await started.Reader.ReadAsync().AsTask().WaitAsync(TestDeadline);
        await clock.WaitForAttemptTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await started.Reader.ReadAsync().AsTask().WaitAsync(TestDeadline);
        clock.Advance(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestDeadline));
        Assert.Equal(2, stopped);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task DnsTimeoutNeverOpensASocket()
    {
        using var clock = new ProbeClock();
        using var probe = new VpnTcpProbe(32, clock);
        var dnsStarted = Signal();
        var operation = probe.ProbeCoreAsync("vpn.example", 443, TimeSpan.FromSeconds(8),
            async (_, token) =>
            {
                dnsStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Addresses(1);
            }, (_, _, _) => throw new InvalidOperationException("Unexpected connect"), CancellationToken.None);
        await dnsStarted.Task.WaitAsync(TestDeadline);
        clock.Advance(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestDeadline));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task DnsTimeIsDeductedFromConnectionBudget()
    {
        using var clock = new ProbeClock();
        using var probe = new VpnTcpProbe(32, clock);
        var started = Signal();
        var operation = probe.ProbeCoreAsync("vpn.example", 443, TimeSpan.FromSeconds(8),
            (_, _) => { clock.Advance(TimeSpan.FromSeconds(6)); return Task.FromResult(Addresses(1)); },
            async (_, _, token) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); },
            CancellationToken.None);
        await started.Task.WaitAsync(TestDeadline);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestDeadline));
    }

    [Fact]
    public async Task AllFailuresPreserveLastNetworkError()
    {
        using var probe = new VpnTcpProbe(32);
        var error = new SocketException((int)SocketError.ConnectionRefused);
        var count = 0;
        var result = await Assert.ThrowsAsync<IOException>(() => probe.ProbeCoreAsync("vpn.example", 443,
            TimeSpan.FromSeconds(8), (_, _) => Task.FromResult(Addresses(3)),
            (_, _, _) => { count++; return Task.FromException(error); }, CancellationToken.None));
        Assert.Same(error, result.InnerException);
        Assert.Equal(3, count);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    public async Task UnsafeAddressAnywhereInDnsBlocksAllConnections(string unsafeAddress)
    {
        using var probe = new VpnTcpProbe(32);
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeCoreAsync("vpn.example", 443,
            TimeSpan.FromSeconds(8), (_, _) => Task.FromResult(new[] { Addresses(1)[0], IPAddress.Parse(unsafeAddress) }),
            (_, _, _) => throw new InvalidOperationException("Unexpected connect"), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task InvalidDnsFanOutIsRejectedBeforeConnection(int count)
    {
        using var probe = new VpnTcpProbe(32);
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeCoreAsync("vpn.example", 443,
            TimeSpan.FromSeconds(8), (_, _) => Task.FromResult(Addresses(count)),
            (_, _, _) => throw new InvalidOperationException("Unexpected connect"), CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateAndMappedAddressesAreOnlyAttemptedOnce()
    {
        using var probe = new VpnTcpProbe(32);
        var address = Addresses(1)[0];
        var attempted = new List<IPAddress>();
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeCoreAsync("vpn.example", 443,
            TimeSpan.FromSeconds(8), (_, _) => Task.FromResult(new[] { address, address, address.MapToIPv6() }),
            (ip, _, _) => { attempted.Add(ip); return Task.FromException(new SocketException()); }, CancellationToken.None));
        Assert.Equal(new[] { address }, attempted);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public async Task PublicLiteralDoesNotResolveDns(string host)
    {
        using var probe = new VpnTcpProbe(32);
        await probe.ProbeCoreAsync(host, 8443, TimeSpan.FromSeconds(8),
            (_, _) => throw new InvalidOperationException("Unexpected DNS"),
            (ip, port, _) => { Assert.Equal(IPAddress.Parse(host), ip); Assert.Equal(8443, port); return Task.CompletedTask; },
            CancellationToken.None);
    }

    [Fact]
    public async Task AlreadyCancelledCallerDoesNotResolveOrConnect()
    {
        using var probe = new VpnTcpProbe(32);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeCoreAsync("vpn.example", 443,
            TimeSpan.FromSeconds(8), (_, _) => throw new InvalidOperationException("Unexpected DNS"),
            (_, _, _) => throw new InvalidOperationException("Unexpected connect"), cancellation.Token));
    }

    [Fact]
    public async Task ReservationWaitDoesNotConsumeTimeoutOrInflateLatency()
    {
        using var clock = new ProbeClock();
        using var probe = new VpnTcpProbe(32, clock);
        var release = Signal();
        var firstStarted = Signal();
        var first = probe.ProbeCoreAsync("full.example", 443, TimeSpan.FromSeconds(30),
            (_, _) => Task.FromResult(Addresses(32)),
            async (_, _, token) => { firstStarted.TrySetResult(); await release.Task.WaitAsync(token); }, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestDeadline);
        var secondConnected = false;
        var second = probe.ProbeCoreAsync("8.8.4.4", 443, TimeSpan.FromSeconds(1),
            (_, _) => throw new InvalidOperationException("Unexpected DNS"),
            (_, _, _) => { secondConnected = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(secondConnected);
        clock.Advance(TimeSpan.FromSeconds(5));
        release.SetResult();
        await first.WaitAsync(TestDeadline);
        Assert.Equal(0, await second.WaitAsync(TestDeadline));
        Assert.True(secondConnected);
    }

    [Fact]
    public async Task WaitingReservationCanBeCancelledWithoutAConnectionOrLeakedPermits()
    {
        using var probe = new VpnTcpProbe(32);
        using var cancellation = new CancellationTokenSource();
        var release = Signal();
        var firstStarted = Signal();
        var first = probe.ProbeCoreAsync("full.example", 443, TimeSpan.FromSeconds(30),
            (_, _) => Task.FromResult(Addresses(32)),
            async (_, _, token) => { firstStarted.TrySetResult(); await release.Task.WaitAsync(token); }, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestDeadline);
        var second = probe.ProbeCoreAsync("8.8.4.4", 443, TimeSpan.FromSeconds(1),
            (_, _) => throw new InvalidOperationException("Unexpected DNS"),
            (_, _, _) => throw new InvalidOperationException("Unexpected connect"), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TestDeadline));
        release.SetResult();
        await first.WaitAsync(TestDeadline);
        await probe.ProbeCoreAsync("reuse.example", 443, TimeSpan.FromSeconds(1),
            (_, _) => Task.FromResult(Addresses(32)), (_, _, _) => Task.CompletedTask, CancellationToken.None).WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task ConcurrentEndpointsNeverExceedGlobalSocketBudget()
    {
        using var probe = new VpnTcpProbe(32);
        var release = Signal();
        var started = Channel.CreateUnbounded<bool>();
        var active = 0;
        var maximum = 0;
        async Task Connect(IPAddress _, int port, CancellationToken token)
        {
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximum); }
            while (current > previous && Interlocked.CompareExchange(ref maximum, current, previous) != previous);
            started.Writer.TryWrite(true);
            try { await release.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref active); }
        }
        var probes = Enumerable.Range(0, 64).Select(_ => probe.ProbeCoreAsync("8.8.8.8", 443,
            TimeSpan.FromSeconds(30), (_, _) => throw new InvalidOperationException("Unexpected DNS"), Connect,
            CancellationToken.None)).ToArray();
        for (var index = 0; index < 32; index++) await started.Reader.ReadAsync().AsTask().WaitAsync(TestDeadline);
        Assert.False(started.Reader.TryRead(out _));
        release.SetResult();
        await Task.WhenAll(probes).WaitAsync(TestDeadline);
        Assert.Equal(32, maximum);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task LastOf32AddressesCanWinAndAllEarlierConnectionsAreDrained()
    {
        using var clock = new ProbeClock();
        using var probe = new VpnTcpProbe(32, clock);
        var addresses = Addresses(32);
        var started = Channel.CreateUnbounded<IPAddress>();
        var stopped = 0;
        var operation = probe.ProbeCoreAsync("many.example", 443, TimeSpan.FromSeconds(30),
            (_, _) => Task.FromResult(addresses),
            async (ip, _, token) =>
            {
                started.Writer.TryWrite(ip);
                if (ip.Equals(addresses[^1])) return;
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Increment(ref stopped); }
            }, CancellationToken.None);
        for (var index = 0; index < addresses.Length; index++)
        {
            Assert.Equal(addresses[index], await started.Reader.ReadAsync().AsTask().WaitAsync(TestDeadline));
            if (index == addresses.Length - 1) break;
            await clock.WaitForAttemptTimerAsync();
            clock.Advance(TimeSpan.FromMilliseconds(250));
        }
        await operation.WaitAsync(TestDeadline);
        Assert.Equal(31, stopped);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task RealTcpSocketIsClosedAfterAProbe()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepting = listener.AcceptTcpClientAsync();
        await VpnTcpProbe.ConnectAsync(IPAddress.Loopback, port, CancellationToken.None).WaitAsync(TestDeadline);
        using var accepted = await accepting.WaitAsync(TestDeadline);
        var buffer = new byte[1];
        Assert.Equal(0, await accepted.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TestDeadline));
    }

    private sealed class ProbeClock : TimeProvider, IDisposable
    {
        private readonly List<ProbeTimer> timers = [];
        private readonly Channel<bool> attemptTimers = Channel.CreateUnbounded<bool>();
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        internal int ActiveTimers { get { lock (timers) return timers.Count(timer => timer.Active); } }
        internal Task<bool> WaitForAttemptTimerAsync() => attemptTimers.Reader.ReadAsync().AsTask().WaitAsync(TestDeadline);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ProbeTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (timers) timers.Add(timer);
            if (dueTime == TimeSpan.FromMilliseconds(250)) attemptTimers.Writer.TryWrite(true);
            return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref ticks, duration.Ticks);
            ProbeTimer[] due;
            lock (timers) due = timers.Where(timer => timer.Active && timer.Due <= ticks).ToArray();
            foreach (var timer in due) timer.Fire();
        }
        public void Dispose() { lock (timers) foreach (var timer in timers) timer.Dispose(); }
        private sealed class ProbeTimer(ProbeClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal long Due { get; private set; }
            internal bool Active { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (clock.timers) { Due = clock.GetTimestamp() + dueTime.Ticks; Active = dueTime >= TimeSpan.Zero; }
                return true;
            }
            internal void Fire()
            {
                lock (clock.timers) { if (!Active) return; Active = false; }
                callback(state);
            }
            public void Dispose() { lock (clock.timers) Active = false; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
