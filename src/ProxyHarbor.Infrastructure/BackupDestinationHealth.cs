using Microsoft.EntityFrameworkCore;

namespace ProxyHarbor.Infrastructure;

/// <summary>Решение короткоживущего operation-scoped breaker.</summary>
public sealed record BackupDestinationHealthDecision(bool Allowed, DateTimeOffset RetryAt);

/// <summary>
/// Не делает optional destination частью глобальной readiness. Локальные решения
/// дополняются недавними durable PUT и VERIFY outcomes, чтобы новая replica не начинала с нуля.
/// </summary>
public sealed class BackupDestinationHealth(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DurableLookback = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan OutcomeRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan TransientCooldown = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ConfigurationCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HalfOpenLease = TimeSpan.FromMinutes(15);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<(Guid DestinationId, BackupDestinationOperation Operation), Entry> entries = [];
    private readonly object sync = new();

    /// <summary>Проверяет только перед provider I/O; open destination не расходует PUT attempt.</summary>
    public async Task<BackupDestinationHealthDecision> TryEnterAsync(
        ProxyHarborDbContext db,
        Guid destinationId,
        BackupDestinationOperation operation,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(db);
        var now = clock.GetUtcNow();
        RecentOutcome[] durable;
        if (operation == BackupDestinationOperation.Put)
        {
            var recentJobs = await db.BackupDeliveryJobs.AsNoTracking()
                .Where(job => job.BackupCopy.BackupDestinationId == destinationId &&
                    job.BackupCopy.LastAttemptAt >= now.Subtract(DurableLookback) &&
                    job.State != "processing")
                .OrderByDescending(job => job.BackupCopy.LastAttemptAt)
                .Take(12)
                .Select(job => new RecentPutOutcome(
                    job.BackupCopyId, job.State, job.LastErrorCode,
                    job.BackupCopy.LastAttemptAt!.Value))
                .ToArrayAsync(token);
            durable = recentJobs.GroupBy(item => item.CopyId)
                .Select(group => group.First()).Take(3)
                .Select(item => new RecentOutcome(
                    item.State == "completed" && item.ErrorCode is null,
                    item.ErrorCode, item.ObservedAt)).ToArray();
        }
        else if (operation == BackupDestinationOperation.Verify)
        {
            durable = await db.BackupDestinationHealthOutcomes.AsNoTracking()
                .Where(item => item.BackupDestinationId == destinationId &&
                    item.Operation == "verify" && item.ObservedAt >= now.Subtract(DurableLookback))
                .OrderByDescending(item => item.ObservedAt)
                .ThenByDescending(item => item.Id)
                .Take(3)
                .Select(item => new RecentOutcome(item.Succeeded, item.ErrorCode, item.ObservedAt))
                .ToArrayAsync(token);
        }
        else durable = [];

        lock (sync)
        {
            var key = (destinationId, operation);
            if (!entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                entries.Add(key, entry);
            }
            ApplyDurable(entry, durable, now);
            if (entry.OpenUntil > now)
                return new BackupDestinationHealthDecision(false, entry.OpenUntil);
            if (entry.HalfOpenUntil > now)
                return new BackupDestinationHealthDecision(false, entry.HalfOpenUntil);
            if (entry.OpenUntil != default)
            {
                entry.HalfOpenUntil = now.Add(HalfOpenLease);
                return new BackupDestinationHealthDecision(true, now);
            }
            return new BackupDestinationHealthDecision(true, now);
        }
    }

    /// <summary>Удаляет только наблюдения старше health window; вызывается не чаще часа на worker.</summary>
    public static Task<int> PruneOldOutcomesAsync(ProxyHarborDbContext db, CancellationToken token)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(OutcomeRetention);
        return db.BackupDestinationHealthOutcomes
            .Where(item => item.ObservedAt < cutoff)
            .ExecuteDeleteAsync(token);
    }

    /// <summary>Успех операции закрывает только breaker той же destination и операции.</summary>
    public void RecordSuccess(Guid destinationId, BackupDestinationOperation operation)
    {
        lock (sync)
        {
            var entry = GetOrAdd(destinationId, operation);
            entry.LastLocalOutcomeAt = clock.GetUtcNow();
            entry.Failures = 0;
            entry.OpenUntil = default;
            entry.HalfOpenUntil = default;
        }
    }

    /// <summary>Освобождает half-open слот, если provider call так и не был начат.</summary>
    public void ReleaseWithoutOutcome(Guid destinationId, BackupDestinationOperation operation)
    {
        lock (sync)
        {
            var entry = GetOrAdd(destinationId, operation);
            entry.HalfOpenUntil = default;
        }
    }

    /// <summary>Учитывает только сбои destination, не content-specific collision/integrity.</summary>
    public void RecordFailure(
        Guid destinationId,
        BackupDestinationOperation operation,
        BackupDestinationErrorCode code)
    {
        var now = clock.GetUtcNow();
        lock (sync)
        {
            var entry = GetOrAdd(destinationId, operation);
            if (now - entry.LastLocalOutcomeAt > ObservationWindow)
                entry.Failures = 0;
            entry.LastLocalOutcomeAt = now;
            if (IsConfigurationFailure(code))
            {
                entry.Failures = 0;
                entry.OpenUntil = now.Add(ConfigurationCooldown);
                entry.HalfOpenUntil = default;
            }
            else if (IsTransientFailure(code))
            {
                entry.Failures++;
                if (entry.Failures >= 2 || entry.HalfOpenUntil > now)
                {
                    entry.OpenUntil = now.Add(TransientCooldown);
                    entry.HalfOpenUntil = default;
                }
            }
        }
    }

    private static void ApplyDurable(Entry entry, RecentOutcome[] durable, DateTimeOffset now)
    {
        if (durable.Length == 0 || durable[0].ObservedAt <= entry.LastLocalOutcomeAt)
            return;
        var latest = durable[0];
        if (latest.Succeeded)
        {
            entry.Failures = 0;
            entry.OpenUntil = default;
            entry.HalfOpenUntil = default;
            entry.LastLocalOutcomeAt = latest.ObservedAt;
            return;
        }
        if (!Enum.TryParse<BackupDestinationErrorCode>(latest.ErrorCode, out var code))
            return;
        if (IsConfigurationFailure(code))
        {
            var until = latest.ObservedAt.Add(ConfigurationCooldown);
            if (until > now) entry.OpenUntil = Max(entry.OpenUntil, until);
        }
        else if (IsTransientFailure(code) && durable.Length >= 2 &&
            Enum.TryParse<BackupDestinationErrorCode>(durable[1].ErrorCode, out var previous) &&
            IsTransientFailure(previous) &&
            latest.ObservedAt - durable[1].ObservedAt <= ObservationWindow)
        {
            var until = latest.ObservedAt.Add(TransientCooldown);
            if (until > now) entry.OpenUntil = Max(entry.OpenUntil, until);
        }
        if (entry.OpenUntil > now)
            entry.LastLocalOutcomeAt = latest.ObservedAt;
    }

    private Entry GetOrAdd(Guid destinationId, BackupDestinationOperation operation)
    {
        var key = (destinationId, operation);
        if (!entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            entries.Add(key, entry);
        }
        return entry;
    }

    private static bool IsConfigurationFailure(BackupDestinationErrorCode code) =>
        code is BackupDestinationErrorCode.AuthenticationFailed or
            BackupDestinationErrorCode.AuthorizationFailed or
            BackupDestinationErrorCode.QuotaExceeded or
            BackupDestinationErrorCode.InvalidConfiguration;

    private static bool IsTransientFailure(BackupDestinationErrorCode code) =>
        code is BackupDestinationErrorCode.RateLimited or
            BackupDestinationErrorCode.Timeout or
            BackupDestinationErrorCode.Unavailable or
            BackupDestinationErrorCode.UnknownOutcome;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private sealed class Entry
    {
        public DateTimeOffset LastLocalOutcomeAt { get; set; }
        public DateTimeOffset OpenUntil { get; set; }
        public DateTimeOffset HalfOpenUntil { get; set; }
        public int Failures { get; set; }
    }

    private sealed record RecentPutOutcome(
        Guid CopyId, string State, string? ErrorCode, DateTimeOffset ObservedAt);

    private sealed record RecentOutcome(bool Succeeded, string? ErrorCode, DateTimeOffset ObservedAt);
}
