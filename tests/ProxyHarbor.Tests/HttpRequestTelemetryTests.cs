using System.Text;
using Microsoft.AspNetCore.Http;
using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует bounded labels, histogram semantics и все terminal middleware outcomes.</summary>
public sealed class HttpRequestTelemetryTests
{
    [Theory]
    [InlineData("/api/v1/proxies", 0)]
    [InlineData("/api/v1/proxies/seek", 0)]
    [InlineData("/api/v1/export/json", 1)]
    [InlineData("/api/v1/stats", 2)]
    [InlineData("/api/v1/sources", 3)]
    [InlineData("/api/v1/admin/backup", 4)]
    [InlineData("/health/ready", 5)]
    [InlineData("/healthz", 5)]
    [InlineData("/openapi/v1.json", 6)]
    [InlineData("/untrusted/arbitrary/value", 7)]
    public void ClassifierMapsArbitraryPathsToBoundedRouteGroups(string path, int expected)
    {
        Assert.Equal((HttpRouteGroup)expected, HttpRequestTelemetryMiddleware.Classify(path));
    }

    [Theory]
    [InlineData("/metrics")]
    [InlineData("/metrics/anything")]
    public void ClassifierExcludesSelfScrapes(string path)
    {
        Assert.Null(HttpRequestTelemetryMiddleware.Classify(path));
    }

    [Fact]
    public void PrometheusHistogramIsCumulativeAndUsesOnlyClosedLabels()
    {
        var telemetry = new HttpRequestTelemetry();
        telemetry.Record(HttpRouteGroup.Proxies, 200, TimeSpan.FromMilliseconds(40));
        telemetry.Record(HttpRouteGroup.Proxies, 503, TimeSpan.FromSeconds(3));

        var output = new StringBuilder();
        telemetry.AppendPrometheus(output);
        var metrics = output.ToString();

        Assert.Contains("proxyharbor_http_requests_total{route=\"proxies\",status=\"2xx\"} 1", metrics);
        Assert.Contains("proxyharbor_http_requests_total{route=\"proxies\",status=\"5xx\"} 1", metrics);
        Assert.Contains("proxyharbor_http_request_duration_seconds_bucket{route=\"proxies\",le=\"0.05\"} 1", metrics);
        Assert.Contains("proxyharbor_http_request_duration_seconds_bucket{route=\"proxies\",le=\"2\"} 1", metrics);
        Assert.Contains("proxyharbor_http_request_duration_seconds_bucket{route=\"proxies\",le=\"5\"} 2", metrics);
        Assert.Contains("proxyharbor_http_request_duration_seconds_bucket{route=\"proxies\",le=\"+Inf\"} 2", metrics);
        Assert.Contains("proxyharbor_http_request_duration_seconds_sum{route=\"proxies\"} 3.04", metrics);
        Assert.Contains("proxyharbor_http_request_duration_seconds_count{route=\"proxies\"} 2", metrics);
        Assert.DoesNotContain("untrusted", metrics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MiddlewareRecordsResponseFailureUnhandledFailureAndClientAbort()
    {
        var telemetry = new HttpRequestTelemetry();
        await InvokeAsync(telemetry, "/api/v1/stats", context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync(telemetry, "/api/v1/proxies", _ => throw new InvalidOperationException("safe-test")));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeAsync(telemetry, "/api/v1/export/txt", _ => Task.FromCanceled(cancellation.Token), cancellation.Token));

        var output = new StringBuilder();
        telemetry.AppendPrometheus(output);
        var metrics = output.ToString();
        Assert.Contains("proxyharbor_http_requests_total{route=\"stats\",status=\"5xx\"} 1", metrics);
        Assert.Contains("proxyharbor_http_requests_total{route=\"proxies\",status=\"5xx\"} 1", metrics);
        Assert.Contains("proxyharbor_http_requests_total{route=\"export\",status=\"4xx\"} 1", metrics);
    }

    [Fact]
    public async Task MiddlewareDoesNotRecordMetricsScrape()
    {
        var telemetry = new HttpRequestTelemetry();
        await InvokeAsync(telemetry, "/metrics", _ => Task.CompletedTask);
        var output = new StringBuilder();
        telemetry.AppendPrometheus(output);
        Assert.DoesNotContain("} 1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentRecordsRemainExactWithoutLocksOrLostUpdates()
    {
        var telemetry = new HttpRequestTelemetry();
        Parallel.For(0, 10_000, _ =>
            telemetry.Record(HttpRouteGroup.Sources, 200, TimeSpan.FromMilliseconds(75)));

        var output = new StringBuilder();
        telemetry.AppendPrometheus(output);
        var metrics = output.ToString();
        Assert.Contains("proxyharbor_http_requests_total{route=\"sources\",status=\"2xx\"} 10000", metrics);
        Assert.Contains("proxyharbor_http_request_duration_seconds_bucket{route=\"sources\",le=\"0.1\"} 10000", metrics);
        Assert.Contains("proxyharbor_http_request_duration_seconds_count{route=\"sources\"} 10000", metrics);
    }

    private static Task InvokeAsync(
        HttpRequestTelemetry telemetry,
        string path,
        RequestDelegate next,
        CancellationToken cancellationToken = default)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.RequestAborted = cancellationToken;
        return new HttpRequestTelemetryMiddleware(next).InvokeAsync(context, telemetry);
    }
}
