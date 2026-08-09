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

        var source = modelBuilder.Entity<ProxySource>();
        source.HasIndex(x => x.Url).IsUnique();
        source.HasIndex(x => new { x.Enabled, x.ConsecutiveFailures });
        source.HasIndex(x => new { x.Enabled, x.NextFetchAt });
        source.Property(x => x.Name).HasMaxLength(120);
        source.Property(x => x.Url).HasMaxLength(2048);
        source.Property(x => x.LastError).HasMaxLength(500);

        modelBuilder.Entity<CollectionRun>().Property(x => x.Error).HasMaxLength(2000);

        var validationRun = modelBuilder.Entity<ValidationRun>();
        validationRun.HasIndex(x => x.LeaseId).IsUnique();
        validationRun.HasIndex(x => x.StartedAt);
        validationRun.HasIndex(x => new { x.Status, x.FinishedAt });
        validationRun.Property(x => x.Status).HasMaxLength(32);
        validationRun.Property(x => x.Error).HasMaxLength(2000);

        var backupRun = modelBuilder.Entity<BackupRun>();
        backupRun.HasIndex(x => x.StartedAt);
        backupRun.HasIndex(x => new { x.Status, x.FinishedAt });
        backupRun.Property(x => x.Status).HasMaxLength(32);
        backupRun.Property(x => x.FileName).HasMaxLength(255);
        backupRun.Property(x => x.Error).HasMaxLength(2000);
    }
}
