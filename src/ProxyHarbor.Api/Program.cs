using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment() && (builder.Configuration["Security:AdminApiKey"]?.Length ?? 0) < 24)
    throw new InvalidOperationException("В Production параметр Security__AdminApiKey должен содержать не менее 24 символов.");

builder.Services.AddProxyHarborInfrastructure(builder.Configuration);
builder.Services.AddControllers().AddJsonOptions(x =>
    x.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
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
    options.AddPolicy("public-summary", policy => policy
        .Expire(TimeSpan.FromSeconds(15))
        // Неизвестные query-параметры не должны создавать неограниченное число cache keys.
        .SetVaryByQuery([]));
});
builder.Services.AddCors(x => x.AddPolicy("frontend", policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod()));
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

var app = builder.Build();
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeaders.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
app.UseForwardedHeaders(forwardedHeaders);
app.UseExceptionHandler();
app.UseResponseCompression();
app.UseRouting();
app.UseCors("frontend");
app.UseRateLimiter();
app.UseOutputCache();
app.UseMiddleware<AdminApiKeyMiddleware>();
app.MapOpenApi();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", async (IDbContextFactory<ProxyHarborDbContext> factory, CancellationToken token) =>
{
    await using var db = await factory.CreateDbContextAsync(token);
    return await db.Database.CanConnectAsync(token)
        ? Results.Ok(new { status = "healthy", time = DateTimeOffset.UtcNow })
        : Results.Problem("database unavailable", statusCode: 503);
});
app.MapGet("/healthz", () => Results.Redirect("/health/ready"));

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProxyHarborDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await DatabaseSeeder.InitializeAsync(db);
}

app.Run();

/// <summary>Маркер для интеграционных тестов WebApplicationFactory.</summary>
public partial class Program;
