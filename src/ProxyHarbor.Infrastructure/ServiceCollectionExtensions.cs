using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ProxyHarbor.Infrastructure;

/// <summary>Единая точка регистрации инфраструктуры ProxyHarbor.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Регистрирует PostgreSQL, сетевые клиенты и фоновые процессы.</summary>
    public static IServiceCollection AddProxyHarborInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__Postgres.");
        services.AddOptions<CollectorOptions>().Bind(configuration.GetSection(CollectorOptions.Section))
            .Validate(x => x.CollectionIntervalMinutes is >= 1 and <= 10_080, "CollectionIntervalMinutes: 1..10080")
            .Validate(x => x.ValidationIntervalMinutes is >= 1 and <= 1_440, "ValidationIntervalMinutes: 1..1440")
            .Validate(x => x.PublicFreshnessMinutes is >= 2 and <= 2_880, "PublicFreshnessMinutes: 2..2880")
            .Validate(x => x.PublicFreshnessMinutes >= x.ValidationIntervalMinutes, "PublicFreshnessMinutes не может быть меньше ValidationIntervalMinutes")
            .Validate(x => x.DeadRetryBaseMinutes is >= 1 and <= 1_440, "DeadRetryBaseMinutes: 1..1440")
            .Validate(x => x.DeadRetryMaxHours is >= 1 and <= 720, "DeadRetryMaxHours: 1..720")
            .Validate(x => TimeSpan.FromMinutes(x.DeadRetryBaseMinutes) <= TimeSpan.FromHours(x.DeadRetryMaxHours), "DeadRetryBaseMinutes не может превышать DeadRetryMaxHours")
            .Validate(x => x.ValidationConcurrency is >= 1 and <= 1_000, "ValidationConcurrency: 1..1000")
            .Validate(x => x.ValidationBatchSize is >= 1 and <= 100_000, "ValidationBatchSize: 1..100000")
            .Validate(x => x.ProbeTimeoutSeconds is >= 1 and <= 120, "ProbeTimeoutSeconds: 1..120")
            .Validate(x => x.SourceTimeoutSeconds is >= 2 and <= 300, "SourceTimeoutSeconds: 2..300")
            .Validate(x => x.SourceConcurrency is >= 1 and <= 32, "SourceConcurrency: 1..32")
            .Validate(x => x.SourceRetryCount is >= 0 and <= 5, "SourceRetryCount: 0..5")
            .Validate(x => x.SourceFailureBackoffBaseMinutes is >= 1 and <= 1_440, "SourceFailureBackoffBaseMinutes: 1..1440")
            .Validate(x => x.SourceFailureBackoffMaxHours is >= 1 and <= 720, "SourceFailureBackoffMaxHours: 1..720")
            .Validate(x => TimeSpan.FromMinutes(x.SourceFailureBackoffBaseMinutes) <= TimeSpan.FromHours(x.SourceFailureBackoffMaxHours),
                "SourceFailureBackoffBaseMinutes не может превышать SourceFailureBackoffMaxHours")
            .Validate(x => x.MaxProxiesPerSource is >= 1 and <= 1_000_000, "MaxProxiesPerSource: 1..1000000")
            .Validate(x => x.MaxCandidatesPerRun is >= 1 and <= 5_000_000, "MaxCandidatesPerRun: 1..5000000")
            .Validate(x => x.LastSeenRefreshMinutes is >= 1 and <= 10_080, "LastSeenRefreshMinutes: 1..10080")
            .Validate(x => TimeSpan.FromMinutes(x.LastSeenRefreshMinutes) <= TimeSpan.FromDays(x.DeadRetentionDays),
                "LastSeenRefreshMinutes не может превышать DeadRetentionDays")
            .Validate(x => x.DeadRetentionDays is >= 1 and <= 365, "DeadRetentionDays: 1..365")
            .Validate(x => x.RunRetentionDays is >= 1 and <= 3_650, "RunRetentionDays: 1..3650")
            .Validate(x => x.ProbePort is >= 1 and <= 65_535, "ProbePort: 1..65535")
            .Validate(x => Uri.CheckHostName(x.ProbeHost) != UriHostNameType.Unknown && !x.ProbeHost.Any(char.IsControl), "ProbeHost должен быть корректным DNS-именем или IP")
            .Validate(x => x.ProbePath.StartsWith('/') && x.ProbePath.Length <= 2048 && !x.ProbePath.Any(char.IsControl), "ProbePath должен быть безопасным относительным HTTP-путём")
            .ValidateOnStart();
        services.AddOptions<BackupOptions>().Bind(configuration.GetSection(BackupOptions.Section))
            .Validate(x => x.IntervalHours is >= 1 and <= 8_760, "IntervalHours: 1..8760")
            .Validate(x => x.RetentionDays is >= 1 and <= 3_650, "RetentionDays: 1..3650")
            .Validate(x => x.MaxTelegramFileSizeMb is >= 1 and <= 49, "MaxTelegramFileSizeMb: 1..49")
            .Validate(x => !x.Enabled || x.EncryptionKey?.Length >= 16, "Для резервного копирования нужен EncryptionKey длиной не менее 16 символов")
            .Validate(x => string.IsNullOrWhiteSpace(x.TelegramBotToken) == string.IsNullOrWhiteSpace(x.TelegramChatId), "TelegramBotToken и TelegramChatId задаются только вместе")
            .ValidateOnStart();
        services.AddPooledDbContextFactory<ProxyHarborDbContext>(x => x.UseNpgsql(connection, npgsql =>
            npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null)));
        services.AddHttpClient("sources", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyHarbor/1.0");
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = PublicNetworkConnector.ConnectAsync
        });
        services.AddHttpClient("telegram", client => client.Timeout = TimeSpan.FromMinutes(5)).RemoveAllLoggers();
        services.AddHttpClient("origin", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<ProxyCollector>();
        services.AddSingleton<ProxyProbeService>();
        services.AddSingleton<OriginIpProvider>();
        services.AddSingleton<ProxyValidator>();
        services.AddSingleton<BackupService>();
        services.AddHostedService<CollectorWorker>();
        services.AddHostedService<ValidatorWorker>();
        services.AddHostedService<BackupWorker>();
        return services;
    }
}
