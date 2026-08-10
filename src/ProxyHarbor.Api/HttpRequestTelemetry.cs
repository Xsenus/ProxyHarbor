using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ProxyHarbor.Api;

/// <summary>
/// Хранит bounded in-process SLI HTTP API. Набор route/status labels закрыт enum-ами,
/// поэтому произвольный URL, IP или User-Agent никогда не увеличивает cardinality Prometheus.
/// </summary>
public sealed class HttpRequestTelemetry
{
    private static readonly double[] DurationBuckets = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10];
    private static readonly string[] RouteLabels = ["proxies", "export", "stats", "sources", "admin", "health", "openapi", "other"];
    private static readonly string[] StatusLabels = ["1xx", "2xx", "3xx", "4xx", "5xx", "other"];

    private readonly long[,] _requests = new long[RouteLabels.Length, StatusLabels.Length];
    private readonly long[,] _durationBuckets = new long[RouteLabels.Length, DurationBuckets.Length];
    private readonly long[] _durationCounts = new long[RouteLabels.Length];
    private readonly long[] _durationTicks = new long[RouteLabels.Length];

    /// <summary>Атомарно учитывает один законченный запрос без allocations и блокировок.</summary>
    internal void Record(HttpRouteGroup route, int statusCode, TimeSpan elapsed)
    {
        var routeIndex = (int)route;
        var statusIndex = StatusIndex(statusCode);
        var safeTicks = Math.Max(0, elapsed.Ticks);
        var seconds = TimeSpan.FromTicks(safeTicks).TotalSeconds;

        Interlocked.Increment(ref _requests[routeIndex, statusIndex]);
        Interlocked.Increment(ref _durationCounts[routeIndex]);
        Interlocked.Add(ref _durationTicks[routeIndex], safeTicks);
        // От больших bucket к малым: даже попавший внутрь scrape никогда не увидит
        // нижний cumulative bucket больше следующего верхнего.
        for (var index = DurationBuckets.Length - 1; index >= 0; index--)
        {
            if (seconds <= DurationBuckets[index])
                Interlocked.Increment(ref _durationBuckets[routeIndex, index]);
            else
                break;
        }
    }

    /// <summary>Добавляет согласованный по типам Prometheus exposition к общему ответу `/metrics`.</summary>
    internal void AppendPrometheus(StringBuilder output)
    {
        output.AppendLine("# HELP proxyharbor_http_requests_total Completed HTTP requests by bounded route group and status class.");
        output.AppendLine("# TYPE proxyharbor_http_requests_total counter");
        for (var route = 0; route < RouteLabels.Length; route++)
        {
            for (var status = 0; status < StatusLabels.Length; status++)
            {
                output.Append("proxyharbor_http_requests_total{route=\"").Append(RouteLabels[route])
                    .Append("\",status=\"").Append(StatusLabels[status]).Append("\"} ")
                    .AppendLine(Interlocked.Read(ref _requests[route, status]).ToString(CultureInfo.InvariantCulture));
            }
        }

        output.AppendLine("# HELP proxyharbor_http_request_duration_seconds Completed HTTP request duration by bounded route group.");
        output.AppendLine("# TYPE proxyharbor_http_request_duration_seconds histogram");
        for (var route = 0; route < RouteLabels.Length; route++)
        {
            for (var bucket = 0; bucket < DurationBuckets.Length; bucket++)
            {
                output.Append("proxyharbor_http_request_duration_seconds_bucket{route=\"").Append(RouteLabels[route])
                    .Append("\",le=\"").Append(DurationBuckets[bucket].ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("\"} ").AppendLine(Interlocked.Read(ref _durationBuckets[route, bucket])
                        .ToString(CultureInfo.InvariantCulture));
            }

            var count = Interlocked.Read(ref _durationCounts[route]);
            var ticks = Interlocked.Read(ref _durationTicks[route]);
            output.Append("proxyharbor_http_request_duration_seconds_bucket{route=\"").Append(RouteLabels[route])
                .Append("\",le=\"+Inf\"} ").AppendLine(count.ToString(CultureInfo.InvariantCulture));
            output.Append("proxyharbor_http_request_duration_seconds_sum{route=\"").Append(RouteLabels[route])
                .Append("\"} ").AppendLine(TimeSpan.FromTicks(Math.Max(0, ticks)).TotalSeconds
                    .ToString("0.#######", CultureInfo.InvariantCulture));
            output.Append("proxyharbor_http_request_duration_seconds_count{route=\"").Append(RouteLabels[route])
                .Append("\"} ").AppendLine(count.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static int StatusIndex(int statusCode) => statusCode switch
    {
        >= 100 and <= 199 => 0,
        >= 200 and <= 299 => 1,
        >= 300 and <= 399 => 2,
        >= 400 and <= 499 => 3,
        >= 500 and <= 599 => 4,
        _ => 5
    };
}

/// <summary>Низкокардинальная классификация всех HTTP surfaces процесса.</summary>
internal enum HttpRouteGroup
{
    Proxies,
    Export,
    Stats,
    Sources,
    Admin,
    Health,
    OpenApi,
    Other
}

/// <summary>Измеряет завершённые запросы; scrape `/metrics` исключён, чтобы не наблюдать сам себя.</summary>
public sealed class HttpRequestTelemetryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, HttpRequestTelemetry telemetry)
    {
        var route = Classify(context.Request.Path);
        if (route is null)
        {
            await next(context);
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var statusCode = StatusCodes.Status200OK;
        try
        {
            await next(context);
            statusCode = context.Response.StatusCode;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 499 не отправляется клиенту framework-ом, но корректно классифицирует client abort как 4xx SLI.
            statusCode = 499;
            throw;
        }
        catch
        {
            statusCode = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            telemetry.Record(route.Value, statusCode, Stopwatch.GetElapsedTime(startedAt));
        }
    }

    /// <summary>Сводит любые пользовательские path к восьми заранее известным значениям.</summary>
    internal static HttpRouteGroup? Classify(PathString path)
    {
        if (path.StartsWithSegments("/metrics")) return null;
        if (path.StartsWithSegments("/api/v1/export")) return HttpRouteGroup.Export;
        if (path.StartsWithSegments("/api/v1/proxies")) return HttpRouteGroup.Proxies;
        if (path.StartsWithSegments("/api/v1/stats")) return HttpRouteGroup.Stats;
        if (path.StartsWithSegments("/api/v1/sources")) return HttpRouteGroup.Sources;
        if (path.StartsWithSegments("/api/v1/admin")) return HttpRouteGroup.Admin;
        if (path.StartsWithSegments("/health") || path.StartsWithSegments("/healthz")) return HttpRouteGroup.Health;
        if (path.StartsWithSegments("/openapi")) return HttpRouteGroup.OpenApi;
        return HttpRouteGroup.Other;
    }
}
