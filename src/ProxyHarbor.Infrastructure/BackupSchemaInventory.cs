namespace ProxyHarbor.Infrastructure;

/// <summary>Политика включения одной таблицы PostgreSQL в переносимый backup.</summary>
public enum BackupTableDisposition
{
    /// <summary>Таблица обязана иметь отдельный JSON entry и импортироваться при полном restore.</summary>
    Included,
    /// <summary>Таблица содержит только краткоживущее operational ownership и создаётся заново.</summary>
    Ephemeral
}

/// <summary>Явная классификация одной физической таблицы для backup и restore.</summary>
public sealed record BackupTableClassification(
    string TableName,
    BackupTableDisposition Disposition,
    string? ArchiveEntry,
    int IntroducedInManifestVersion,
    string Rationale);

/// <summary>
/// Единый проверяемый inventory таблиц. Новая EF-таблица не может остаться вне
/// backup незаметно: contract test сравнивает этот список с relational model.
/// </summary>
public static class BackupSchemaInventory
{
    /// <summary>Текущая полная классификация физических таблиц PostgreSQL.</summary>
    public static IReadOnlyList<BackupTableClassification> Tables { get; } =
    [
        Included("AccessBlockRules", "database/access-block-rules.json", "Правила доступа являются durable policy."),
        Included("AspNetRoleClaims", "database/role-claims.json", "Identity authorization state."),
        Included("AspNetRoles", "database/roles.json", "Identity roles."),
        Included("AspNetUserClaims", "database/user-claims.json", "Identity account state."),
        Included("AspNetUserLogins", "database/user-logins.json", "External login bindings."),
        Included("AspNetUserRoles", "database/user-roles.json", "Identity role assignments."),
        Included("AspNetUsers", "database/users.json", "User accounts and password hashes."),
        Included("AspNetUserTokens", "database/user-identity-tokens.json", "Identity token state."),
        IncludedV9("BackupCopies", "database/backup-copies.json", "Durable physical-copy evidence."),
        Included("BackupConfigurations", "database/backup-configuration.json", "Persistent runtime backup settings."),
        IncludedV9("BackupDeliveryJobs", "database/backup-delivery-jobs.json", "Durable delivery and reconciliation queue."),
        IncludedV9("BackupDestinations", "database/backup-destinations.json", "Protected destination configuration."),
        Ephemeral("BackupDestinationHealthOutcomes", "Short-lived provider VERIFY health is rebuilt from new probes after restore.", 9),
        IncludedV9("BackupPoolDestinations", "database/backup-pool-destinations.json", "Allowed routing graph."),
        IncludedV9("BackupPools", "database/backup-pools.json", "Backup protection policies."),
        IncludedV9("BackupRestoreVerifications", "database/backup-restore-verifications.json", "Restore-drill evidence."),
        Included("BackupRuns", "database/backup-runs.json", "Backup audit and delivery evidence."),
        Included("CheckerNodes", "database/checker-nodes.json", "Registered checker topology without plaintext credentials."),
        Included("FreeProxyExportGrants", "database/free-proxy-export-grants.json", "Durable export grants."),
        Included("MetricsSnapshotStates", "database/metrics-snapshot-states.json", "Persistent compact operational snapshots."),
        Included("PaymentConfigurations", "database/payment-configuration.json", "Protected payment configuration."),
        Included("PaymentOrders", "database/payment-orders.json", "Financial order audit."),
        Included("Proxies", "database/proxies.json", "Proxy catalog."),
        Included("ProxyAccessBuckets", "database/proxy-access-buckets.json", "Durable access accounting."),
        Included("ProxySourceCredentials", "database/proxy-source-credentials.json", "Protected paid-provider credentials and status."),
        Ephemeral("ProxyValidationLeases", "Validation ownership expires and must never survive restore."),
        Included("ReferralRelationships", "database/referral-relationships.json", "Immutable referral ownership."),
        Included("ReferralRewards", "database/referral-rewards.json", "Idempotent subscription rewards."),
        Included("Runs", "database/runs.json", "Collection audit."),
        Included("SiteConfigurations", "database/site-configuration.json", "Public-site runtime configuration."),
        Included("SiteVisitLogs", "database/site-visit-logs.json", "Durable first-party visit audit."),
        Included("Sources", "database/sources.json", "Proxy source catalog and fetch state."),
        Included("SubscriptionAdminActions", "database/subscription-admin-actions.json", "Administrative subscription audit."),
        Included("Subscriptions", "database/subscriptions.json", "Current account entitlements."),
        Included("TelegramBotConfigurations", "database/telegram-bot-configuration.json", "Protected Telegram runtime configuration."),
        Included("TelegramChats", "database/telegram-chats.json", "Telegram CRM identities and preferences."),
        Included("TelegramConversationMessages", "database/telegram-conversation-messages.json", "CRM conversation history."),
        Included("TelegramOutboundMessages", "database/telegram-outbound-messages.json", "Durable outbound queue."),
        Included("TelegramUpdateReceipts", "database/telegram-update-receipts.json", "Inbound update idempotency."),
        Included("UserApiTokenRequests", "database/user-api-token-requests.json", "API-token request audit."),
        Included("UserApiTokens", "database/user-api-tokens.json", "Hashed subscriber API tokens."),
        Included("UserNotifications", "database/user-notifications.json", "Durable user notifications."),
        Included("ValidationRuns", "database/validation-runs.json", "Validation audit."),
        Included("VpnEndpoints", "database/vpn-endpoints.json", "VPN catalog."),
        Included("VpnEndpointSources", "database/vpn-endpoint-sources.json", "VPN provenance links."),
        Included("VpnSources", "database/vpn-sources.json", "VPN source catalog and fetch state.")
    ];

    private static BackupTableClassification Included(
        string tableName,
        string archiveEntry,
        string rationale) =>
        new(tableName, BackupTableDisposition.Included, archiveEntry, 8, rationale);

    private static BackupTableClassification IncludedV9(
        string tableName,
        string archiveEntry,
        string rationale) =>
        new(tableName, BackupTableDisposition.Included, archiveEntry, 9, rationale);

    private static BackupTableClassification Ephemeral(
        string tableName, string rationale, int introducedInManifestVersion = 8) =>
        new(tableName, BackupTableDisposition.Ephemeral, ArchiveEntry: null,
            introducedInManifestVersion, rationale);
}
