using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
// Docker secrets загружаются до validation/options и не присутствуют в
// container environment. При обычном dotnet run этот шаг является no-op.
RuntimeSecretConfiguration.Apply(builder.Configuration);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

if (!builder.Environment.IsDevelopment())
{
    var adminKey = builder.Configuration["Security:AdminApiKey"];
    if (!AdminApiKeyPolicy.IsValid(adminKey))
        throw new InvalidOperationException(
            "В Production Security__AdminApiKey должен содержать 24–256 значимых Unicode-символов без управляющих знаков и некорректных surrogate.");
    if (!ProductionHostPolicy.IsValid(builder.Configuration["AllowedHosts"]))
        throw new InvalidOperationException(
            "В Production AllowedHosts должен содержать 1–32 явных ASCII host pattern без порта; allow-all '*' запрещён.");
}

builder.Services.AddProxyHarborInfrastructure(builder.Configuration);
builder.Services.AddControllers().AddJsonOptions(x =>
    x.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["AdminApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Admin-Key",
            Description = "Административный ключ ProxyHarbor; передавайте только по HTTPS."
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        ExportOpenApiContract.Apply(operation, context.Description.RelativePath);
        if (context.Description.RelativePath?.StartsWith("api/v1/admin", StringComparison.OrdinalIgnoreCase) == true)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("AdminApiKey", context.Document, null)] = []
            });
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<HttpRequestTelemetry>();
builder.Services.AddResponseCompression(x =>
{
    x.EnableForHttps = true;
    x.Providers.Add<BrotliCompressionProvider>();
    x.Providers.Add<GzipCompressionProvider>();
    x.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/xml", "text/csv"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(x => x.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.AddOutputCache(options =>
{
    options.SizeLimit = 32 * 1024 * 1024;
    options.MaximumBodySize = 2 * 1024 * 1024;
    options.AddPolicy("public-list", policy => policy
        .Expire(TimeSpan.FromSeconds(10))
        .SetVaryByQuery("protocol", "maxLatencyMs", "minSuccessRate", "page", "pageSize"));
    options.AddPolicy(PublicOutputCachePolicies.SeekFirstPage, policy => policy
        // Произвольные cursor продолжения не должны создавать одноразовые cache entries.
        .With(context => PublicOutputCachePolicies.IsSeekFirstPage(context.HttpContext))
        .Expire(TimeSpan.FromSeconds(10))
        .SetVaryByQuery("protocol", "maxLatencyMs", "minSuccessRate", "pageSize"));
    options.AddPolicy("public-summary", policy => policy
        .Expire(TimeSpan.FromSeconds(15))
        // Неизвестные query-параметры не должны создавать неограниченное число cache keys.
        .SetVaryByQuery([]));
});
var configuredCorsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>();
var requestedCorsOrigins = configuredCorsOrigins ??
    (builder.Environment.IsDevelopment() ? ["http://localhost:5173"] : []);
if (!CorsOriginPolicy.TryNormalize(
    requestedCorsOrigins,
    allowHttp: builder.Environment.IsDevelopment(),
    out var corsOrigins))
    throw new InvalidOperationException(
        "Cors__Origins должен содержать не более 32 HTTPS origins без credentials, path, query и fragment; HTTP разрешён только в Development.");
builder.Services.AddCors(x => x.AddPolicy("frontend", policy =>
{
    if (corsOrigins.Length > 0)
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()
            .WithExposedHeaders(
                "Content-Disposition", "X-Export-Limit", "X-Export-Offset", "X-Export-Cursor",
                "X-Export-Truncated", "X-Next-Offset", "X-Next-Cursor");
}));
builder.Services.AddRateLimiter(x =>
{
    x.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    x.OnRejected = async (context, token) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Слишком много запросов",
            Detail = "Повторите запрос после указанного в Retry-After интервала.",
            Status = StatusCodes.Status429TooManyRequests
        }, token);
    };
    x.AddPolicy("public", context => RateLimitPartition.GetSlidingWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        }));
    x.AddPolicy("admin", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    x.AddPolicy("export", context => RateLimitPartition.GetTokenBucketLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 5,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0
        }));
});
builder.Services.Configure<ApiBehaviorOptions>(x => x.SuppressMapClientErrors = false);
if (!ForwardedNetworkPolicy.TryParse(
    builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>(),
    out var knownProxyNetworks))
    throw new InvalidOperationException(
        "ForwardedHeaders__KnownNetworks должен содержать не более 32 канонических CIDR; минимальная маска IPv4 /8, IPv6 /24.");

