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
public sealed class AdminController(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    ProxyCollector collector,
    ProxyValidator validator,
    BackupService backup,
    IOptions<BackupOptions> backupOptions,
    IOptions<CollectorOptions> collectorOptions) : ControllerBase
{
    [HttpGet("sources")]
    public async Task<ActionResult<IReadOnlyList<SourceResponse>>> Sources(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var sources = await db.Sources.AsNoTracking().OrderBy(x => x.Priority).ToListAsync(token);
        return Ok(sources.Select(SourceResponse.From).ToArray());
    }

    [HttpGet("sources/{id:guid}")]
    public async Task<ActionResult<SourceResponse>> GetSource(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        return source is null ? NotFound() : Ok(SourceResponse.From(source));
    }

    [HttpPost("sources")]
    public async Task<ActionResult<SourceResponse>> CreateSource([FromBody] SourceRequest request, CancellationToken token)
    {
        if (!await NetworkSafety.IsSafePublicHttpsUrlAsync(request.Url, token))
            return Problem("Разрешены только публичные HTTPS-адреса источников без fragment.", statusCode: 400);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var normalizedUrl = new Uri(request.Url, UriKind.Absolute).AbsoluteUri;
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
    public async Task<IActionResult> UpdateSource(Guid id, [FromBody] SourceRequest request, CancellationToken token)
    {
        var normalizedUrl = new Uri(request.Url, UriKind.Absolute).AbsoluteUri;
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
    public async Task<IActionResult> DeleteSource(Guid id, CancellationToken token)
    {
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

    [HttpGet("diagnostics")]
    public async Task<IActionResult> Diagnostics(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var now = DateTimeOffset.UtcNow;
        var validationWindowStart = now.AddMinutes(-5);
        var databaseBytes = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"")
            .SingleAsync(token);
        var queue = await db.Proxies.AsNoTracking().GroupBy(_ => 1).Select(x => new
        {
            total = x.Count(),
            leased = x.Count(proxy => proxy.CheckLeaseUntil > now),
            neverChecked = x.Count(proxy => proxy.LastCheckedAt == null),
            neverAttempted = x.Count(proxy => proxy.LastValidationAttemptAt == null),
            due = x.Count(proxy => proxy.NextCheckAt == null || proxy.NextCheckAt <= now),
            scheduled = x.Count(proxy => proxy.NextCheckAt > now),
            repeatedlyFailing = x.Count(proxy => proxy.ConsecutiveFailedChecks >= 3),
            lastAttemptAt = x.Max(proxy => proxy.LastValidationAttemptAt)
        }).FirstOrDefaultAsync(token);
        var validationRuns = await db.ValidationRuns.AsNoTracking()
            .Where(run => run.FinishedAt >= validationWindowStart || run.Status == "running")
            .ToListAsync(token);
        var validationTelemetry = ValidationTelemetry.Calculate(
            validationRuns, validationWindowStart, queue?.due ?? 0);
        var validationQueue = queue is null ? null : new
        {
            queue.total,
            queue.leased,
            queue.neverChecked,
            queue.neverAttempted,
            queue.due,
            queue.scheduled,
            queue.repeatedlyFailing,
            attemptsLastFiveMinutes = validationTelemetry.Attempts,
            checkedLastFiveMinutes = validationTelemetry.Checked,
            aliveLastFiveMinutes = validationTelemetry.Alive,
            deferredLastFiveMinutes = validationTelemetry.Deferred,
            failedRunsLastFiveMinutes = validationTelemetry.FailedRuns,
            activeRuns = validationTelemetry.ActiveRuns,
            concurrencyLimit = collectorOptions.Value.ValidationConcurrency,
            batchSize = collectorOptions.Value.ValidationBatchSize,
            checksPerSecond = validationTelemetry.ChecksPerSecond,
            estimatedDrainSeconds = validationTelemetry.EstimatedDrainSeconds,
            queue.lastAttemptAt
        };
        var builtInUrls = BuiltInSourceCatalog.Sources.Select(source => source.Url).ToArray();
        var sourceCatalog = SourceCatalogHealth.Calculate(
            await db.Sources.AsNoTracking().Where(source => builtInUrls.Contains(source.Url)).ToListAsync(token),
            now,
            SourceCatalogHealth.FreshnessWindow(collectorOptions.Value.CollectionIntervalMinutes));
        var recentRuns = await db.Runs.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(10).ToListAsync(token);
        var recentValidationRuns = await db.ValidationRuns.AsNoTracking()
            .OrderByDescending(x => x.StartedAt).Take(10).ToListAsync(token);
        var recentBackups = await db.BackupRuns.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(10).ToListAsync(token);
        return Ok(new
        {
            serverTime = now,
            databaseBytes,
            validationQueue,
            sourceCatalog,
            recentRuns,
            recentValidationRuns,
            recentBackups
        });
    }

    [HttpPost("collect")]
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
    public async Task<IActionResult> Validate(CancellationToken token)
    {
        (int Checked, int Alive, int Deferred) result;
        try { result = await validator.ValidateBatchAsync(token); }
        catch (OperationAlreadyRunningException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message, Status = 409 });
        }
        return Ok(new { result.Checked, result.Alive, result.Deferred });
    }

    [HttpPost("backup")]
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
        return Ok(new { created = Path.GetFileName(path), sentToTelegram = sent });
    }
}

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
