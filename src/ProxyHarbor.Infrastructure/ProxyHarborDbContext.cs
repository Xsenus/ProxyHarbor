using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Контекст PostgreSQL со всеми индексами и начальными источниками.</summary>
public sealed class ProxyHarborDbContext(DbContextOptions<ProxyHarborDbContext> options) : DbContext(options)
{
    public DbSet<ProxyEndpoint> Proxies => Set<ProxyEndpoint>();
    public DbSet<ProxySource> Sources => Set<ProxySource>();
    public DbSet<CollectionRun> Runs => Set<CollectionRun>();
    public DbSet<ValidationRun> ValidationRuns => Set<ValidationRun>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var proxy = modelBuilder.Entity<ProxyEndpoint>();
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
            table.HasCheckConstraint("CK_Proxies_Latency", "\"LatencyMs\" IS NULL OR \"LatencyMs\" >= 0");
            table.HasCheckConstraint("CK_Proxies_CheckCounters", "\"SuccessfulChecks\" >= 0 AND \"FailedChecks\" >= 0 AND \"ConsecutiveFailedChecks\" >= 0 AND \"ConsecutiveFailedChecks\" <= \"FailedChecks\" AND \"SuccessfulChecks\"::bigint + \"FailedChecks\"::bigint <= 2147483647");
            table.HasCheckConstraint("CK_Proxies_Lease", "(\"CheckLeaseUntil\" IS NULL) = (\"CheckLeaseId\" IS NULL)");
            table.HasCheckConstraint("CK_Proxies_DeferredAttempt", "NOT \"LastValidationDeferred\" OR \"LastValidationAttemptAt\" IS NOT NULL");
        });

        var source = modelBuilder.Entity<ProxySource>();
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
        });

        var collectionRun = modelBuilder.Entity<CollectionRun>();
        collectionRun.Property(x => x.Error).HasMaxLength(2000);
        collectionRun.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Runs_State", "\"Status\" IN ('running', 'completed', 'failed') AND ((\"Status\" = 'running') = (\"FinishedAt\" IS NULL)) AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
            table.HasCheckConstraint("CK_Runs_Counters", "\"SourcesProcessed\" >= 0 AND \"SourcesSucceeded\" >= 0 AND \"SourcesFailed\" >= 0 AND \"SourcesSkipped\" >= 0 AND \"SourcesTruncated\" >= 0 AND \"CandidatesFound\" >= 0 AND \"NewProxies\" >= 0 AND \"AliveProxies\" >= 0 AND \"SourcesSucceeded\"::bigint + \"SourcesFailed\"::bigint = \"SourcesProcessed\" AND \"SourcesTruncated\" <= \"SourcesSucceeded\" AND \"NewProxies\" <= \"CandidatesFound\"");
        });

        var validationRun = modelBuilder.Entity<ValidationRun>();
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

        var backupRun = modelBuilder.Entity<BackupRun>();
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
