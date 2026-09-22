using Microsoft.EntityFrameworkCore;

namespace ProxyHarbor.Infrastructure;

/// <summary>Итог защиты immutable backup относительно policy snapshot самого run.</summary>
public enum BackupProtectionState
{
    /// <summary>Достигнуты обязательное и желаемое числа независимых verified-копий.</summary>
    Protected,
    /// <summary>Обязательный минимум достигнут, желаемая избыточность ещё нет.</summary>
    Degraded,
    /// <summary>Минимум не достигнут, но durable staging и job позволяют продолжить.</summary>
    Pending,
    /// <summary>Минимум не достигнут и durable способа завершить доставку нет.</summary>
    Unavailable
}

/// <summary>Неизменяемая политика, сохранённая вместе с backup run.</summary>
public sealed record BackupProtectionPolicySnapshot(
    Guid PoolId,
    int PolicyVersion,
    int RequiredVerifiedCopies,
    int DesiredVerifiedCopies);

/// <summary>Одна физическая copy и доказательства, доступные evaluator без provider I/O.</summary>
public sealed record BackupProtectionCandidate(
    Guid CopyId,
    Guid DestinationId,
    string FailureDomain,
    bool IsExternal,
    bool RouteEligible,
    bool SupportsIndependentVerification,
    string State,
    string ContentSha256,
    long SizeBytes,
    int PolicyVersion,
    DateTimeOffset? VerifiedAt,
    string? NativeLocator);

/// <summary>Полный fail-closed вход оценки защиты.</summary>
public sealed record BackupProtectionEvaluationRequest(
    Guid BackupRunId,
    string ContentSha256,
    long SizeBytes,
    BackupProtectionPolicySnapshot Policy,
    bool HasDurableLocalStaging,
    bool HasDurablePendingJobs,
    IReadOnlyCollection<BackupProtectionCandidate> Candidates);

/// <summary>Объяснимый результат оценки без смешивания copies одного failure domain.</summary>
public sealed record BackupProtectionEvaluation(
    Guid BackupRunId,
    BackupProtectionState State,
    int VerifiedPhysicalCopies,
    int VerifiedIndependentCopies,
    int RequiredVerifiedCopies,
    int DesiredVerifiedCopies,
    int RequiredCopyDebt,
    int DesiredCopyDebt)
{
    /// <summary>Обязательная защита уже доказана.</summary>
    public bool RequiredProtectionMet => VerifiedIndependentCopies >= RequiredVerifiedCopies;
    /// <summary>Обязательная защита есть, но желаемая избыточность не достигнута.</summary>
    public bool Degraded => State == BackupProtectionState.Degraded;
}

/// <summary>
/// Считает только independently verified внешние copies точного ciphertext и версии policy.
/// Локальный staging никогда не считается copy и служит лишь условием безопасного pending.
/// </summary>
public sealed class BackupProtectionEvaluator(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    BackupDestinationRegistry registry)
{
    private static readonly HashSet<string> DurableJobStates = new(
        ["pending", "processing", "reconciling"], StringComparer.Ordinal);

    /// <summary>Чистая детерминированная оценка, пригодная для API, worker и тестов.</summary>
    public static BackupProtectionEvaluation Evaluate(BackupProtectionEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Candidates);
        Validate(request);

