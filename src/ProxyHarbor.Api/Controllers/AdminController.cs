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
    IOptions<BackupOptions> backupOptions) : ControllerBase
{
    [HttpGet("sources")]
    public async Task<ActionResult<IReadOnlyList<ProxySource>>> Sources(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return Ok(await db.Sources.AsNoTracking().OrderBy(x => x.Priority).ToListAsync(token));
    }

    [HttpPost("sources")]
    public async Task<ActionResult<ProxySource>> CreateSource([FromBody] SourceRequest request, CancellationToken token)
    {
        if (!await NetworkSafety.IsSafePublicHttpsUrlAsync(request.Url, token))
            return Problem("Разрешены только публичные HTTPS-адреса источников.", statusCode: 400);
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
        return CreatedAtAction(nameof(Sources), new { id = source.Id }, source);
    }

    [HttpPut("sources/{id:guid}")]
    public async Task<IActionResult> UpdateSource(Guid id, [FromBody] SourceRequest request, CancellationToken token)
    {
        if (!await NetworkSafety.IsSafePublicHttpsUrlAsync(request.Url, token))
            return Problem("Разрешены только публичные HTTPS-адреса источников.", statusCode: 400);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var source = await db.Sources.FindAsync([id], token);
        if (source is null) return NotFound();
        var normalizedUrl = new Uri(request.Url, UriKind.Absolute).AbsoluteUri;
        if (await db.Sources.AnyAsync(x => x.Id != id && x.Url == normalizedUrl, token))
            return Conflict(new ProblemDetails { Title = "Источник с таким URL уже существует", Status = 409 });
        var endpointChanged = !string.Equals(source.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase) ||
            source.DefaultProtocol != request.Protocol;
        var reenabled = request.Enabled && !source.Enabled;
        source.Name = request.Name.Trim(); source.Url = normalizedUrl; source.DefaultProtocol = request.Protocol;
        source.Priority = request.Priority; source.Enabled = request.Enabled;
        if (endpointChanged)
        {
            source.LastFetchedAt = null;
            source.LastSucceededAt = null;
            source.LastItemCount = 0;
            source.ConsecutiveFailures = 0;
            source.LastError = null;
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
        if (BuiltInSourceCatalog.Sources.Any(x => string.Equals(x.Url, source.Url, StringComparison.OrdinalIgnoreCase)))
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
        var databaseBytes = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"")
            .SingleAsync(token);
        var queue = await db.Proxies.AsNoTracking().GroupBy(_ => 1).Select(x => new
        {
            total = x.Count(),
            leased = x.Count(proxy => proxy.CheckLeaseUntil > now),
            neverChecked = x.Count(proxy => proxy.LastCheckedAt == null),
            due = x.Count(proxy => proxy.NextCheckAt == null || proxy.NextCheckAt <= now),
            scheduled = x.Count(proxy => proxy.NextCheckAt > now),
            repeatedlyFailing = x.Count(proxy => proxy.ConsecutiveFailedChecks >= 3)
        }).FirstOrDefaultAsync(token);
        var recentRuns = await db.Runs.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(10).ToListAsync(token);
        var recentBackups = await db.BackupRuns.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(10).ToListAsync(token);
        return Ok(new { serverTime = now, databaseBytes, validationQueue = queue, recentRuns, recentBackups });
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
    ProxyProtocol Protocol,
    [Range(-10_000, 10_000)] int Priority = 100,
    bool Enabled = true);
