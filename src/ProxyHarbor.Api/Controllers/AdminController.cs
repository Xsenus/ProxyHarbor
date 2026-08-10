using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Операции администратора; доступ ограничивает middleware по X-Admin-Key.</summary>
[ApiController, Route("api/v1/admin"), EnableRateLimiting("admin")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class AdminController(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    ProxyCollector collector,
    ProxyValidator validator,
    BackupService backup,
    ISourceCatalogMutationCoordinator sourceMutationCoordinator,
    IOptions<BackupOptions> backupOptions,
    IOptions<CollectorOptions> collectorOptions) : ControllerBase
{
    [HttpGet("sources")]
    [ProducesResponseType<IReadOnlyList<SourceResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SourceResponse>>> Sources(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var sources = await db.Sources.AsNoTracking().OrderBy(x => x.Priority).ToListAsync(token);
        return Ok(sources.Select(SourceResponse.From).ToArray());
    }

    [HttpGet("sources/{id:guid}")]
    [ProducesResponseType<SourceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SourceResponse>> GetSource(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        return source is null ? NotFound() : Ok(SourceResponse.From(source));
    }

    [HttpPost("sources")]
    [ProducesResponseType<SourceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SourceResponse>> CreateSource([FromBody] SourceRequest request, CancellationToken token)
    {
        if (!NetworkSafety.TryParseSafeHttpsUrl(request.Url, out var requestedUri) ||
            !await NetworkSafety.IsSafePublicHttpsUrlAsync(requestedUri.AbsoluteUri, token))
            return Problem("Разрешены только публичные HTTPS-адреса источников без fragment.", statusCode: 400);
        await using var mutationLease = await sourceMutationCoordinator.TryAcquireAsync(token);
        if (mutationLease is null) return SourceMutationConflict();
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var normalizedUrl = requestedUri.AbsoluteUri;
        if (await db.Sources.AnyAsync(x => x.Url == normalizedUrl, token))
            return Conflict(new ProblemDetails { Title = "Источник с таким URL уже существует", Status = 409 });
        var source = new ProxySource { Name = request.Name.Trim(), Url = normalizedUrl, DefaultProtocol = request.Protocol, Priority = request.Priority, Enabled = request.Enabled };
        db.Sources.Add(source);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Conflict(new ProblemDetails { Title = "Источник с таким URL уже существует", Status = 409 });
        }
        var response = SourceResponse.From(source);
        return CreatedAtAction(nameof(GetSource), new { id = source.Id }, response);
    }

    [HttpPut("sources/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateSource(Guid id, [FromBody] SourceRequest request, CancellationToken token)
    {
        if (!NetworkSafety.TryParseSafeHttpsUrl(request.Url, out var requestedUri))
            return Problem("Разрешены только публичные HTTPS-адреса источников без fragment.", statusCode: 400);
        await using var mutationLease = await sourceMutationCoordinator.TryAcquireAsync(token);
        if (mutationLease is null) return SourceMutationConflict();
        var normalizedUrl = requestedUri.AbsoluteUri;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var source = await db.Sources.FindAsync([id], token);
        if (source is null) return NotFound();
        var builtIn = BuiltInSourceCatalog.FindByUrl(source.Url);
        if (builtIn is not null &&
            (!string.Equals(normalizedUrl, builtIn.Url, StringComparison.Ordinal) ||
                request.Protocol != builtIn.Protocol ||
                !string.Equals(request.Name.Trim(), builtIn.Name, StringComparison.Ordinal) ||
                request.Priority != builtIn.Rank * 10))
            return Conflict(new ProblemDetails
            {
                Title = "Метаданные встроенного источника неизменяемы; его можно только включить или отключить",
                Status = 409
            });
        // Канонический built-in уже прошёл release-аудит и не меняется этим запросом.
        // Для пользовательского endpoint проверяем актуальный DNS до сохранения.
        if (builtIn is null && !await NetworkSafety.IsSafePublicHttpsUrlAsync(normalizedUrl, token))
            return Problem("Разрешены только публичные HTTPS-адреса источников без fragment.", statusCode: 400);
        if (await db.Sources.AnyAsync(x => x.Id != id && x.Url == normalizedUrl, token))
            return Conflict(new ProblemDetails { Title = "Источник с таким URL уже существует", Status = 409 });
        var endpointChanged = !string.Equals(source.Url, normalizedUrl, StringComparison.Ordinal) ||
            source.DefaultProtocol != request.Protocol;
        var reenabled = request.Enabled && !source.Enabled;
        source.Name = request.Name.Trim(); source.Url = normalizedUrl; source.DefaultProtocol = request.Protocol;
        source.Priority = request.Priority; source.Enabled = request.Enabled;
        if (endpointChanged)
        {
            source.LastFetchedAt = null;
            source.LastSucceededAt = null;
            source.LastContentFetchedAt = null;
            source.LastItemCount = 0;
            source.LastResultTruncated = false;
            source.ConsecutiveFailures = 0;
            source.LastError = null;
            source.HttpETag = null;
            source.HttpLastModifiedAt = null;
        }
        if (endpointChanged || reenabled) source.NextFetchAt = null;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Conflict(new ProblemDetails { Title = "Источник с таким URL уже существует", Status = 409 });
        }
        return NoContent();
    }

    [HttpDelete("sources/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteSource(Guid id, CancellationToken token)
    {
        await using var mutationLease = await sourceMutationCoordinator.TryAcquireAsync(token);
        if (mutationLease is null) return SourceMutationConflict();
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var source = await db.Sources.FindAsync([id], token);
        if (source is null) return NotFound();
        // Встроенные feed'ы синхронизируются при старте, поэтому DELETE для них означает устойчивое отключение.
        if (BuiltInSourceCatalog.FindByUrl(source.Url) is not null)
            source.Enabled = false;
        else
            db.Sources.Remove(source);
        await db.SaveChangesAsync(token);
        return NoContent();
    }

    private ConflictObjectResult SourceMutationConflict() => Conflict(new ProblemDetails
    {
        Title = "Сбор источников уже выполняется; повторите изменение после его завершения",
        Status = StatusCodes.Status409Conflict
    });

    [HttpGet("diagnostics")]
    [ProducesResponseType<DiagnosticsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiagnosticsResponse>> Diagnostics(CancellationToken requestToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(requestToken);
        return await BufferedReadSnapshot.ExecuteAsync(
            db, token => GetDiagnosticsSnapshotAsync(db, token), requestToken);
    }

    /// <summary>Строит весь database-derived операторский ответ внутри одного read snapshot.</summary>
    private async Task<ActionResult<DiagnosticsResponse>> GetDiagnosticsSnapshotAsync(
        ProxyHarborDbContext db,
        CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var validationWindowStart = now.AddMinutes(-5);
        var unseenRetentionCutoff = now.AddDays(-Math.Max(1, collectorOptions.Value.DeadRetentionDays));
        var databaseBytes = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"")
            .SingleAsync(token);
        var queue = await db.Proxies.AsNoTracking().GroupBy(_ => 1).Select(x => new
        {
            total = x.Count(),
            leased = x.Count(proxy => proxy.CheckLeaseUntil >= now),
            neverChecked = x.Count(proxy => proxy.LastCheckedAt == null),
            neverAttempted = x.Count(proxy => proxy.LastValidationAttemptAt == null),
            due = x.Count(proxy => (proxy.NextCheckAt == null || proxy.NextCheckAt <= now) &&
                (proxy.CheckLeaseUntil == null || proxy.CheckLeaseUntil < now)),
            scheduled = x.Count(proxy => proxy.NextCheckAt > now),
            repeatedlyFailing = x.Count(proxy => proxy.ConsecutiveFailedChecks >= 3),
            staleUnseen = x.Count(proxy =>
                (proxy.Status == ProxyStatus.Pending || proxy.Status == ProxyStatus.Dead) &&
                proxy.LastSeenAt < unseenRetentionCutoff &&
                (proxy.CheckLeaseUntil == null || proxy.CheckLeaseUntil < now)),
            lastAttemptAt = x.Max(proxy => proxy.LastValidationAttemptAt)
        }).SingleOrDefaultAsync(token);
        var validationRuns = await db.ValidationRuns.AsNoTracking()
            .Where(run => run.FinishedAt >= validationWindowStart || run.Status == "running")
            .ToListAsync(token);
        var validationTelemetry = ValidationTelemetry.Calculate(
            validationRuns, validationWindowStart, queue?.due ?? 0);
        var validationQueue = queue is null ? null : new ValidationQueueResponse(
            queue.total,
            queue.leased,
            queue.neverChecked,
            queue.neverAttempted,
            queue.due,
            queue.scheduled,
            queue.repeatedlyFailing,
            queue.staleUnseen,
            validationTelemetry.Attempts,
            validationTelemetry.Checked,
            validationTelemetry.Alive,
            validationTelemetry.Deferred,
            validationTelemetry.FailedRuns,
            validationTelemetry.ActiveRuns,
            collectorOptions.Value.ValidationConcurrency,
            collectorOptions.Value.ValidationBatchSize,
            validationTelemetry.ChecksPerSecond,
            validationTelemetry.EstimatedDrainSeconds,
            queue.lastAttemptAt);
        var builtInUrls = BuiltInSourceCatalog.Sources.Select(source => source.Url).ToArray();
        var sourceCatalog = SourceCatalogHealth.Calculate(
            await db.Sources.AsNoTracking().Where(source => builtInUrls.Contains(source.Url)).ToListAsync(token),
            now,
            SourceCatalogHealth.FreshnessWindow(collectorOptions.Value.CollectionIntervalMinutes));
        var recentRuns = await db.Runs.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(10).ToListAsync(token);
        var recentValidationRuns = await db.ValidationRuns.AsNoTracking()
            .OrderByDescending(x => x.StartedAt).Take(10).ToListAsync(token);
        var recentBackups = await db.BackupRuns.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(10).ToListAsync(token);
        return Ok(new DiagnosticsResponse(
            now,
            databaseBytes,
            validationQueue,
            sourceCatalog,
            recentRuns,
            recentValidationRuns,
            recentBackups));
    }

    [HttpPost("collect")]
    [ProducesResponseType<CollectionRun>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Collect(CancellationToken token)
    {
        // Ручной запуск является полным аудитом и намеренно игнорирует background backoff.
        try { return Ok(await collector.CollectAsync(token, forceAllSources: true)); }
        catch (OperationAlreadyRunningException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message, Status = 409 });
        }
    }

    [HttpPost("validate")]
    [ProducesResponseType<ValidationTriggerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Validate(CancellationToken token)
    {
        (int Checked, int Alive, int Deferred) result;
        try { result = await validator.ValidateBatchAsync(token); }
        catch (OperationAlreadyRunningException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message, Status = 409 });
        }
        return Ok(new ValidationTriggerResponse(result.Checked, result.Alive, result.Deferred));
    }

    [HttpPost("backup")]
    [ProducesResponseType<BackupTriggerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Backup(CancellationToken token)
    {
        string path;
        try { path = await backup.CreateAndSendAsync(token); }
        catch (OperationAlreadyRunningException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message, Status = 409 });
        }
        var sent = !string.IsNullOrWhiteSpace(backupOptions.Value.TelegramBotToken) &&
            !string.IsNullOrWhiteSpace(backupOptions.Value.TelegramChatId);
        return Ok(new BackupTriggerResponse(Path.GetFileName(path), sent));
    }
}

/// <summary>Результат одного ручного validation batch.</summary>
public sealed record ValidationTriggerResponse(int Checked, int Alive, int Deferred);

/// <summary>Результат ручного создания и опциональной Telegram-доставки backup.</summary>
public sealed record BackupTriggerResponse(string Created, bool SentToTelegram);

/// <summary>Текущий backlog без уже арендованных строк и rolling validation telemetry.</summary>
public sealed record ValidationQueueResponse(
    int Total,
    int Leased,
    int NeverChecked,
    int NeverAttempted,
    int Due,
    int Scheduled,
    int RepeatedlyFailing,
    int StaleUnseen,
    int AttemptsLastFiveMinutes,
    int CheckedLastFiveMinutes,
    int AliveLastFiveMinutes,
    int DeferredLastFiveMinutes,
    int FailedRunsLastFiveMinutes,
    int ActiveRuns,
    int ConcurrencyLimit,
    int BatchSize,
    double ChecksPerSecond,
    long? EstimatedDrainSeconds,
    DateTimeOffset? LastAttemptAt);

/// <summary>Типизированный операторский snapshot для React и generated OpenAPI clients.</summary>
public sealed record DiagnosticsResponse(
    DateTimeOffset ServerTime,
    long DatabaseBytes,
    ValidationQueueResponse? ValidationQueue,
    SourceCatalogSnapshot SourceCatalog,
    IReadOnlyList<CollectionRun> RecentRuns,
    IReadOnlyList<ValidationRun> RecentValidationRuns,
    IReadOnlyList<BackupRun> RecentBackups);

/// <summary>Изменяемые поля источника.</summary>
public sealed record SourceRequest(
    [Required, StringLength(120, MinimumLength = 2)] string Name,
    [Required, StringLength(2048), Url] string Url,
    [EnumDataType(typeof(ProxyProtocol))] ProxyProtocol Protocol,
    [Range(-10_000, 10_000)] int Priority = 100,
    bool Enabled = true) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length < 2)
            yield return new ValidationResult(
                "Имя источника после удаления пробелов должно содержать минимум два символа.",
                [nameof(Name)]);
        if (!Enum.IsDefined(Protocol))
            yield return new ValidationResult("Неизвестный протокол источника.", [nameof(Protocol)]);
        if (!NetworkSafety.TryParseSafeHttpsUrl(Url, out _))
            yield return new ValidationResult(
                "URL источника должен быть bounded HTTPS endpoint без credentials, нестандартного порта или fragment.",
                [nameof(Url)]);
    }
}

