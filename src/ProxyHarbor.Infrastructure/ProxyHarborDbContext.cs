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
    /// <summary>Аудит ручных изменений подписок.</summary>
    public DbSet<SubscriptionAdminAction> SubscriptionAdminActions => Set<SubscriptionAdminAction>();
    /// <summary>Агрегированная статистика выдачи адресов и посещений сайта.</summary>
    public DbSet<ProxyAccessBucket> ProxyAccessBuckets => Set<ProxyAccessBucket>();
    /// <summary>Серверные интервалы бесплатной выгрузки по аккаунту или IP.</summary>
    public DbSet<FreeProxyExportGrant> FreeProxyExportGrants => Set<FreeProxyExportGrant>();
    /// <summary>Правила блокировки клиентов выдачи.</summary>
    public DbSet<AccessBlockRule> AccessBlockRules => Set<AccessBlockRule>();
    /// <summary>Singleton runtime-настройка торгового Telegram-бота.</summary>
    public DbSet<TelegramBotConfiguration> TelegramBotConfigurations => Set<TelegramBotConfiguration>();
    /// <summary>Singleton runtime-настройка резервного копирования.</summary>
    public DbSet<BackupConfiguration> BackupConfigurations => Set<BackupConfiguration>();
    /// <summary>Telegram-диалоги и связанные аккаунты.</summary>
    public DbSet<TelegramChat> TelegramChats => Set<TelegramChat>();
    /// <summary>Идемпотентный журнал входящих update.</summary>
    public DbSet<TelegramUpdateReceipt> TelegramUpdateReceipts => Set<TelegramUpdateReceipt>();
    /// <summary>Надёжная очередь исходящих сообщений.</summary>
    public DbSet<TelegramOutboundMessage> TelegramOutboundMessages => Set<TelegramOutboundMessage>();
    /// <summary>История CRM-диалогов.</summary>
    public DbSet<TelegramConversationMessage> TelegramConversationMessages => Set<TelegramConversationMessage>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var user = builder.Entity<ApplicationUser>();
        user.Property(x => x.DisplayName).HasMaxLength(120);
        user.Property(x => x.PreferredLanguage).HasMaxLength(2).HasDefaultValue(SupportedLanguages.Default);
        user.HasIndex(x => x.CreatedAt);
        user.ToTable(table =>
        {
            table.HasCheckConstraint("CK_AspNetUsers_ActiveTimeline", "\"LastLoginAt\" IS NULL OR \"LastLoginAt\" >= \"CreatedAt\"");
            table.HasCheckConstraint("CK_AspNetUsers_PreferredLanguage", "\"PreferredLanguage\" IN ('ru', 'en', 'de', 'fr', 'zh')");
        });

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
            table.HasCheckConstraint("CK_Subscriptions_Status", "\"Status\" IN ('active', 'trialing', 'past_due', 'canceled', 'expired', 'suspended')");
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

        var subscriptionAction = builder.Entity<SubscriptionAdminAction>();
        subscriptionAction.HasIndex(x => new { x.SubscriptionId, x.CreatedAt });
        subscriptionAction.Property(x => x.Action).HasMaxLength(32);
        subscriptionAction.Property(x => x.PreviousPlan).HasMaxLength(32);
        subscriptionAction.Property(x => x.PreviousStatus).HasMaxLength(32);
        subscriptionAction.Property(x => x.NewPlan).HasMaxLength(32);
        subscriptionAction.Property(x => x.NewStatus).HasMaxLength(32);
        subscriptionAction.Property(x => x.Reason).HasMaxLength(500);
        subscriptionAction.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);
        subscriptionAction.HasOne(x => x.Administrator).WithMany().HasForeignKey(x => x.AdministratorId)
            .OnDelete(DeleteBehavior.Restrict);

        var accessBucket = builder.Entity<ProxyAccessBucket>();
        accessBucket.HasIndex(x => new { x.BucketStartedAt, x.IpAddress, x.UserId, x.Endpoint })
            .IsUnique().AreNullsDistinct(false);
        accessBucket.HasIndex(x => new { x.LastSeenAt, x.Requests });
        accessBucket.Property(x => x.IpAddress).HasMaxLength(45);
        accessBucket.Property(x => x.Endpoint).HasMaxLength(32);
        accessBucket.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.SetNull);
        accessBucket.ToTable(table => table.HasCheckConstraint(
            "CK_ProxyAccessBuckets_Counters",
            "\"Requests\" >= 0 AND \"BlockedRequests\" >= 0 AND \"ProxyItems\" >= 0 AND \"BytesSent\" >= 0"));

        var freeExportGrant = builder.Entity<FreeProxyExportGrant>();
        freeExportGrant.HasKey(x => x.ClientKey);
        freeExportGrant.Property(x => x.ClientKey).HasMaxLength(128);
        freeExportGrant.HasIndex(x => x.NextAllowedAt);
        freeExportGrant.ToTable(table => table.HasCheckConstraint(
            "CK_FreeProxyExportGrants_Timeline", "\"NextAllowedAt\" > \"LastGrantedAt\""));

        var blockRule = builder.Entity<AccessBlockRule>();
        blockRule.HasIndex(x => new { x.Enabled, x.ExpiresAt });
        blockRule.HasIndex(x => new { x.Kind, x.Value });
        blockRule.Property(x => x.Kind).HasMaxLength(16);
        blockRule.Property(x => x.Value).HasMaxLength(128);
        blockRule.Property(x => x.Reason).HasMaxLength(500);
        blockRule.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        blockRule.HasOne(x => x.Administrator).WithMany().HasForeignKey(x => x.AdministratorId)
            .OnDelete(DeleteBehavior.Restrict);
        blockRule.ToTable(table => table.HasCheckConstraint(
            "CK_AccessBlockRules_Kind", "\"Kind\" IN ('ip', 'cidr', 'user')"));

        var telegramConfiguration = builder.Entity<TelegramBotConfiguration>();
        telegramConfiguration.Property(x => x.SettingsJson).HasColumnType("jsonb");
        telegramConfiguration.Property(x => x.ProtectedSecrets).HasMaxLength(65_536);
        telegramConfiguration.Property(x => x.BotUsername).HasMaxLength(64);
        telegramConfiguration.ToTable(table => table.HasCheckConstraint(
            "CK_TelegramBotConfigurations_Singleton", "\"Id\" = 1"));

        var backupConfiguration = builder.Entity<BackupConfiguration>();
        backupConfiguration.Property(x => x.SettingsJson).HasColumnType("jsonb");
        backupConfiguration.Property(x => x.ProtectedSecrets).HasMaxLength(65_536);
        backupConfiguration.ToTable(table => table.HasCheckConstraint(
            "CK_BackupConfigurations_Singleton", "\"Id\" = 1"));

        var telegramChat = builder.Entity<TelegramChat>();
        telegramChat.HasIndex(x => x.ChatId).IsUnique();
        telegramChat.HasIndex(x => x.TelegramUserId).IsUnique();
        telegramChat.HasIndex(x => x.UserId).IsUnique();
        telegramChat.HasIndex(x => new { x.NotificationsEnabled, x.IsBlocked, x.LastInteractionAt });
        telegramChat.Property(x => x.Username).HasMaxLength(64);
        telegramChat.Property(x => x.DisplayName).HasMaxLength(160);
        telegramChat.Property(x => x.LanguageCode).HasMaxLength(16);
        telegramChat.HasOne(x => x.User).WithOne(x => x.TelegramChat)
            .HasForeignKey<TelegramChat>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var telegramUpdate = builder.Entity<TelegramUpdateReceipt>();
        telegramUpdate.HasKey(x => x.UpdateId);
        telegramUpdate.HasIndex(x => x.ReceivedAt);
        telegramUpdate.Property(x => x.Transport).HasMaxLength(16);
        telegramUpdate.Property(x => x.Error).HasMaxLength(1000);
        telegramUpdate.ToTable(table => table.HasCheckConstraint(
            "CK_TelegramUpdateReceipts_Transport", "\"Transport\" IN ('webhook', 'polling')"));

        var telegramOutbound = builder.Entity<TelegramOutboundMessage>();
        telegramOutbound.HasIndex(x => x.IdempotencyKey).IsUnique();
        telegramOutbound.HasIndex(x => new { x.Status, x.AvailableAt, x.LeaseUntil });
        telegramOutbound.HasIndex(x => new { x.TelegramChatId, x.CreatedAt });
        telegramOutbound.Property(x => x.Kind).HasMaxLength(24);
        telegramOutbound.Property(x => x.Status).HasMaxLength(16);
        telegramOutbound.Property(x => x.IdempotencyKey).HasMaxLength(160);
        telegramOutbound.Property(x => x.PayloadJson).HasColumnType("jsonb");
        telegramOutbound.Property(x => x.LastError).HasMaxLength(1000);
        telegramOutbound.HasOne(x => x.TelegramChat).WithMany().HasForeignKey(x => x.TelegramChatId)
            .OnDelete(DeleteBehavior.Cascade);
        telegramOutbound.ToTable(table =>
        {
            table.HasCheckConstraint("CK_TelegramOutboundMessages_Kind", "\"Kind\" IN ('text', 'invoice', 'proxy_file')");
            table.HasCheckConstraint("CK_TelegramOutboundMessages_Status", "\"Status\" IN ('pending', 'processing', 'sent', 'failed', 'canceled')");
            table.HasCheckConstraint("CK_TelegramOutboundMessages_Attempts", "\"Attempts\" BETWEEN 0 AND 20");
        });

        var telegramConversation = builder.Entity<TelegramConversationMessage>();
        telegramConversation.HasIndex(x => new { x.TelegramChatId, x.CreatedAt });
        telegramConversation.Property(x => x.Direction).HasMaxLength(16);
        telegramConversation.Property(x => x.Text).HasMaxLength(4096);
        telegramConversation.HasOne(x => x.TelegramChat).WithMany().HasForeignKey(x => x.TelegramChatId)
            .OnDelete(DeleteBehavior.Cascade);
        telegramConversation.HasOne(x => x.Administrator).WithMany().HasForeignKey(x => x.AdministratorId)
            .OnDelete(DeleteBehavior.SetNull);
        telegramConversation.HasOne<TelegramOutboundMessage>().WithMany().HasForeignKey(x => x.OutboundMessageId)
            .OnDelete(DeleteBehavior.SetNull);
        telegramConversation.ToTable(table => table.HasCheckConstraint(
            "CK_TelegramConversationMessages_Direction", "\"Direction\" IN ('inbound', 'bot', 'admin')"));

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
