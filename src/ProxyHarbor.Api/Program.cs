using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
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
    var adminUsername = builder.Configuration["Security:AdminUsername"];
    var adminPassword = builder.Configuration["Security:AdminPassword"];
    var adminKey = builder.Configuration["Security:AdminApiKey"];
    var dataProtectionKeysDirectory = builder.Configuration["Security:DataProtectionKeysDirectory"];
    if (!AdminUsernamePolicy.IsValid(adminUsername))
        throw new InvalidOperationException(
            "В Production Security__AdminUsername должен содержать 3–64 символа A-Z, a-z, 0-9, точку, дефис или подчёркивание.");
    if (!AdminApiKeyPolicy.IsValid(adminKey))
        throw new InvalidOperationException(
            "В Production Security__AdminApiKey должен содержать 24–256 значимых Unicode-символов без управляющих знаков и некорректных surrogate.");
    if (!AdminApiKeyPolicy.IsValid(adminPassword))
        throw new InvalidOperationException(
            "В Production Security__AdminPassword должен содержать 24–256 значимых Unicode-символов без управляющих знаков и некорректных surrogate.");
    if (string.IsNullOrWhiteSpace(dataProtectionKeysDirectory) || !Path.IsPathFullyQualified(dataProtectionKeysDirectory))
        throw new InvalidOperationException(
            "В Production Security__DataProtectionKeysDirectory должен содержать абсолютный путь к постоянному каталогу ключей cookie-сессии.");
    if (!ProductionHostPolicy.IsValid(builder.Configuration["AllowedHosts"]))
        throw new InvalidOperationException(
            "В Production AllowedHosts должен содержать 1–32 явных ASCII host pattern без порта; allow-all '*' запрещён.");
}

builder.Services.AddProxyHarborInfrastructure(builder.Configuration);
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("ProxyHarbor");
var configuredKeysDirectory = builder.Configuration["Security:DataProtectionKeysDirectory"];
if (string.IsNullOrWhiteSpace(configuredKeysDirectory))
    dataProtection.UseEphemeralDataProtectionProvider();