var app = builder.Build();
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
    RequireHeaderSymmetry = true
};
foreach (var network in knownProxyNetworks) forwardedHeaders.KnownIPNetworks.Add(network);
app.UseForwardedHeaders(forwardedHeaders);
app.UseMiddleware<SecurityHeadersMiddleware>();
// Middleware стоит снаружи exception handler: обработанный 500 уже виден в итоговом SLI.
app.UseMiddleware<HttpRequestTelemetryMiddleware>();
app.UseExceptionHandler();
app.UseResponseCompression();
app.UseRouting();
app.UseCors("frontend");
app.UseRateLimiter();
app.UseOutputCache();
app.UseMiddleware<AdminApiKeyMiddleware>();
app.MapOpenApi().CacheOutput("public-summary").RequireRateLimiting("public");
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", async (DatabaseReadinessProbe probe, CancellationToken token) =>
{
    return await probe.CheckAsync(token)
        ? Results.Ok(new { status = "healthy", time = DateTimeOffset.UtcNow })
        : Results.Problem("database or schema unavailable", statusCode: 503);
}).RequireRateLimiting("public");
app.MapGet("/healthz", () => Results.Redirect("/health/ready"));

var dbFactory = app.Services.GetRequiredService<IDbContextFactory<ProxyHarborDbContext>>();
string runtimeConnectionString;
await using (var connectionDb = await dbFactory.CreateDbContextAsync())
{
    runtimeConnectionString = connectionDb.Database.GetConnectionString()
        ?? throw new InvalidOperationException("Не найдена строка подключения PostgreSQL.");
}
// Shared lifetime-lease разрешает горизонтальное масштабирование API, но не позволяет
// destructive restore начаться до остановки последней реплики. Lease берётся до migrations/seed,
// поэтому новая реплика также не может стартовать посередине восстановления.
await using var runtimeLease = await DatabaseRuntimeGate.TryAcquireApiLeaseAsync(
    runtimeConnectionString, CancellationToken.None)
    ?? throw new InvalidOperationException(
        "База данных занята восстановлением; запуск API отменён до завершения restore.");
await using (var startupDb = await dbFactory.CreateDbContextAsync())
{
    await DatabaseSeeder.InitializeAsync(startupDb);
}

var runtimeLeaseLost = LoggerMessage.Define(
    LogLevel.Critical,
    new EventId(1401, "RuntimeLeaseLost"),
    "Потеряна PostgreSQL lifetime-lock session; API выполняет controlled shutdown.");
var runtimeLeaseMonitor = RuntimeLeaseMonitor.RunAsync(
    runtimeLease, app.Lifetime, app.Logger, runtimeLeaseLost);
try { await app.RunAsync(); }
finally
{
    // Гарантирует завершение timer и heartbeat перед освобождением самой lease.
    app.Lifetime.StopApplication();
    await runtimeLeaseMonitor;
}

/// <summary>Маркер для интеграционных тестов WebApplicationFactory.</summary>
public partial class Program;

/// <summary>Контролирует живость owning PostgreSQL lifetime-lock session API-реплики.</summary>
internal static class RuntimeLeaseMonitor
{
    internal static Task RunAsync(
        DatabaseRuntimeLease lease,
        IHostApplicationLifetime lifetime,
        ILogger logger,
        Action<ILogger, Exception?> runtimeLeaseLost) =>
        RunCoreAsync(
            lease.VerifyAsync,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            lifetime,
            logger,
            runtimeLeaseLost);

    /// <summary>
    /// Тестируемое ядро принимает verify delegate и интервалы. Потеря lease всегда инициирует
    /// controlled shutdown; отказ logging provider не может остановить этот переход или
    /// превратить monitor task во вторичную ошибку поверх host failure.
    /// </summary>
    internal static async Task RunCoreAsync(
        Func<CancellationToken, Task> verifyAsync,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        IHostApplicationLifetime lifetime,
        ILogger logger,
        Action<ILogger, Exception?> runtimeLeaseLost)
    {
        ArgumentNullException.ThrowIfNull(verifyAsync);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runtimeLeaseLost);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);

        using var timer = new PeriodicTimer(heartbeatInterval);
        var stopping = lifetime.ApplicationStopping;
        try
        {
            while (await timer.WaitForNextTickAsync(stopping))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(heartbeatTimeout);
                try { await verifyAsync(timeout.Token); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    ReportFailureAndStop(lifetime, logger, runtimeLeaseLost, exception);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    /// <summary>Shutdown выполняется даже при неисправном logging provider.</summary>
    private static void ReportFailureAndStop(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        Action<ILogger, Exception?> runtimeLeaseLost,
        Exception failure)
    {
        try
        {
            runtimeLeaseLost(logger, failure);
        }
        catch (Exception loggingFailure)
        {
            // Fallback не включает message/connection string и сам остаётся best-effort.
            try
            {
                Console.Error.WriteLine(
                    $"Не удалось записать RuntimeLeaseLost ({loggingFailure.GetType().Name}); API останавливается.");
            }
            catch (Exception) { }
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
