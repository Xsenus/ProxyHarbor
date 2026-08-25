using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Контекст PostgreSQL со всеми индексами и начальными источниками.</summary>
public sealed class ProxyHarborDbContext(DbContextOptions<ProxyHarborDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    /// <summary>Все собранные и дедуплицированные proxy endpoints.</summary>
    public DbSet<ProxyEndpoint> Proxies => Set<ProxyEndpoint>();
    /// <summary>Встроенные и пользовательские proxy feed'ы.</summary>
    public DbSet<ProxySource> Sources => Set<ProxySource>();
    /// <summary>История циклов сбора.</summary>
    public DbSet<CollectionRun> Runs => Set<CollectionRun>();
    /// <summary>История validation-партий.</summary>
    public DbSet<ValidationRun> ValidationRuns => Set<ValidationRun>();
    /// <summary>История создания и Telegram-доставки backup.</summary>
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    /// <summary>Текущие тарифы пользователей, отделённые от Identity-ролей.</summary>
    public DbSet<UserSubscription> Subscriptions => Set<UserSubscription>();
    /// <summary>Аудируемые заказы на оплату без платёжных реквизитов.</summary>
    public DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();
    /// <summary>Singleton runtime-настройка платежей с защищёнными секретами.</summary>
    public DbSet<PaymentConfiguration> PaymentConfigurations => Set<PaymentConfiguration>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var user = builder.Entity<ApplicationUser>();
        user.Property(x => x.DisplayName).HasMaxLength(120);
        user.HasIndex(x => x.CreatedAt);
        user.ToTable(table => table.HasCheckConstraint(
            "CK_AspNetUsers_ActiveTimeline",
            "\"LastLoginAt\" IS NULL OR \"LastLoginAt\" >= \"CreatedAt\""));

        var subscription = builder.Entity<UserSubscription>();
        subscription.HasIndex(x => x.UserId).IsUnique();
        subscription.HasIndex(x => new { x.Plan, x.Status, x.ExpiresAt });
        subscription.Property(x => x.Plan).HasMaxLength(32);
        subscription.Property(x => x.Status).HasMaxLength(32);
        subscription.Property(x => x.ExternalCustomerId).HasMaxLength(255);
        subscription.Property(x => x.ExternalSubscriptionId).HasMaxLength(255);
        subscription.HasOne(x => x.User).WithOne(x => x.Subscription)
            .HasForeignKey<UserSubscription>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        subscription.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Subscriptions_Plan", "\"Plan\" IN ('free', 'pro', 'unlimited')");
            table.HasCheckConstraint("CK_Subscriptions_Status", "\"Status\" IN ('active', 'trialing', 'past_due', 'canceled', 'expired')");
            table.HasCheckConstraint("CK_Subscriptions_Timeline", "\"ExpiresAt\" IS NULL OR \"ExpiresAt\" >= \"StartedAt\"");
        });

        var payment = builder.Entity<PaymentOrder>();
        payment.HasIndex(x => x.IdempotencyKey).IsUnique();
        payment.HasIndex(x => new { x.Provider, x.ProviderPaymentId }).IsUnique();
        payment.HasIndex(x => new { x.UserId, x.CreatedAt });
        payment.Property(x => x.ProductCode).HasMaxLength(64);
        payment.Property(x => x.Plan).HasMaxLength(32);
        payment.Property(x => x.Provider).HasMaxLength(32);
        payment.Property(x => x.Currency).HasMaxLength(3);
        payment.Property(x => x.Status).HasMaxLength(32);
        payment.Property(x => x.ProviderPaymentId).HasMaxLength(255);
        payment.Property(x => x.CheckoutUrl).HasMaxLength(2048);
        payment.Property(x => x.IdempotencyKey).HasMaxLength(64);
        payment.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        payment.ToTable(table =>
        {
            table.HasCheckConstraint("CK_PaymentOrders_Plan", "\"Plan\" IN ('pro', 'unlimited')");
            table.HasCheckConstraint("CK_PaymentOrders_Status", "\"Status\" IN ('pending', 'paid', 'failed', 'canceled', 'refunded')");
            table.HasCheckConstraint("CK_PaymentOrders_Amount", "\"AmountMinor\" > 0 AND \"DurationDays\" BETWEEN 1 AND 3660");
            table.HasCheckConstraint("CK_PaymentOrders_Currency", "char_length(\"Currency\") = 3 AND \"Currency\" = upper(\"Currency\")");
            table.HasCheckConstraint("CK_PaymentOrders_Timeline", "\"PaidAt\" IS NULL OR (\"PaidAt\" >= \"CreatedAt\" AND \"Status\" IN ('paid', 'refunded'))");
        });

        var paymentConfiguration = builder.Entity<PaymentConfiguration>();
        paymentConfiguration.Property(x => x.SettingsJson).HasColumnType("jsonb");
        paymentConfiguration.Property(x => x.ProtectedSecrets).HasMaxLength(65_536);
        paymentConfiguration.ToTable(table =>
            table.HasCheckConstraint("CK_PaymentConfigurations_Singleton", "\"Id\" = 1"));

        var proxy = builder.Entity<ProxyEndpoint>();
        proxy.HasIndex(x => new { x.Host, x.Port, x.Protocol }).IsUnique();
        // Публичная выдача читает только Alive. Частичные индексы не раздуваются
        // сотнями тысяч Pending/Dead строк и точно повторяют стабильный API order.
        proxy.HasIndex(x => new { x.LatencyMs, x.SuccessfulChecks, x.Id, x.LastCheckedAt })
            .HasDatabaseName("IX_Proxies_Alive_PublicOrder")
            .HasFilter("\"Status\" = 1")
            .IsDescending(false, true, false, false)
            .IsCreatedConcurrently();
        proxy.HasIndex(x => new { x.Protocol, x.LatencyMs, x.SuccessfulChecks, x.Id, x.LastCheckedAt })
            .HasDatabaseName("IX_Proxies_Alive_Protocol_PublicOrder")
            .HasFilter("\"Status\" = 1")
            .IsDescending(false, false, true, false, false)
            .IsCreatedConcurrently();
        // Отдельные компактные индексы позволяют считать точный total без scan
        // всей таблицы; LastCheckedAt является range-key окна публикации.
        proxy.HasIndex(x => x.LastCheckedAt)
            .HasDatabaseName("IX_Proxies_Alive_LastCheckedAt")
            .HasFilter("\"Status\" = 1")
            .IsCreatedConcurrently();
        proxy.HasIndex(x => new { x.Protocol, x.LastCheckedAt })
            .HasDatabaseName("IX_Proxies_Alive_Protocol_LastCheckedAt")
            .HasFilter("\"Status\" = 1")
            .IsCreatedConcurrently();
        proxy.HasIndex(x => new { x.Status, x.LastSeenAt });
        proxy.HasIndex(x => new { x.NextCheckAt, x.CheckLeaseUntil });
        // Точный expression-index для CASE priority + due order создаётся raw migration,
        // поскольку EF-модель не представляет CASE key. Он остаётся вне snapshot намеренно.
        proxy.HasIndex(x => x.CheckLeaseId);
        proxy.HasIndex(x => x.LastValidationAttemptAt);
        proxy.Ignore(x => x.Key);
        proxy.Ignore(x => x.SuccessRate);
        proxy.Property(x => x.Host).HasMaxLength(255);
        proxy.Property(x => x.ExitIp).HasMaxLength(64);
        proxy.Property(x => x.CountryCode).HasMaxLength(2);
        proxy.Property(x => x.LastError).HasMaxLength(500);
        proxy.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Proxies_Identity", "\"Port\" BETWEEN 1 AND 65535 AND \"Protocol\" BETWEEN 0 AND 3 AND \"Status\" BETWEEN 0 AND 2");
            table.HasCheckConstraint("CK_Proxies_Timeline", "\"LastSeenAt\" >= \"FirstSeenAt\"");
            table.HasCheckConstraint("CK_Proxies_AliveTimeline", "(\"FirstAliveAt\" IS NULL) = (\"LastAliveAt\" IS NULL) AND (\"FirstAliveAt\" IS NULL OR (\"FirstAliveAt\" >= \"FirstSeenAt\" AND \"LastAliveAt\" >= \"FirstAliveAt\")) AND (\"CurrentAliveSince\" IS NULL OR (\"Status\" = 1 AND \"FirstAliveAt\" IS NOT NULL AND \"CurrentAliveSince\" >= \"FirstAliveAt\" AND \"LastAliveAt\" >= \"CurrentAliveSince\"))");
            table.HasCheckConstraint("CK_Proxies_Latency", "\"LatencyMs\" IS NULL OR \"LatencyMs\" >= 0");
            table.HasCheckConstraint("CK_Proxies_CheckCounters", "\"SuccessfulChecks\" >= 0 AND \"FailedChecks\" >= 0 AND \"ConsecutiveFailedChecks\" >= 0 AND \"ConsecutiveFailedChecks\" <= \"FailedChecks\" AND \"SuccessfulChecks\"::bigint + \"FailedChecks\"::bigint <= 2147483647");
            // Alive/Dead публикуются и учитываются только после доказанной проверки.
            // Pending остаётся свободным состоянием для новых и повторно поставленных в очередь строк.
            table.HasCheckConstraint("CK_Proxies_StatusEvidence", "(\"Status\" = 0) OR (\"Status\" = 1 AND \"LastCheckedAt\" IS NOT NULL AND \"LatencyMs\" IS NOT NULL AND \"SuccessfulChecks\" > 0) OR (\"Status\" = 2 AND \"LastCheckedAt\" IS NOT NULL AND \"FailedChecks\" > 0)");
            table.HasCheckConstraint("CK_Proxies_Lease", "(\"CheckLeaseUntil\" IS NULL) = (\"CheckLeaseId\" IS NULL)");
            table.HasCheckConstraint("CK_Proxies_DeferredAttempt", "NOT \"LastValidationDeferred\" OR \"LastValidationAttemptAt\" IS NOT NULL");
        });

        var source = builder.Entity<ProxySource>();
        source.HasIndex(x => x.Url).IsUnique();
        source.HasIndex(x => new { x.Enabled, x.ConsecutiveFailures });
        source.HasIndex(x => new { x.Enabled, x.NextFetchAt });
        source.Property(x => x.Name).HasMaxLength(120);
        source.Property(x => x.Url).HasMaxLength(2048);
        source.Property(x => x.HttpETag).HasMaxLength(512);
        source.Property(x => x.LastError).HasMaxLength(500);
        source.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Sources_ProtocolPriority", "\"DefaultProtocol\" BETWEEN 0 AND 3 AND \"Priority\" BETWEEN -10000 AND 10000");
            table.HasCheckConstraint("CK_Sources_Counters", "\"LastItemCount\" >= 0 AND \"ConsecutiveFailures\" >= 0");
            table.HasCheckConstraint("CK_Sources_FetchTimeline", "\"LastSucceededAt\" IS NULL OR (\"LastFetchedAt\" IS NOT NULL AND \"LastSucceededAt\" <= \"LastFetchedAt\")");
            table.HasCheckConstraint("CK_Sources_ContentTimeline", "\"LastContentFetchedAt\" IS NULL OR (\"LastFetchedAt\" IS NOT NULL AND \"LastSucceededAt\" IS NOT NULL AND \"LastContentFetchedAt\" <= \"LastFetchedAt\" AND \"LastContentFetchedAt\" <= \"LastSucceededAt\")");
        });

        var collectionRun = builder.Entity<CollectionRun>();
        collectionRun.Property(x => x.Error).HasMaxLength(2000);
        collectionRun.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Runs_State", "\"Status\" IN ('running', 'completed', 'failed') AND ((\"Status\" = 'running') = (\"FinishedAt\" IS NULL)) AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
            table.HasCheckConstraint("CK_Runs_Counters", "\"SourcesProcessed\" >= 0 AND \"SourcesSucceeded\" >= 0 AND \"SourcesFailed\" >= 0 AND \"SourcesSkipped\" >= 0 AND \"SourcesTruncated\" >= 0 AND \"CandidatesFound\" >= 0 AND \"NewProxies\" >= 0 AND \"AliveProxies\" >= 0 AND \"SourcesSucceeded\"::bigint + \"SourcesFailed\"::bigint = \"SourcesProcessed\" AND \"SourcesTruncated\" <= \"SourcesSucceeded\" AND \"NewProxies\" <= \"CandidatesFound\"");
        });

        var validationRun = builder.Entity<ValidationRun>();
        validationRun.HasIndex(x => x.LeaseId).IsUnique();
        validationRun.HasIndex(x => x.StartedAt);
        validationRun.HasIndex(x => new { x.Status, x.FinishedAt });
        validationRun.Property(x => x.Status).HasMaxLength(32);
        validationRun.Property(x => x.Error).HasMaxLength(2000);
        validationRun.ToTable(table =>
        {
            table.HasCheckConstraint("CK_ValidationRuns_State", "\"Status\" IN ('running', 'completed', 'failed') AND ((\"Status\" = 'running') = (\"FinishedAt\" IS NULL)) AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
            table.HasCheckConstraint("CK_ValidationRuns_Counters", "\"Claimed\" >= 0 AND \"Checked\" >= 0 AND \"Alive\" >= 0 AND \"Deferred\" >= 0 AND \"Checked\"::bigint + \"Deferred\"::bigint <= \"Claimed\" AND \"Alive\" <= \"Checked\"");
        });

        var backupRun = builder.Entity<BackupRun>();
        backupRun.HasIndex(x => x.StartedAt);
        backupRun.HasIndex(x => new { x.Status, x.FinishedAt });
        backupRun.Property(x => x.Status).HasMaxLength(32);
        backupRun.Property(x => x.FileName).HasMaxLength(255);
        backupRun.Property(x => x.Error).HasMaxLength(2000);
        backupRun.ToTable(table =>
        {
            table.HasCheckConstraint("CK_BackupRuns_State", "\"Status\" IN ('running', 'completed', 'failed') AND ((\"Status\" = 'running') = (\"FinishedAt\" IS NULL)) AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
            table.HasCheckConstraint("CK_BackupRuns_Result", "\"SizeBytes\" >= 0 AND (NOT \"SentToTelegram\" OR \"TelegramConfigured\") AND (\"Status\" <> 'completed' OR NOT \"TelegramConfigured\" OR \"SentToTelegram\")");
        });
    }
}
