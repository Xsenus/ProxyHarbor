using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.CheckerAgent;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Tests;

public sealed class CheckerAgentWorkerTests
{
    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task LostLeaseCancelsInFlightProbesWithoutUploadingPartialResults(HttpStatusCode status)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = 0;
        using var clients = new TestClients(async (name, request, token) =>
        {
            if (name == "origin")
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { canceled.TrySetResult(); }
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/results", StringComparison.Ordinal)) Interlocked.Increment(ref uploads);
            return new HttpResponseMessage(status);
        });
        using var runtime = new CheckerAgentProbeRuntime(clients);
        using var clock = new PulseTimeProvider();
        using var worker = Worker(clients, runtime, clock);
        using var stop = new CancellationTokenSource();
        var processing = worker.ProcessAsync(Lease(3), stop.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Pulse();
            await Assert.ThrowsAsync<LeaseLostException>(() => processing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(canceled.Task.IsCompletedSuccessfully);
            Assert.Equal(0, uploads);
            Assert.True(clock.TimerDisposed);
        }
        finally
        {
            await stop.CancelAsync();
            try { await processing; } catch (Exception) { }
        }
    }

    [Fact]
    public async Task TransientHeartbeatFailureDoesNotCancelProbesAndNextHeartbeatRetries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHeartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHeartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeats = 0;
        using var clients = new TestClients(async (name, _, token) =>
        {
            if (name == "origin")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            if (Interlocked.Increment(ref heartbeats) == 1)
            {
                firstHeartbeat.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            secondHeartbeat.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var runtime = new CheckerAgentProbeRuntime(clients);
        using var clock = new PulseTimeProvider();
        using var worker = Worker(clients, runtime, clock);
        using var stop = new CancellationTokenSource();
        var processing = worker.ProcessAsync(Lease(3), stop.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Pulse();
            await firstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Pulse();
            await secondHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(processing.IsCompleted);
            Assert.Equal(2, heartbeats);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
            Assert.True(clock.TimerDisposed);
        }
        finally
        {
            await stop.CancelAsync();
            try { await processing; } catch (Exception) { }
        }
    }

    [Fact]
    public async Task HeartbeatConflictDuringUploadDoesNotCancelCompletionAcknowledgementRetry()
    {
        var firstUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = 0;
        using var clients = new TestClients((name, request, _) =>
        {
            // Force a neutral local result without opening proxy sockets.
            if (name == "origin") throw new HttpRequestException("Test control unavailable");
            if (request.RequestUri!.AbsolutePath.EndsWith("/results", StringComparison.Ordinal))
            {
                var attempt = Interlocked.Increment(ref uploads);
                firstUpload.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(attempt == 1
                    ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
            }
            heartbeatSeen.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        });
        using var runtime = new CheckerAgentProbeRuntime(clients);
        using var clock = new PulseTimeProvider();
        using var worker = Worker(clients, runtime, clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var processing = worker.ProcessAsync(Lease(1), stop.Token);
        try
        {
            await firstUpload.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Pulse();
            await heartbeatSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, uploads);
            Assert.True(clock.TimerDisposed);
        }
        finally
        {
            await stop.CancelAsync();
            try { await processing; } catch (Exception) { }
        }
    }

    private static CheckerAgentWorker Worker(TestClients clients, CheckerAgentProbeRuntime runtime, TimeProvider clock) =>
        new(clients, runtime, Options.Create(new CheckerAgentOptions
        {
            ControlPlaneBaseUrl = "https://control.example", NodeId = Guid.NewGuid()
        }), NullLogger<CheckerAgentWorker>.Instance, clock);

    private static CheckerLeaseResponse Lease(int count) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), 1, 10, "control.example", 443, "/ip",
        Enumerable.Range(1, count).Select(index =>
            new CheckerProxyItem(Guid.NewGuid(), $"198.51.100.{index}", 8080, ProxyProtocol.Http)).ToArray());

    private sealed class TestClients(Func<string, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> clients = [];
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new Handler((request, token) => send(name, request, token)));
            lock (clients) clients.Add(client);
            return client;
        }
        public void Dispose()
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class PulseTimeProvider : TimeProvider, IDisposable
    {
        private PulseTimer? timer;
        internal bool TimerDisposed => timer?.Disposed == true;
        internal void Pulse() => timer!.Pulse();
        public void Dispose() => timer?.Dispose();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timer = new PulseTimer(callback, state);

        private sealed class PulseTimer(TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed { get; private set; }
            internal void Pulse() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
