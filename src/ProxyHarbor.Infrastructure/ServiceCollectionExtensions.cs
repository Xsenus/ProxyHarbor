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
            .Validate(x => (long)x.DeadRetryBaseMinutes <= (long)x.DeadRetryMaxHours * 60,
                "DeadRetryBaseMinutes не может превышать DeadRetryMaxHours")
            .Validate(x => x.ValidationConcurrency is >= 1 and <= 1_000, "ValidationConcurrency: 1..1000")
            .Validate(x => x.ValidationBatchSize is >= 1 and <= 100_000, "ValidationBatchSize: 1..100000")
            .Validate(x => x.ProbeTimeoutSeconds is >= 1 and <= 120, "ProbeTimeoutSeconds: 1..120")
            .Validate(x => x.SourceTimeoutSeconds is >= 2 and <= 300, "SourceTimeoutSeconds: 2..300")
            .Validate(x => x.SourceConcurrency is >= 1 and <= 32, "SourceConcurrency: 1..32")
            .Validate(x => x.SourceRetryCount is >= 0 and <= 5, "SourceRetryCount: 0..5")
            .Validate(x => x.SourceFailureBackoffBaseMinutes is >= 1 and <= 1_440, "SourceFailureBackoffBaseMinutes: 1..1440")
            .Validate(x => x.SourceFailureBackoffMaxHours is >= 1 and <= 720, "SourceFailureBackoffMaxHours: 1..720")
            .Validate(x => (long)x.SourceFailureBackoffBaseMinutes <= (long)x.SourceFailureBackoffMaxHours * 60,
                "SourceFailureBackoffBaseMinutes не может превышать SourceFailureBackoffMaxHours")
            .Validate(x => x.MaxProxiesPerSource is >= 1 and <= 1_000_000, "MaxProxiesPerSource: 1..1000000")
            .Validate(x => x.MaxCandidatesPerRun is >= 1 and <= 5_000_000, "MaxCandidatesPerRun: 1..5000000")
            .Validate(x => x.LastSeenRefreshMinutes is >= 1 and <= 10_080, "LastSeenRefreshMinutes: 1..10080")
            .Validate(x => (long)x.LastSeenRefreshMinutes <= (long)x.DeadRetentionDays * 24 * 60,
                "LastSeenRefreshMinutes не может превышать DeadRetentionDays")
            .Validate(x => x.DeadRetentionDays is >= 1 and <= 365, "DeadRetentionDays: 1..365")
            .Validate(x => x.RunRetentionDays is >= 1 and <= 3_650, "RunRetentionDays: 1..3650")
            .Validate(x => x.ProbePort is >= 1 and <= 65_535, "ProbePort: 1..65535")
            .Validate(x => CollectorOptions.IsProbeHostValid(x.ProbeHost),
                "ProbeHost должен быть каноническим ASCII DNS-именем или IP длиной не более 253 символов")
            .Validate(x => !IPAddress.TryParse(x.ProbeHost, out var address) || NetworkSafety.IsPublicAddress(address),
                "ProbeHost не может быть локальным, private или служебным IP")
            .Validate(x => CollectorOptions.IsProbePathValid(x.ProbePath),
                "ProbePath должен быть каноническим printable ASCII origin-form без fragment, пробелов или network-path")
            .ValidateOnStart();
        services.AddOptions<BackupOptions>().Bind(configuration.GetSection(BackupOptions.Section))
            .Validate(x => x.IntervalHours is >= 1 and <= 8_760, "IntervalHours: 1..8760")
            .Validate(x => x.RetentionDays is >= 1 and <= 3_650, "RetentionDays: 1..3650")
            .Validate(x => x.HistoryRetentionDays is >= 1 and <= 3_650, "HistoryRetentionDays: 1..3650")
            .Validate(x => x.MaxTelegramFileSizeMb is >= 1 and <= 49, "MaxTelegramFileSizeMb: 1..49")
            .Validate(x => !x.Enabled || BackupOptions.IsNewEncryptionKeyValid(x.EncryptionKey),
                $"Для резервного копирования нужен EncryptionKey длиной {BackupOptions.MinimumEncryptionKeyLength}..{BackupOptions.MaximumEncryptionKeyLength} символов с корректной Unicode-кодировкой без управляющих знаков")
            .Validate(x => !x.Enabled || BackupOptions.IsDirectoryValid(x.Directory),
                "Backup Directory должен быть абсолютным безопасным путём длиной не более 1024 символов")
            .Validate(x => string.IsNullOrWhiteSpace(x.TelegramBotToken) == string.IsNullOrWhiteSpace(x.TelegramChatId), "TelegramBotToken и TelegramChatId задаются только вместе")
            .Validate(x => !x.Enabled || (!string.IsNullOrWhiteSpace(x.TelegramBotToken) &&
                !string.IsNullOrWhiteSpace(x.TelegramChatId)),
                "При Backup Enabled доставка в Telegram обязательна; задайте TelegramBotToken и TelegramChatId")
            .Validate(x => string.IsNullOrWhiteSpace(x.TelegramBotToken) ||
                BackupOptions.IsTelegramBotTokenValid(x.TelegramBotToken),
                "TelegramBotToken должен содержать 20..256 printable path-safe ASCII символов без /, \\, ?, # и %")
            .Validate(x => string.IsNullOrWhiteSpace(x.TelegramChatId) ||
                BackupOptions.IsTelegramChatIdValid(x.TelegramChatId),
                "TelegramChatId должен быть ненулевым signed 64-bit числом")
            .ValidateOnStart();
        services.AddOptions<GeoIpOptions>().Bind(configuration.GetSection(GeoIpOptions.Section))
            .Validate(x => x.RefreshHours is >= 1 and <= 720, "RefreshHours: 1..720")
            .Validate(x => x.BackfillBatchSize is >= 1 and <= 100_000, "BackfillBatchSize: 1..100000")
            .Validate(x => Path.IsPathFullyQualified(x.DatabasePath), "DatabasePath должен быть абсолютным")
            .ValidateOnStart();
        services.AddPooledDbContextFactory<ProxyHarborDbContext>(x => x.UseNpgsql(connection, npgsql =>
            npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null)));
        // Короткие API/worker-команды выигрывают от retry, но streaming export нельзя
        // повторять после отправки части body. Для него зарегистрирован отдельный контекст.
        services.AddSingleton<IProxyExportDbContextFactory>(new NpgsqlExportDbContextFactory(connection));
        services.AddHttpClient("sources", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyHarbor/1.0");
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => PublicNetworkConnector.Harden(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }));
        services.AddHttpClient("telegram", client => client.Timeout = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(() => PublicNetworkConnector.Harden(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }))
            // URI Bot API содержит token, поэтому стандартное HTTP-логирование полностью отключено.
            .RemoveAllLoggers();
        services.AddHttpClient("origin", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => PublicNetworkConnector.Harden(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                MaxConnectionsPerServer = 2,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }));
        services.AddHttpClient("geoip", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyHarbor/1.0");
            client.Timeout = TimeSpan.FromMinutes(3);
        });
        services.AddSingleton<ProxyCollector>();
        services.AddSingleton<ISourceCatalogMutationCoordinator, SourceCatalogMutationCoordinator>();
        services.AddSingleton<ProxyProbeService>();
        services.AddSingleton<ProbeControlHealth>();
        services.AddSingleton<OriginIpProvider>();
        services.AddSingleton<ValidationWakeSignal>();
        services.AddSingleton<ProxyValidator>();
        services.AddSingleton<VpnCatalogService>();
        services.AddSingleton<IBackupConfigurationStore, BackupConfigurationStore>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<DatabaseReadinessProbe>();
        services.AddSingleton<OperationalMaintenanceService>();
        services.AddSingleton<ProxyCountryResolver>();
        services.AddHostedService<CollectorWorker>();
        services.AddHostedService<ValidatorWorker>();
        services.AddHostedService<VpnCatalogWorker>();
        services.AddHostedService<BackupWorker>();
        services.AddHostedService<OperationalMaintenanceWorker>();
        services.AddHostedService<ProxyCountryWorker>();
        return services;
    }
}
