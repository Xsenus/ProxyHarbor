namespace ProxyHarbor.Api;

/// <summary>Безопасные замеры медленных запросов каталога без URL, SQL, IP и токенов.</summary>
public sealed class CatalogRequestDiagnostics(ILogger<CatalogRequestDiagnostics> logger, TimeProvider clock)
{
    internal static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);
    private readonly long[] lastLogged = [long.MinValue, long.MinValue];
    private static readonly Action<ILogger, string, double, double, double, double, double, Exception?> SlowRequest =
        LoggerMessage.Define<string, double, double, double, double, double>(LogLevel.Warning,
            new EventId(1801, "SlowCatalogRequest"),
            "Slow catalog {Catalog}: total {TotalMs} ms, controller {ControllerMs} ms, count {CountMs} ms, access {AccessMs} ms, selection {SelectionMs} ms.");

    internal CatalogRequestTrace? Begin(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)) return null;
        var catalog = context.Request.Path.Equals("/api/v1/proxies") ? 0 :
            context.Request.Path.Equals("/api/v1/vpn") ? 1 : -1;
        if (catalog < 0) return null;
        var trace = new CatalogRequestTrace(clock, catalog);
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
            SlowRequest(logger, trace.Catalog == 0 ? "proxies" : "vpn", elapsed.TotalMilliseconds,
                trace.Milliseconds(CatalogReadPhase.Controller), trace.Milliseconds(CatalogReadPhase.Count),
                trace.Milliseconds(CatalogReadPhase.Access), trace.Milliseconds(CatalogReadPhase.Selection), null);
        }
        catch (Exception)
        {
            // Диагностический logger не должен менять ответ, скрывать исходную
            // ошибку или прерывать обработку запроса при отказе log sink.
        }
    }
}

internal enum CatalogReadPhase { Controller, Count, Access, Selection }

/// <summary>Суммирует завершённые фазы, включая повторные попытки EF execution strategy.</summary>
internal sealed class CatalogRequestTrace(TimeProvider clock, int catalog)
{
    private readonly TimeProvider timer = clock;
    private readonly long[] elapsedTicks = new long[4];
    internal int Catalog { get; } = catalog;
    internal long StartedAt { get; } = clock.GetTimestamp();
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