else
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(configuredKeysDirectory));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-";
        options.Password.RequiredLength = 12;
        options.Password.RequiredUniqueChars = 4;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Tokens.PasswordResetTokenProvider = TokenOptions.DefaultProvider;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddSignInManager()
    .AddEntityFrameworkStores<ProxyHarborDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.Name = "ProxyHarbor.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.Path = "/";
        // Постоянная cookie живёт 30 дней и продлевается при активности. Непостоянная
        // cookie всё равно удаляется браузером при завершении сеанса.
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(5));
builder.Services.AddAuthorization();
builder.Services.AddOptions<AccountEmailOptions>().Bind(builder.Configuration.GetSection(AccountEmailOptions.Section))
    .Validate(x => x.Port is >= 1 and <= 65_535, "Email:Port должен быть 1..65535")
    .Validate(x => Uri.TryCreate(x.PublicBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
        "Email:PublicBaseUrl должен быть абсолютным HTTPS URL")
    .ValidateOnStart();
builder.Services.AddSingleton<IAccountEmailSender, SmtpAccountEmailSender>();
builder.Services.AddOptions<PaymentOptions>().Bind(builder.Configuration.GetSection(PaymentOptions.Section))
    .Validate(x => Uri.TryCreate(x.PublicBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
        "Payments:PublicBaseUrl должен быть абсолютным HTTPS URL")
    .Validate(x => x.Products.Values.All(product =>
        !product.Enabled || product.AmountMinor > 0 && product.DurationDays is >= 1 and <= 3660 &&
        product.Currency.Length == 3 && SubscriptionPlans.All.Contains(product.Plan) && product.Plan != SubscriptionPlans.Free),
        "Активные Payments:Products должны иметь положительную цену, платный тариф, ISO currency и срок 1..3660 дней")
    .ValidateOnStart();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IPaymentConfigurationStore, PaymentConfigurationStore>();
builder.Services.AddScoped<ISiteConfigurationStore, SiteConfigurationStore>();
builder.Services.AddScoped<PaymentGatewayClient>();
builder.Services.AddScoped<PaymentSettlementService>();
builder.Services.AddHostedService<PaymentReconciliationWorker>();
builder.Services.AddOptions<TelegramBotHostOptions>()
    .Bind(builder.Configuration.GetSection(TelegramBotHostOptions.Section))
    .Validate(x => Uri.TryCreate(x.PublicBaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo),
        "TelegramBot:PublicBaseUrl должен быть абсолютным HTTPS URL")
    .ValidateOnStart();
builder.Services.AddScoped<ITelegramBotConfigurationStore, TelegramBotConfigurationStore>();
builder.Services.AddSingleton<ITelegramBackupDeliveryResolver, TelegramBackupDeliveryResolver>();
builder.Services.AddSingleton<ITelegramBackupTransport, TelegramBackupTransport>();
builder.Services.AddSingleton<TelegramProxyCandidateCache>();
builder.Services.AddScoped<ITelegramProxyCandidateProvider, TelegramProxyCandidateProvider>();
builder.Services.AddSingleton<TelegramTransportHealth>();
builder.Services.AddSingleton<TelegramProxyHttpClientPool>();
builder.Services.AddSingleton<TelegramOutboundWakeSignal>();
builder.Services.AddScoped<TelegramBotApiClient>();
builder.Services.AddScoped<TelegramDispatchService>();
builder.Services.AddScoped<TelegramUpdateProcessor>();
builder.Services.AddScoped<SubscriptionReminderProcessor>();
builder.Services.AddHostedService<TelegramProvisioningWorker>();
builder.Services.AddHostedService<TelegramOutboundWorker>();
builder.Services.AddHostedService<TelegramPollingWorker>();
builder.Services.AddHostedService<TelegramSubscriptionReminderWorker>();
builder.Services.AddSingleton<ProxyAccessMonitor>();
builder.Services.AddScoped<IFreeExportAccessService, FreeExportAccessService>();
builder.Services.AddScoped<IUserApiTokenService, UserApiTokenService>();
builder.Services.AddOptions<CheckerAgentDeploymentOptions>()
    .Bind(builder.Configuration.GetSection(CheckerAgentDeploymentOptions.Section))
    .Validate(x => Uri.TryCreate(x.PublicBaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment),
        "CheckerAgentDeployment:PublicBaseUrl должен быть корневым HTTPS origin без пути, query и fragment")
    .ValidateOnStart();
builder.Services.AddSingleton<CheckerNodeProvisioner>();
builder.Services.AddHostedService(services => services.GetRequiredService<ProxyAccessMonitor>());
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
        document.Components.SecuritySchemes["AdminSession"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "ProxyHarbor.Session",
            Description = "HttpOnly cookie-сессия, выдаваемая POST /api/v1/auth/login."
        };
        document.Components.SecuritySchemes["UserApiToken"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "ProxyHarbor API token",
            Description = "Персональный токен из профиля. Действует только вместе с активной подпиской."
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
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("AdminSession", context.Document, null)] = []
            });
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<HttpRequestTelemetry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CheckerNodeCredentialCache>();
builder.Services.AddSingleton<IMetricsSnapshotStore, MetricsSnapshotStore>();
builder.Services.AddSingleton<ProxyMetricsSnapshotCache>();
builder.Services.AddHostedService<ProxyMetricsSnapshotRefreshWorker>();
builder.Services.AddSingleton<VpnMetricsSnapshotCache>();
builder.Services.AddHostedService<VpnMetricsSnapshotRefreshWorker>();
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
    options.AddPolicy(PublicOutputCachePolicies.ProxyCatalog, policy => policy
        .With(context => PublicOutputCachePolicies.IsAnonymousFirstPage(context.HttpContext))
        .Expire(PublicOutputCachePolicies.CatalogExpiration)
        .SetVaryByQuery(PublicOutputCachePolicies.ListVaryByQuery)
        .VaryByValue(PublicOutputCachePolicies.CultureKey));
    options.AddPolicy(PublicOutputCachePolicies.VpnCatalog, policy => policy
        .With(context => PublicOutputCachePolicies.IsAnonymousFirstPage(context.HttpContext))
        .Expire(PublicOutputCachePolicies.CatalogExpiration)
        .SetVaryByQuery(PublicOutputCachePolicies.VpnListVaryByQuery)
        .VaryByValue(PublicOutputCachePolicies.CultureKey));
    options.AddPolicy(PublicOutputCachePolicies.Countries, policy => policy
        .Expire(PublicOutputCachePolicies.CatalogExpiration)
        .SetVaryByQuery([])
        .VaryByValue(PublicOutputCachePolicies.CultureKey));
    options.AddPolicy(PublicOutputCachePolicies.SeekFirstPage, policy => policy
        // Произвольные cursor продолжения не должны создавать одноразовые cache entries.
        .With(context => PublicOutputCachePolicies.IsSeekFirstPage(context.HttpContext))
        .Expire(TimeSpan.FromSeconds(10))
        .SetVaryByQuery(PublicOutputCachePolicies.SeekVaryByQuery));
    options.AddPolicy(PublicOutputCachePolicies.Summary, policy => policy
        .Expire(PublicOutputCachePolicies.SummaryExpiration)
        // Неизвестные query-параметры не должны создавать неограниченное число cache keys.
        .SetVaryByQuery([]));
    options.AddPolicy(PublicOutputCachePolicies.Metrics, policy => policy
        // Два последовательных стандартных scrape используют один согласованный
        // снимок вместо двух дорогих проходов таблицы Proxies.
        .Expire(PublicOutputCachePolicies.MetricsExpiration)
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
            .AllowCredentials()
            .WithExposedHeaders(
                "Content-Disposition", "X-Export-Limit", "X-Export-Offset", "X-Export-Cursor",
                "X-Export-Truncated", "X-Next-Offset", "X-Next-Cursor", "X-Access-Tier",
                "X-Free-Cooldown", "Link", "Retry-After");
}));
builder.Services.AddRateLimiter(x =>
{
    x.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    x.OnRejected = async (context, token) =>
    {
        // Не все встроенные limiter lease обязаны возвращать RetryAfter metadata.
        // Контракт API всё равно обещает header для 429, поэтому используем bounded
        // fallback и никогда не отправляем клиенту бессмысленный ноль.
        var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 1;
        context.HttpContext.Response.Headers.RetryAfter =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
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
    // Один браузер обычно отправляет один beacon на загрузку страницы. Отдельный
    // limiter не позволяет подделанным событиям вытеснять публичный API-трафик.
    x.AddPolicy("telemetry", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Панель выполняет несколько параллельных чтений и обновляет диагностику в фоне.
    // Считаем лимит на конкретную защищённую учётную запись, а не на общий IP
    // офиса/NAT, и оставляем запас для переходов между административными разделами.
    x.AddPolicy("admin", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
            context.User.Identity?.Name ??
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    x.AddPolicy("account-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    x.AddPolicy("account-register", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
    x.AddPolicy("account-recovery", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
    x.AddPolicy("account", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    x.AddPolicy("payment-webhook", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    x.AddPolicy("telegram-webhook", context => RateLimitPartition.GetTokenBucketLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 300,
            TokensPerPeriod = 100,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            AutoReplenishment = true,
            QueueLimit = 0
        }));
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
var supportedCultures = SupportedLanguages.All.Select(language => CultureInfo.GetCultureInfo(SupportedLanguages.CultureName(language))).ToArray();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("ru-RU");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    // UI хранит простой стабильный код; Accept-Language остаётся запасным источником.
    options.AddInitialRequestCultureProvider(new CustomRequestCultureProvider(context =>
    {
        var raw = context.Request.Cookies["ProxyHarbor.Language"];
        if (string.IsNullOrWhiteSpace(raw)) return Task.FromResult<ProviderCultureResult?>(null);
        return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(SupportedLanguages.CultureName(raw)));
    }));
});
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
foreach (var network in ForwardedNetworkPolicy.ExpandForRuntime(knownProxyNetworks))
    forwardedHeaders.KnownIPNetworks.Add(network);
app.UseForwardedHeaders(forwardedHeaders);
app.UseRequestLocalization();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.ContentLanguage = CultureInfo.CurrentUICulture.Name;
        return Task.CompletedTask;
    });
    await next();
});
app.UseMiddleware<SecurityHeadersMiddleware>();
// Middleware стоит снаружи exception handler: обработанный 500 уже виден в итоговом SLI.
app.UseMiddleware<HttpRequestTelemetryMiddleware>();
app.UseExceptionHandler();
app.UseResponseCompression();
app.UseRouting();
app.UseCors("frontend");
app.UseAuthentication();
// Пользовательский bearer-токен формирует обычный principal, но только если его
// владелец активен и подписка ещё действует. Полный токен в базе не хранится.
app.UseMiddleware<UserApiTokenMiddleware>();
// API key должен сформировать административный principal до выбора partition
// rate limiter; cookie-аутентификация уже выполнена предыдущим middleware.
app.UseMiddleware<AdminApiKeyMiddleware>();
app.UseMiddleware<ProxyAccessMiddleware>();
app.UseRateLimiter();
// Стандартный Authorization middleware проверяет endpoint-level политики для
// cookie-сессии или сформированного выше automation API key principal.
app.UseAuthorization();
// Output cache работает после authentication/authorization: публичные cache policies
// видят итоговый principal и никогда не разделяют entitlement-aware ответы пользователей.
app.UseOutputCache();
app.MapOpenApi().CacheOutput(PublicOutputCachePolicies.Summary).RequireRateLimiting("public");
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
await IdentitySeeder.InitializeAsync(app.Services, builder.Configuration);

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