/// <summary>Источник вместе с неизменяемой принадлежностью к встроенному каталогу.</summary>
public sealed record SourceResponse(
    Guid Id,
    string Name,
    string Url,
    ProxyProtocol DefaultProtocol,
    bool Enabled,
    int Priority,
    DateTimeOffset? LastFetchedAt,
    DateTimeOffset? LastSucceededAt,
    DateTimeOffset? LastContentFetchedAt,
    DateTimeOffset? NextFetchAt,
    int LastItemCount,
    bool LastResultTruncated,
    int ConsecutiveFailures,
    string? LastError,
    bool IsBuiltIn,
    string? Provider,
    string? ProviderIdentity,
    int? CatalogRank)
{
    /// <summary>Обогащает изменяемую запись БД каноническими метаданными каталога.</summary>
    public static SourceResponse From(ProxySource source)
    {
        var builtIn = BuiltInSourceCatalog.FindByUrl(source.Url);
        return new SourceResponse(
            source.Id,
            source.Name,
            source.Url,
            source.DefaultProtocol,
            source.Enabled,
            source.Priority,
            source.LastFetchedAt,
            source.LastSucceededAt,
            source.LastContentFetchedAt,
            source.NextFetchAt,
            source.LastItemCount,
            source.LastResultTruncated,
            source.ConsecutiveFailures,
            source.LastError,
            builtIn is not null,
            builtIn?.Provider,
            builtIn?.ProviderIdentity,
            builtIn?.Rank);
    }
}