        var eligible = request.Candidates
            .Where(candidate => candidate.IsExternal &&
                candidate.RouteEligible &&
                candidate.SupportsIndependentVerification &&
                string.Equals(candidate.State, "verified", StringComparison.Ordinal) &&
                candidate.VerifiedAt.HasValue &&
                !string.IsNullOrWhiteSpace(candidate.NativeLocator) &&
                candidate.PolicyVersion == request.Policy.PolicyVersion &&
                candidate.SizeBytes == request.SizeBytes &&
                string.Equals(candidate.ContentSha256, request.ContentSha256, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(candidate.FailureDomain))
            // Повтор одной физической записи или одного destination не увеличивает защиту.
            .GroupBy(candidate => candidate.CopyId)
            .Select(group => group.First())
            .GroupBy(candidate => candidate.DestinationId)
            .Select(group => group.First())
            .ToArray();
        var independentCount = eligible
            .Select(candidate => candidate.FailureDomain.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var requiredDebt = Math.Max(0, request.Policy.RequiredVerifiedCopies - independentCount);
        var desiredDebt = Math.Max(0, request.Policy.DesiredVerifiedCopies - independentCount);
        var state = independentCount >= request.Policy.DesiredVerifiedCopies
            ? BackupProtectionState.Protected
            : independentCount >= request.Policy.RequiredVerifiedCopies
                ? BackupProtectionState.Degraded
                : request.HasDurableLocalStaging && request.HasDurablePendingJobs
                    ? BackupProtectionState.Pending
                    : BackupProtectionState.Unavailable;

        return new BackupProtectionEvaluation(
            request.BackupRunId,
            state,
            eligible.Length,
            independentCount,
            request.Policy.RequiredVerifiedCopies,
            request.Policy.DesiredVerifiedCopies,
            requiredDebt,
            desiredDebt);
    }

    /// <summary>Строит оценку из сохранённых run/copy/job/route данных без сетевого I/O.</summary>
    public async Task<BackupProtectionEvaluation> EvaluateAsync(
        Guid backupRunId,
        bool hasDurableLocalStaging,
        CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var run = await db.BackupRuns.AsNoTracking()
            .Include(item => item.Copies).ThenInclude(copy => copy.BackupDestination)
            .Include(item => item.Copies).ThenInclude(copy => copy.Jobs)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == backupRunId, token)
            ?? throw new InvalidOperationException("Backup run не найден.");
        if (run.BackupPoolId is not { } poolId ||
            run.ProtectionPolicyVersion is not { } policyVersion ||
            run.RequiredVerifiedCopies is not { } required ||
            run.DesiredVerifiedCopies is not { } desired ||
            string.IsNullOrWhiteSpace(run.ContentSha256))
            throw new InvalidOperationException("Backup run не содержит полный protection policy snapshot.");

        var routes = await db.BackupPoolDestinations.AsNoTracking()
            .Where(route => route.BackupPoolId == poolId)
            .ToDictionaryAsync(route => route.BackupDestinationId, token);
        var candidates = run.Copies.Select(copy =>
        {
            var routeEligible = routes.TryGetValue(copy.BackupDestinationId, out var route) &&
                (route.Enabled || route.Draining) &&
                RouteAllowsVerification(route.AllowedOperations) &&
                copy.BackupDestination.Enabled;
            var adapter = registry.GetRequired(copy.BackupDestination.Kind);
            return new BackupProtectionCandidate(
                copy.Id,
                copy.BackupDestinationId,
                copy.BackupDestination.FailureDomain,
                IsExternal: true,
                routeEligible,
                adapter.Capabilities.Verify.Supported,
                copy.State,
                copy.ContentSha256,
                copy.SizeBytes,
                copy.PolicyVersion,
                copy.VerifiedAt,
                copy.NativeLocator);
        }).ToArray();
        var hasDurableJobs = run.Copies.SelectMany(copy => copy.Jobs)
            .Any(job => DurableJobStates.Contains(job.State));

        return Evaluate(new BackupProtectionEvaluationRequest(
            run.Id,
            run.ContentSha256,
            run.SizeBytes,
            new BackupProtectionPolicySnapshot(poolId, policyVersion, required, desired),
            hasDurableLocalStaging,
            hasDurableJobs,
            candidates));
    }

    private static bool RouteAllowsVerification(string operations) =>
        operations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("verify", StringComparer.Ordinal);

    private static void Validate(BackupProtectionEvaluationRequest request)
    {
        if (request.BackupRunId == Guid.Empty || request.Policy.PoolId == Guid.Empty)
            throw new ArgumentException("Backup run и pool должны иметь стабильные идентификаторы.", nameof(request));
        if (request.Policy.PolicyVersion < 1 || request.Policy.RequiredVerifiedCopies is < 1 or > 16 ||
            request.Policy.DesiredVerifiedCopies < request.Policy.RequiredVerifiedCopies ||
            request.Policy.DesiredVerifiedCopies > 16)
            throw new ArgumentException("Protection policy snapshot некорректен.", nameof(request));
        if (request.SizeBytes < 0 || request.ContentSha256.Length != 64 ||
            request.ContentSha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Canonical ciphertext identity некорректна.", nameof(request));
    }
}
