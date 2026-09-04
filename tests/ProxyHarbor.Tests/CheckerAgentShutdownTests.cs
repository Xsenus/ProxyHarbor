using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.CheckerAgent;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Tests;

public sealed class CheckerAgentShutdownTests
{
    [Fact]
    public async Task StopOfFaultedWorkerDoesNotRethrowItsStartupFailure()
    {
        using var fixture = new Fixture((_, _, _) => throw new InvalidOperationException("HTTP must not be reached"));
        fixture.InvalidateToken();
        try
        {
            await fixture.Worker.StartAsync(CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            await fixture.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await fixture.ForceStopAsync(); }
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("probe")]
    [InlineData("upload")]
    public async Task StopDrainsAcquiredWorkWithoutClaimingAnotherBatch(string phase)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = false;
        var claims = 0;
        var uploads = 0;
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var clock = new DrainTimeProvider();
        using var fixture = new Fixture(async (name, request, token) =>
        {
            var current = name == "origin" ? "probe" : request.RequestUri!.AbsolutePath.Split('/')[^1] switch
            {
                "lease" => "claim", "results" => "upload", _ => "heartbeat"
            };
            if (current == "claim") Interlocked.Increment(ref claims);
            if (current == phase)
            {
                reached.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                catch (OperationCanceledException) { interrupted = true; throw; }
            }
            if (current == "probe") throw new HttpRequestException("Neutral test control failure");
            if (current == "upload") Interlocked.Increment(ref uploads);
            if (current == "heartbeat" && request.RequestUri!.AbsolutePath.Contains("/leases/", StringComparison.Ordinal))
                renewed.TrySetResult();
            return current == "claim" ? LeaseResponse() : new HttpResponseMessage(HttpStatusCode.OK);
        }, clock);
        try
        {
            await fixture.Worker.StartAsync(CancellationToken.None);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = fixture.Worker.StopAsync(CancellationToken.None);
            Assert.False(interrupted);
            Assert.False(stopping.IsCompleted);
            if (phase is "probe" or "upload")
            {
                clock.PulseHeartbeat();
                await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(interrupted);
            Assert.Equal(1, claims);
            Assert.Equal(1, uploads);
            Assert.True(fixture.Worker.ExecuteTask!.IsCompletedSuccessfully);
        }
        finally { release.TrySetResult(); await fixture.ForceStopAsync(); }
    }

    [Fact]
    public async Task DrainTimeoutDoesNotWaitAgainForAnUncooperativeOperation()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var clock = new DrainTimeProvider();
        using var fixture = new Fixture(async (name, request, _) =>
        {
            if (name == "origin")
            {
                reached.TrySetResult();
                await release.Task; // Deliberately ignore cancellation to test the outer bound.
                throw new HttpRequestException("Neutral test control failure");
            }
            return request.RequestUri!.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal)
                ? LeaseResponse() : new HttpResponseMessage(HttpStatusCode.OK);
        }, clock);
        try
        {
            await fixture.Worker.StartAsync(CancellationToken.None);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = fixture.Worker.StopAsync(CancellationToken.None);
            await clock.DrainTimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.ExpireDrain();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fixture.Worker.ExecuteTask!.IsCompleted);
        }
        finally { release.TrySetResult(); await fixture.ForceStopAsync(); }
    }

    [Fact]
    public async Task IdleStopDoesNotWaitForEmptyPollDelay()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((_, request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal)) requested.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        try
        {
            await fixture.Worker.StartAsync(CancellationToken.None);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.Worker.ExecuteTask!.IsCompletedSuccessfully);
        }
        finally { await fixture.ForceStopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostDeadlineOrDrainTimeoutCancelsStuckProbeWithoutPartialUpload(bool drainTimeout)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = 0;
        using var clock = new DrainTimeProvider();
        using var fixture = new Fixture(async (name, request, token) =>
        {
            if (name == "origin")
            {
                reached.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { canceled.TrySetResult(); }
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/results", StringComparison.Ordinal)) Interlocked.Increment(ref uploads);
            return request.RequestUri!.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal)
                ? LeaseResponse() : new HttpResponseMessage(HttpStatusCode.OK);
        }, clock);
        using var deadline = new CancellationTokenSource();
        try
        {
            await fixture.Worker.StartAsync(CancellationToken.None);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = fixture.Worker.StopAsync(deadline.Token);
            Assert.False(canceled.Task.IsCompleted);
            if (drainTimeout)
            {
                await clock.DrainTimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
                clock.ExpireDrain();
            }
            else await deadline.CancelAsync();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, uploads);
        }
        finally { await fixture.ForceStopAsync(); }
    }

    private static HttpResponseMessage LeaseResponse()
    {
        var lease = new CheckerLeaseResponse(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), 1, 10,
            "control.example", 443, "/ip",
            [new CheckerProxyItem(Guid.NewGuid(), "198.51.100.1", 8080, ProxyProtocol.Http)]);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(lease, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new JsonStringEnumConverter() } })
        };
    }

    private sealed class Fixture : IHttpClientFactory, IDisposable
    {
        private readonly string tokenFile = Path.GetTempFileName();
        private readonly List<HttpClient> clients = [];
        private readonly Func<string, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        private readonly CheckerAgentProbeRuntime runtime;
        internal CheckerAgentWorker Worker { get; }

        internal Fixture(Func<string, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeProvider? clock = null)
        {
            this.send = send;
            File.WriteAllText(tokenFile, new string('x', 40));
            runtime = new CheckerAgentProbeRuntime(this);
            Worker = new CheckerAgentWorker(this, runtime, Options.Create(new CheckerAgentOptions
            {
                ControlPlaneBaseUrl = "https://control.example", NodeId = Guid.NewGuid(),
                TokenFile = tokenFile, EmptyPollSeconds = 60
            }), NullLogger<CheckerAgentWorker>.Instance, clock ?? TimeProvider.System);
        }
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new Handler((request, token) => send(name, request, token)));
            lock (clients) clients.Add(client);
            return client;
        }
        internal void InvalidateToken() => File.WriteAllText(tokenFile, "short");
        internal async Task ForceStopAsync()
        {
            using var deadline = new CancellationTokenSource();
            deadline.Cancel();
            await Worker.StopAsync(deadline.Token);
            if (Worker.ExecuteTask is { } task)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
                catch (Exception) when (task.IsFaulted) { }
            }
        }
        public void Dispose()
        {
            Worker.Dispose();
            runtime.Dispose();
            foreach (var client in clients) client.Dispose();
            File.Delete(tokenFile);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class DrainTimeProvider : TimeProvider, IDisposable
    {
        private readonly List<TestTimer> timers = [];
        private TestTimer? drainTimer;
        private TestTimer? heartbeatTimer;
        internal TaskCompletionSource DrainTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ExpireDrain() => drainTimer!.Fire();
        internal void PulseHeartbeat() => heartbeatTimer!.Fire();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(callback, state);
            lock (timers) timers.Add(timer);
            if (dueTime == TimeSpan.FromSeconds(30)) heartbeatTimer = timer;
            if (dueTime == TimeSpan.FromSeconds(25))
            {
                drainTimer = timer;
                DrainTimerCreated.TrySetResult();
            }
            return timer;
        }
        public void Dispose() { foreach (var timer in timers) timer.Dispose(); }
        private sealed class TestTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
