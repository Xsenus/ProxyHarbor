using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class CatalogRequestDiagnosticsTests
{
    [Theory]
    [InlineData("GET", "/api/v1/proxies", true)]
    [InlineData("GET", "/api/v1/vpn", true)]
    [InlineData("POST", "/api/v1/proxies", false)]
    [InlineData("GET", "/api/v1/proxies/seek", false)]
    [InlineData("GET", "/api/v1/vpn/export/json", false)]
    [InlineData("GET", "/api/v1/admin/users", false)]
    public void OnlyCatalogGetsHaveTraces(string method, string path, bool expected)
    {
        var context = Context(path);
        context.Request.Method = method;
        var diagnostics = new CatalogRequestDiagnostics(new RecordingLogger(), new TestClock());
        var trace = diagnostics.Begin(context);
        Assert.Equal(expected, trace is not null);
        Assert.Same(trace, context.Features.Get<CatalogRequestTrace>());
    }

    [Fact]
    public void PhasesAccumulateAcrossRetriesAndIncludeExceptionalDisposal()
    {
        var clock = new TestClock();
        var logger = new RecordingLogger();
        var diagnostics = new CatalogRequestDiagnostics(logger, clock);
        var context = Context("/api/v1/proxies");
        context.Request.QueryString = new QueryString("?token=private-value&country=DE");
        var trace = diagnostics.Begin(context);
        clock.Advance(100); // Pipeline before the controller.
        using (CatalogRequestTrace.Measure(context, CatalogReadPhase.Controller))
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var count = CatalogRequestTrace.Measure(context, CatalogReadPhase.Count);
                clock.Advance(200);
                throw new InvalidOperationException("private-error");
            }));
            using (CatalogRequestTrace.Measure(context, CatalogReadPhase.Count)) clock.Advance(100);
            using (CatalogRequestTrace.Measure(context, CatalogReadPhase.Access)) clock.Advance(50);
            using (CatalogRequestTrace.Measure(context, CatalogReadPhase.Selection)) clock.Advance(150);
        }
        clock.Advance(100); // Serialization after the controller.
        diagnostics.Complete(trace);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("proxies", entry["Catalog"]);
        Assert.Equal(700d, entry["TotalMs"]);
        Assert.Equal(500d, entry["ControllerMs"]);
        Assert.Equal(300d, entry["CountMs"]);
        Assert.Equal(50d, entry["AccessMs"]);
        Assert.Equal(150d, entry["SelectionMs"]);
        Assert.Equal(7, entry.Count); // Six bounded fields and the constant format template.
        Assert.DoesNotContain("private", string.Join(" ", entry.Values), StringComparison.Ordinal);
    }

    [Fact]
    public void FastRequestsDoNotConsumeTheSlowLogBudget()
    {
        var clock = new TestClock();
        var logger = new RecordingLogger();
        var diagnostics = new CatalogRequestDiagnostics(logger, clock);
        var fast = diagnostics.Begin(Context("/api/v1/vpn"));
        clock.Advance(499);
        diagnostics.Complete(fast);
        Assert.Empty(logger.Entries);
        var slow = diagnostics.Begin(Context("/api/v1/vpn"));
        clock.Advance(500);
        diagnostics.Complete(slow);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(500d, entry["TotalMs"]);
        Assert.Equal(0d, entry["ControllerMs"]); // Cache / middleware can bypass the controller.
    }

    [Fact]
    public void ConcurrentSlowRequestsLogOncePerCatalogPerInterval()
    {
        var clock = new TestClock();
        var logger = new RecordingLogger();
        var diagnostics = new CatalogRequestDiagnostics(logger, clock);
        var traces = Enumerable.Range(0, 1000)
            .Select(i => diagnostics.Begin(Context(i % 2 == 0 ? "/api/v1/proxies" : "/api/v1/vpn")))
            .ToArray();
        clock.Advance(500);
        Parallel.ForEach(traces, diagnostics.Complete);
        Assert.Equal(2, logger.Entries.Count);

        clock.Advance(29_499);
        var next = diagnostics.Begin(Context("/api/v1/proxies"));
        clock.Advance(500);
        diagnostics.Complete(next);
        Assert.Equal(2, logger.Entries.Count); // Only 29,999 ms since the last log.
        clock.Advance(1);
        diagnostics.Complete(next);
        Assert.Equal(3, logger.Entries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenLogSinkCannotChangeResponseOrMaskOriginalFailure(bool fails)
    {
        var clock = new TestClock();
        var logger = new RecordingLogger { ThrowOnLog = true };
        var diagnostics = new CatalogRequestDiagnostics(logger, clock);
        var context = Context("/api/v1/proxies");
        var original = new InvalidOperationException("original");
        var middleware = new HttpRequestTelemetryMiddleware(ctx =>
        {
            clock.Advance(750);
            if (fails) throw original;
            ctx.Response.StatusCode = 202;
            return Task.CompletedTask;
        });
        if (fails)
            Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                middleware.InvokeAsync(context, new HttpRequestTelemetry(), diagnostics)));
        else
        {
            await middleware.InvokeAsync(context, new HttpRequestTelemetry(), diagnostics);
            Assert.Equal(202, context.Response.StatusCode);
        }
        Assert.Equal(1, logger.Attempts);
    }

    [Fact]
    public async Task CancellationRetainsItsOriginalTokenAndCompletesPhases()
    {
        var clock = new TestClock();
        var logger = new RecordingLogger();
        var diagnostics = new CatalogRequestDiagnostics(logger, clock);
        var context = Context("/api/v1/vpn");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        context.RequestAborted = cancellation.Token;
        var middleware = new HttpRequestTelemetryMiddleware(ctx =>
        {
            using var phase = CatalogRequestTrace.Measure(ctx, CatalogReadPhase.Selection);
            clock.Advance(600);
            cancellation.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            middleware.InvokeAsync(context, new HttpRequestTelemetry(), diagnostics));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(600d, Assert.Single(logger.Entries)["SelectionMs"]);
    }

    [Fact]
    public void MissingTraceHasNoSideEffects()
    {
        using var absentContext = CatalogRequestTrace.Measure(null, CatalogReadPhase.Count);
        using var absentTrace = CatalogRequestTrace.Measure(Context("/other"), CatalogReadPhase.Count);
        var logger = new RecordingLogger();
        new CatalogRequestDiagnostics(logger, new TestClock()).Complete(null);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task RealMiddlewareBindingResolvesDiagnosticsFromDependencyInjection()
    {
        var clock = new TestClock();
        var logger = new RecordingLogger();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new HttpRequestTelemetry());
        services.AddSingleton(new CatalogRequestDiagnostics(logger, clock));
        await using var provider = services.BuildServiceProvider();
        var application = new ApplicationBuilder(provider);
        application.UseMiddleware<HttpRequestTelemetryMiddleware>();
        application.Run(context =>
        {
            Assert.NotNull(context.Features.Get<CatalogRequestTrace>());
            clock.Advance(750);
            return Task.CompletedTask;
        });
        var context = Context("/api/v1/proxies");
        context.RequestServices = provider;
        await application.Build()(context);
        Assert.Equal(750d, Assert.Single(logger.Entries)["TotalMs"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActualControllersMeasureEveryReadPhaseForFreeAndPaidCatalogs(bool vpn, bool paid)
    {
        var clock = new SteppingClock();
        var diagnostics = new CatalogRequestDiagnostics(new RecordingLogger(), clock);
        var context = Context(vpn ? "/api/v1/vpn" : "/api/v1/proxies");
        var trace = Assert.IsType<CatalogRequestTrace>(diagnostics.Begin(context));
        var factory = new TestDbFactory(new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"catalog-diagnostics-{Guid.NewGuid():N}").Options);
        var access = new TestAccess(paid);
        var controllerContext = new ControllerContext { HttpContext = context };
        if (vpn)
        {
            var controller = new VpnController(factory, access) { ControllerContext = controllerContext };
            Assert.IsType<OkObjectResult>((await controller.Get()).Result);
        }
        else
        {
            // Catalog reads never use the separate streaming-export factory.
            var controller = new ProxiesController(factory, Options.Create(new CollectorOptions()), null!, access)
                { ControllerContext = controllerContext };
            Assert.IsType<OkObjectResult>((await controller.Get(null, null, null, null)).Result);
        }
        Assert.True(trace.Milliseconds(CatalogReadPhase.Controller) > 0);
        Assert.True(trace.Milliseconds(CatalogReadPhase.Count) > 0);
        Assert.True(trace.Milliseconds(CatalogReadPhase.Access) > 0);
        Assert.True(trace.Milliseconds(CatalogReadPhase.Selection) > 0);
    }

    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        return context;
    }

    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(int milliseconds) => Interlocked.Add(ref timestamp, milliseconds * TimeSpan.TicksPerMillisecond);
    }

    private sealed class SteppingClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref timestamp, TimeSpan.TicksPerMillisecond);
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
    }

    private sealed class TestAccess(bool paid) : IFreeExportAccessService
    {
        public Task<FreeExportAccess> AcquireAsync(System.Security.Claims.ClaimsPrincipal principal, string? remoteIp,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Catalog must not acquire an export.");
        public Task<bool> HasPaidAccessAsync(System.Security.Claims.ClaimsPrincipal principal,
            CancellationToken cancellationToken) => Task.FromResult(paid);
    }

    private sealed class RecordingLogger : ILogger<CatalogRequestDiagnostics>
    {
        internal ConcurrentQueue<Dictionary<string, object?>> Entries { get; } = new();
        internal bool ThrowOnLog { get; init; }
        internal int Attempts;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref Attempts);
            if (ThrowOnLog) throw new InvalidOperationException("sink failure");
            Assert.Equal(1801, eventId.Id);
            Assert.Equal(LogLevel.Warning, logLevel);
            Assert.Null(exception);
            Entries.Enqueue(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                .ToDictionary(x => x.Key, x => x.Value));
        }
    }
}
