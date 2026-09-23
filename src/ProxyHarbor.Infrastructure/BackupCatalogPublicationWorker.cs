using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Fenced lease одной verified S3-копии для побайтно воспроизводимого sidecar.</summary>
public sealed record BackupCatalogPublicationLease(Guid CopyId, Guid LeaseId);

/// <summary>
/// Durable at-least-once публикация каталога. Не меняет состояние самой PHB3-копии:
/// отказ sidecar остаётся отдельным видимым долгом и никогда не превращает backup в успех.
/// </summary>
public sealed class BackupCatalogPublicationProcessor(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    BackupDestinationRegistry registry,
    IOptions<BackupCatalogSigningOptions> signingOptions,
    IOptions<BackupRoutingOptions> routingOptions)
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(90);

    /// <summary>Атомарно берёт одну due verified S3-копию; expired lease безопасно переарендуется.</summary>
    public async Task<BackupCatalogPublicationLease?> TryClaimAsync(CancellationToken token)
    {
        var signing = signingOptions.Value;
        if (!routingOptions.Value.Enabled || !signing.Enabled ||
            !BackupCatalogSigningOptions.IsValid(signing))
            return null;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(token);
        var now = DateTimeOffset.UtcNow;
        await using var command = new NpgsqlCommand("""
            SELECT copy."Id"
            FROM "BackupCopies" AS copy
            JOIN "BackupDestinations" AS destination
              ON destination."Id" = copy."BackupDestinationId"
            WHERE copy."State" = 'verified'
              AND destination."Kind" = 's3'
              AND destination."Enabled"
              AND (copy."CatalogState" IS NULL OR copy."CatalogState" = 'pending'
                OR (copy."CatalogState" = 'processing' AND copy."CatalogLeaseUntil" < @now))
              AND (copy."CatalogNotBefore" IS NULL OR copy."CatalogNotBefore" <= @now)
            ORDER BY copy."VerifiedAt" DESC, copy."Id"
            FOR UPDATE OF copy SKIP LOCKED
            LIMIT 1
            """, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
        command.Parameters.AddWithValue("now", now);
        if (await command.ExecuteScalarAsync(token) is not Guid copyId)
        {
            await transaction.CommitAsync(token);
            return null;
        }
        var copy = await db.BackupCopies.SingleAsync(item => item.Id == copyId, token);
        if (copy.CatalogKeyReference is not null && copy.CatalogKeyReference != signing.KeyReference)
        {
            copy.CatalogState = "manual_review";
            copy.CatalogLeaseId = null;
            copy.CatalogLeaseUntil = null;
            copy.CatalogLastErrorCode = "KeyReferenceChanged";
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return null;
        }
        var leaseId = Guid.NewGuid();
        copy.CatalogState = "processing";
        copy.CatalogLeaseId = leaseId;
        copy.CatalogLeaseUntil = now.Add(LeaseDuration);
        copy.CatalogAttempt = checked(copy.CatalogAttempt + 1);
        copy.CatalogKeyReference ??= signing.KeyReference;
        copy.CatalogLastErrorCode = null;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new BackupCatalogPublicationLease(copyId, leaseId);
    }

    /// <summary>Публикует и подтверждает sidecar; результат сохраняется только текущим lease.</summary>
    public async Task ProcessAsync(BackupCatalogPublicationLease lease, CancellationToken hostToken)
    {
        var signing = signingOptions.Value;
        if (!routingOptions.Value.Enabled || !signing.Enabled ||
            !BackupCatalogSigningOptions.IsValid(signing) || signing.SigningKey is null)
            return;
        await using var db = await dbFactory.CreateDbContextAsync(hostToken);
        var copy = await db.BackupCopies.AsNoTracking()
            .Include(item => item.BackupRun)
            .Include(item => item.BackupDestination)
            .SingleOrDefaultAsync(item => item.Id == lease.CopyId, hostToken);
        if (copy is null || copy.State != "verified" || copy.CatalogState != "processing" ||
            copy.CatalogLeaseId != lease.LeaseId || copy.CatalogLeaseUntil <= DateTimeOffset.UtcNow ||
            copy.CatalogKeyReference != signing.KeyReference ||
            copy.BackupRun.BackupPoolId is not { } poolId)
            return;
        try
        {
            var route = await db.BackupPoolDestinations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.BackupPoolId == poolId &&
                    item.BackupDestinationId == copy.BackupDestinationId, hostToken)
                ?? throw new BackupDestinationRouteException(
                    BackupDestinationRouteRejection.RouteForbidden, "Catalog route отсутствует.");
            var adapter = registry.Resolve(copy.BackupDestination, route,
                BackupDestinationOperation.Put, copy.SizeBytes);
            _ = registry.Resolve(copy.BackupDestination, route,
                BackupDestinationOperation.Materialize, copy.SizeBytes);
            if (copy.BackupDestination.Kind != "s3" || copy.NativeLocator is null)
                throw new BackupDestinationRouteException(
                    BackupDestinationRouteRejection.UnsupportedOperation, "Catalog требует S3 locator.");
            var snapshot = await new BackupCatalogService(dbFactory).CreateForCopyAsync(
                copy.Id, signing.KeyReference, hostToken);
            var bytes = BackupCatalogService.SealForCopySidecar(snapshot, signing.SigningKey);
            var expectedKey = S3BackupObjectStorageTransport.BuildCatalogObjectKey(copy.NativeLocator);
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            var remaining = copy.CatalogLeaseUntil!.Value - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException("Catalog lease истёк.");
            deadline.CancelAfter(remaining < OperationBudget ? remaining : OperationBudget);
            var result = await adapter.PublishCatalogAsync(
                copy.BackupDestination, copy.NativeLocator, bytes, deadline.Token);
            if (result.ObjectKey != expectedKey || result.SizeBytes != bytes.Length ||
                result.Sha256 != expectedHash)
                throw new BackupDestinationOperationException(
                    new BackupDestinationFailure(BackupDestinationErrorCode.IntegrityMismatch,
                        BackupDestinationFailureDisposition.Permanent),
                    "Catalog provider не подтвердил точное содержимое.");
            var now = DateTimeOffset.UtcNow;
            _ = await db.BackupCopies.Where(item => item.Id == lease.CopyId &&
                    item.State == "verified" && item.CatalogState == "processing" &&
                    item.CatalogLeaseId == lease.LeaseId && item.CatalogLeaseUntil > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.CatalogState, "published")
                    .SetProperty(item => item.CatalogObjectKey, result.ObjectKey)
                    .SetProperty(item => item.CatalogPublishedAt, now)
                    .SetProperty(item => item.CatalogLeaseId, (Guid?)null)
                    .SetProperty(item => item.CatalogLeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(item => item.CatalogNotBefore, (DateTimeOffset?)null)
                    .SetProperty(item => item.CatalogLastErrorCode, (string?)null),
                    CancellationToken.None);
        }
        catch (BackupDestinationOperationException exception)
        {
            await FinishFailureAsync(db, lease, exception.Failure.Code,
                IsPermanent(exception.Failure.Code), CancellationToken.None);
        }
        catch (BackupDestinationRouteException)
        {
            await FinishFailureAsync(db, lease, BackupDestinationErrorCode.InvalidConfiguration,
                permanent: false, CancellationToken.None);
        }
        catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
        {
            await FinishFailureAsync(db, lease, BackupDestinationErrorCode.UnknownOutcome,
                permanent: false, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await FinishFailureAsync(db, lease, BackupDestinationErrorCode.Timeout,
                permanent: false, CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
            ArgumentException or NotSupportedException)
        {
            await FinishFailureAsync(db, lease, BackupDestinationErrorCode.InvalidConfiguration,
                permanent: false, CancellationToken.None);
        }
    }

    private static bool IsPermanent(BackupDestinationErrorCode code) => code is
        BackupDestinationErrorCode.Collision or BackupDestinationErrorCode.IntegrityMismatch or
        BackupDestinationErrorCode.UnsupportedOperation or BackupDestinationErrorCode.ProviderRejected;

    private static async Task FinishFailureAsync(
        ProxyHarborDbContext db, BackupCatalogPublicationLease lease,
        BackupDestinationErrorCode code, bool permanent, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var attempt = await db.BackupCopies.AsNoTracking()
            .Where(item => item.Id == lease.CopyId && item.CatalogLeaseId == lease.LeaseId)
            .Select(item => item.CatalogAttempt).SingleOrDefaultAsync(token);
        var delaySeconds = Math.Min(3600, 15 * (1 << Math.Min(8, Math.Max(0, attempt - 1))));
        _ = await db.BackupCopies.Where(item => item.Id == lease.CopyId &&
                item.CatalogState == "processing" && item.CatalogLeaseId == lease.LeaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.CatalogState, permanent ? "manual_review" : "pending")
                .SetProperty(item => item.CatalogNotBefore,
                    permanent ? (DateTimeOffset?)null : now.AddSeconds(delaySeconds))
                .SetProperty(item => item.CatalogLeaseId, (Guid?)null)
                .SetProperty(item => item.CatalogLeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.CatalogLastErrorCode, code.ToString()), token);
    }
}

/// <summary>Отдельный цикл для sidecar debt; без signing key и routing не делает provider I/O.</summary>
public sealed class BackupCatalogPublicationWorker(
    BackupCatalogPublicationProcessor processor,
    IOptions<BackupCatalogSigningOptions> signingOptions,
    IOptions<BackupRoutingOptions> routingOptions,
    ILogger<BackupCatalogPublicationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);
    private static readonly Action<ILogger, string, Exception?> CycleFailed =
        LoggerMessage.Define<string>(LogLevel.Error,
            new EventId(1520, "BackupCatalogPublicationCycleFailed"),
            "Backup catalog publication cycle failed: {ErrorType}");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!routingOptions.Value.Enabled || !signingOptions.Value.Enabled)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }
                var lease = await processor.TryClaimAsync(stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }
                await processor.ProcessAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Provider/DB exception message может содержать URI или connection string.
                CycleFailed(logger, exception.GetType().Name, null);
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }
}
