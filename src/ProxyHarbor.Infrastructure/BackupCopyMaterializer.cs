using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Результат чтения одной подтверждённой физической копии.</summary>
public sealed record BackupCopyMaterialization(
    Guid BackupRunId,
    Guid BackupCopyId,
    Guid BackupDestinationId,
    string Path,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Читает только verified копии immutable run через разрешённый pool route.
/// Не делает restore и не меняет source of truth для production данных.
/// </summary>
public sealed class BackupCopyMaterializer(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    BackupDestinationRegistry registry,
    BackupDestinationHealth health)
{
    /// <summary>Пробует независимые пригодные copies, не смешивая байты разных источников.</summary>
    public async Task<BackupCopyMaterialization> MaterializeAsync(
        Guid backupRunId,
        string finalPath,
        TimeSpan overallDeadline,
        CancellationToken token)
    {
        if (backupRunId == Guid.Empty || string.IsNullOrWhiteSpace(finalPath) ||
            overallDeadline <= TimeSpan.Zero || overallDeadline > TimeSpan.FromMinutes(30))
            throw new ArgumentException("Некорректный запрос чтения backup.");
        var fullFinalPath = Path.GetFullPath(finalPath);
        var directory = Path.GetDirectoryName(fullFinalPath)
            ?? throw new ArgumentException("Каталог backup-файла не определён.", nameof(finalPath));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Каталог backup-файла не существует.");
        if (File.Exists(fullFinalPath))
            throw new IOException("Целевой backup-файл уже существует.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(overallDeadline);
        var started = TimeProvider.System.GetTimestamp();
        await using var db = await dbFactory.CreateDbContextAsync(deadline.Token);
        var run = await db.BackupRuns.AsNoTracking()
            .Include(item => item.Copies).ThenInclude(copy => copy.BackupDestination)
            .SingleOrDefaultAsync(item => item.Id == backupRunId, deadline.Token)
            ?? throw new InvalidOperationException("Backup run не найден.");
        if (run.Status != "completed" || run.BackupPoolId is not { } poolId ||
            run.ProtectionPolicyVersion is not { } policyVersion ||
            string.IsNullOrWhiteSpace(run.FileName) ||
            string.IsNullOrWhiteSpace(run.ContentSha256) || run.SizeBytes <= 0)
            throw new InvalidOperationException("Backup run не имеет полного immutable snapshot.");

        var routes = await db.BackupPoolDestinations.AsNoTracking()
            .Where(route => route.BackupPoolId == poolId)
            .ToDictionaryAsync(route => route.BackupDestinationId, deadline.Token);
        var candidates = run.Copies
            .Where(copy => copy.State == "verified" && copy.VerifiedAt is not null &&
                copy.PolicyVersion == policyVersion && copy.SizeBytes == run.SizeBytes &&
                string.Equals(copy.ContentSha256, run.ContentSha256, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(copy.NativeLocator) &&
                routes.ContainsKey(copy.BackupDestinationId))
            .OrderBy(copy => routes[copy.BackupDestinationId].Priority)
            .ThenBy(copy => copy.BackupDestination.Priority)
            .ThenByDescending(copy => copy.VerifiedAt)
            .ThenBy(copy => copy.Id)
            .Select(copy => (Copy: copy, Route: routes[copy.BackupDestinationId]))
            .Where(item => IsReadable(item.Copy, item.Route, registry))
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("Нет разрешённой verified-копии для чтения.");

        for (var index = 0; index < candidates.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var (copy, route) = candidates[index];
            var adapter = registry.Resolve(
                copy.BackupDestination, route, BackupDestinationOperation.Materialize, run.SizeBytes);
            var decision = await health.TryEnterAsync(
                db, copy.BackupDestinationId, BackupDestinationOperation.Materialize, deadline.Token);
            if (!decision.Allowed) continue;

            var remaining = overallDeadline - TimeProvider.System.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                health.ReleaseWithoutOutcome(copy.BackupDestinationId, BackupDestinationOperation.Materialize);
                break;
            }
            var share = TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / (candidates.Length - index)));
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            attempt.CancelAfter(share);
            var candidatePath = Path.Combine(
                directory, $".{Path.GetFileName(fullFinalPath)}.{Guid.NewGuid():N}.candidate");
            try
            {
                var materialized = await adapter.MaterializeAsync(
                    copy.BackupDestination, run.FileName, copy.NativeLocator!, candidatePath,
                    run.ContentSha256, run.SizeBytes, attempt.Token);
                if (!string.Equals(Path.GetFullPath(materialized.Path), candidatePath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    materialized.SizeBytes != run.SizeBytes ||
                    !string.Equals(materialized.Sha256, run.ContentSha256, StringComparison.Ordinal))
                    throw new BackupDestinationOperationException(
                        new BackupDestinationFailure(
                            BackupDestinationErrorCode.IntegrityMismatch,
                            BackupDestinationFailureDisposition.Permanent),
                        "Результат чтения не совпадает с immutable backup identity.");
                await using (var stream = new FileStream(
                    materialized.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, attempt.Token));
                    if (stream.Length != run.SizeBytes ||
                        !string.Equals(actualHash, run.ContentSha256, StringComparison.Ordinal))
                        throw new BackupDestinationOperationException(
                            new BackupDestinationFailure(
                                BackupDestinationErrorCode.IntegrityMismatch,
                                BackupDestinationFailureDisposition.Permanent),
                            "Материализованный файл не совпадает с immutable backup identity.");
                }
                attempt.Token.ThrowIfCancellationRequested();
                File.Move(candidatePath, fullFinalPath);
                health.RecordSuccess(copy.BackupDestinationId, BackupDestinationOperation.Materialize);
                return new BackupCopyMaterialization(
                    run.Id, copy.Id, copy.BackupDestinationId,
                    fullFinalPath, materialized.SizeBytes, materialized.Sha256);
            }
            catch (BackupDestinationOperationException exception)
            {
                health.RecordFailure(
                    copy.BackupDestinationId, BackupDestinationOperation.Materialize, exception.Failure.Code);
                if (exception.Failure.Code == BackupDestinationErrorCode.IntegrityMismatch)
                    await QuarantineAsync(db, copy, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                health.RecordFailure(
                    copy.BackupDestinationId, BackupDestinationOperation.Materialize,
                    BackupDestinationErrorCode.Timeout);
            }
            catch (IOException)
            {
                health.ReleaseWithoutOutcome(
                    copy.BackupDestinationId, BackupDestinationOperation.Materialize);
                throw;
            }
            finally
            {
                try { File.Delete(candidatePath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Cleanup is best-effort; never remove caller's final path.
                }
            }
        }
        token.ThrowIfCancellationRequested();
        throw new BackupDestinationOperationException(
            new BackupDestinationFailure(
                BackupDestinationErrorCode.Unavailable,
                BackupDestinationFailureDisposition.Retryable),
            "Ни одна разрешённая verified-копия не была прочитана.");
    }

    private static bool IsReadable(
        BackupCopy copy,
        BackupPoolDestination route,
        BackupDestinationRegistry registry)
    {
        try
        {
            _ = registry.Resolve(
                copy.BackupDestination, route,
                BackupDestinationOperation.Materialize, copy.SizeBytes);
            return true;
        }
        catch (BackupDestinationRouteException)
        {
            return false;
        }
    }

    private static async Task QuarantineAsync(
        ProxyHarborDbContext db,
        BackupCopy copy,
        CancellationToken token)
    {
        _ = await db.BackupCopies
            .Where(item => item.Id == copy.Id && item.State == "verified" &&
                item.ContentSha256 == copy.ContentSha256 &&
                item.NativeLocator == copy.NativeLocator &&
                item.VerifiedAt == copy.VerifiedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, "quarantined")
                .SetProperty(item => item.VerifiedAt, (DateTimeOffset?)null)
                .SetProperty(item => item.LastErrorCode,
                    BackupDestinationErrorCode.IntegrityMismatch.ToString()), token);
    }
}
