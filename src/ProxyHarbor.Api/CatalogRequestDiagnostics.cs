namespace ProxyHarbor.Api;

/// <summary>Безопасные замеры медленных запросов каталога без URL, SQL, IP и токенов.</summary>
public sealed class CatalogRequestDiagnostics(ILogger<CatalogRequestDiagnostics> logger, TimeProvider clock)
{
    internal static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);
    private readonly long[] lastLogged = [long.MinValue, long.MinValue];
    internal Func<CatalogRuntimeSnapshot> CaptureRuntime { get; init; } = CatalogRuntimeSnapshot.Capture;
    private static readonly Action<ILogger, string, double, double, double, double, double, Exception?> SlowRequest =
        LoggerMessage.Define<string, double, double, double, double, double>(LogLevel.Warning,
            new EventId(1801, "SlowCatalogRequest"),
            "Slow catalog {Catalog}: total {TotalMs} ms, controller {ControllerMs} ms, count {CountMs} ms, access {AccessMs} ms, selection {SelectionMs} ms.");
    private static readonly Action<ILogger, string, double, int, int, long, long, Exception?> RuntimePressure =
        LoggerMessage.Define<string, double, int, int, long, long>(LogLevel.Warning,
            new EventId(1802, "SlowCatalogRuntime"),
            "Slow catalog runtime {Catalog}: GC pause delta {GcPauseMs} ms, gen2 delta {Gen2Collections}, thread pool threads {ThreadPoolThreads}, queued at start {PendingAtStart}, queued at end {PendingAtEnd}.");

    internal CatalogRequestTrace? Begin(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)) return null;
        var catalog = context.Request.Path.Equals("/api/v1/proxies") ? 0 :
            context.Request.Path.Equals("/api/v1/vpn") ? 1 : -1;
        if (catalog < 0) return null;
        var trace = new CatalogRequestTrace(clock, catalog) { RuntimeAtStart = CaptureRuntime() };
        context.Features.Set(trace);
        return trace;
    }

    internal void Complete(CatalogRequestTrace? trace)
    {
        if (trace is null) return;
        var now = clock.GetTimestamp();
        var elapsed = clock.GetElapsedTime(trace.StartedAt, now);
        if (elapsed < SlowThreshold) return;
        // Не больше одной записи на каталог за 30 секунд даже под конкурентной
        // нагрузкой. Метрики всех запросов остаются в HttpRequestTelemetry.
        while (true)
        {
            var observed = Interlocked.Read(ref lastLogged[trace.Catalog]);
            if (observed != long.MinValue && clock.GetElapsedTime(observed, now) < LogInterval) return;
            if (Interlocked.CompareExchange(ref lastLogged[trace.Catalog], now, observed) == observed) break;
        }
        try
        {
            // Capture before logging: a slow log sink must not contaminate runtime evidence.
            var runtime = CaptureRuntime();
            SlowRequest(logger, trace.Catalog == 0 ? "proxies" : "vpn", elapsed.TotalMilliseconds,
                trace.Milliseconds(CatalogReadPhase.Controller), trace.Milliseconds(CatalogReadPhase.Count),
                trace.Milliseconds(CatalogReadPhase.Access), trace.Milliseconds(CatalogReadPhase.Selection), null);
            RuntimePressure(logger, trace.Catalog == 0 ? "proxies" : "vpn",
                Math.Max(0, (runtime.GcPause - trace.RuntimeAtStart.GcPause).TotalMilliseconds),
                Math.Max(0, runtime.Gen2Collections - trace.RuntimeAtStart.Gen2Collections),
                runtime.ThreadPoolThreads, trace.RuntimeAtStart.PendingWorkItems, runtime.PendingWorkItems, null);
        }
        catch (Exception)
        {
            // Диагностический logger не должен менять ответ, скрывать исходную
            // ошибку или прерывать обработку запроса при отказе log sink.
        }
    }
}

// Process-wide counters: these show overlap with a request, NOT per-request causation.
// No process enumeration, stack capture, allocations scan or forced collection.
internal readonly record struct CatalogRuntimeSnapshot(
    TimeSpan GcPause, int Gen2Collections, int ThreadPoolThreads, long PendingWorkItems)
{
    internal static CatalogRuntimeSnapshot Capture() => new(
        GC.GetTotalPauseDuration(), GC.CollectionCount(2), ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);
}

internal enum CatalogReadPhase { Controller, Count, Access, Selection }

/// <summary>Суммирует завершённые фазы, включая повторные попытки EF execution strategy.</summary>
internal sealed class CatalogRequestTrace(TimeProvider clock, int catalog)
{
    private readonly TimeProvider timer = clock;
    private readonly long[] elapsedTicks = new long[4];
    internal int Catalog { get; } = catalog;
    internal long StartedAt { get; } = clock.GetTimestamp();
    internal CatalogRuntimeSnapshot RuntimeAtStart { get; init; }
    internal double Milliseconds(CatalogReadPhase phase) =>
        TimeSpan.FromTicks(Interlocked.Read(ref elapsedTicks[(int)phase])).TotalMilliseconds;

    internal static Scope Measure(HttpContext? context, CatalogReadPhase phase) =>
        new(context?.Features.Get<CatalogRequestTrace>(), phase);

    internal readonly struct Scope : IDisposable
    {
        private readonly CatalogRequestTrace? trace;
        private readonly CatalogReadPhase phase;
        private readonly long startedAt;
        internal Scope(CatalogRequestTrace? trace, CatalogReadPhase phase)
        {
            this.trace = trace;
            this.phase = phase;
            startedAt = trace is null ? 0 : trace.timer.GetTimestamp();
        }
        public void Dispose()
        {
            if (trace is not null)
                Interlocked.Add(ref trace.elapsedTicks[(int)phase],
                    Math.Max(0, trace.timer.GetElapsedTime(startedAt).Ticks));
        }
    }
}
