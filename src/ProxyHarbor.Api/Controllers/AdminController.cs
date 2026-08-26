using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
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
[Authorize(Roles = UserRoles.Administrator)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class AdminController(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    ProxyCollector collector,
    ProxyValidator validator,
    BackupService backup,
    ISourceCatalogMutationCoordinator sourceMutationCoordinator,
    IOptions<BackupOptions> backupOptions,
    IOptions<CollectorOptions> collectorOptions,
    IBackupConfigurationStore? backupConfigurationStore = null) : ControllerBase
{
    /// <summary>Возвращает стабильную bounded-страницу источников и их runtime-состояние.</summary>
    [HttpGet("sources")]
    [ProducesResponseType<PagedResult<SourceResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<SourceResponse>>> Sources(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var query = db.Sources.AsNoTracking();

        // Фильтрация выполняется до Count/Skip/Take, поэтому поиск охватывает весь
        // каталог, а не только уже загруженную страницу. Провайдер хранится в
        // версионируемом built-in каталоге, поэтому его совпадения переводятся в URL.
        var normalizedSearch = search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            normalizedSearch = normalizedSearch[..Math.Min(normalizedSearch.Length, 200)];
            var providerUrls = BuiltInSourceCatalog.Sources
                .Where(source => source.Provider.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                    source.ProviderIdentity.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                .Select(source => source.Url)
                .ToArray();
            // Именно эти overload'ы переводятся EF Core в SQL lower/LIKE и одинаково
            // работают в тестовом InMemory provider; StringComparison-перегрузка SQL не переводится.
#pragma warning disable CA1304, CA1311, CA1862
            query = query.Where(source => source.Name.ToLower().Contains(normalizedSearch) ||
                source.Url.ToLower().Contains(normalizedSearch) || providerUrls.Contains(source.Url));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await query.CountAsync(token);
        var sources = await query.OrderBy(x => x.Priority).ThenBy(x => x.Name).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        return Ok(new PagedResult<SourceResponse>(
            sources.Select(SourceResponse.From).ToArray(), page, pageSize, total));
    }

    /// <summary>Возвращает один источник по стабильному идентификатору.</summary>
    [HttpGet("sources/{id:guid}")]
    [ProducesResponseType<SourceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SourceResponse>> GetSource(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        return source is null ? NotFound() : Ok(SourceResponse.From(source));
    }

    /// <summary>Добавляет проверенный публичный HTTPS feed под общей collection-lock.</summary>
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

    /// <summary>Изменяет пользовательский feed либо только флаг Enabled встроенного источника.</summary>
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

    /// <summary>Удаляет пользовательский feed либо устойчиво отключает встроенный.</summary>
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
        Title = "Каталог занят сбором источников или восстановлением БД; повторите изменение позже",
        Status = StatusCodes.Status409Conflict
    });

    /// <summary>Возвращает единый PostgreSQL snapshot очередей, каталога и operational audit.</summary>
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
        // VPN-узлы считаются в том же согласованном snapshot, что и прокси,
        // чтобы боковая панель не показывала число из отдельного, более старого запроса.
        var vpnEndpoints = await db.VpnEndpoints.AsNoTracking().CountAsync(token);
        var queue = await db.Proxies.AsNoTracking().GroupBy(_ => 1).Select(x => new
        {
            total = x.Count(),
            everAlive = x.Count(proxy => proxy.FirstAliveAt != null || proxy.SuccessfulChecks > 0),
            historicalDead = x.Count(proxy => proxy.Status == ProxyStatus.Dead &&
                (proxy.FirstAliveAt != null || proxy.SuccessfulChecks > 0)),
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
            queue.everAlive,
            queue.historicalDead,
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
            vpnEndpoints,
            validationQueue,
            sourceCatalog,
            recentRuns,
            recentValidationRuns,
            recentBackups));
    }

    /// <summary>Принудительно загружает и разбирает каждый включённый источник.</summary>
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

    /// <summary>Проверяет одну доступную распределённую партию прокси.</summary>
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

    /// <summary>Создаёт, self-verify шифрует и доставляет администратору один backup.</summary>
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
        var current = await GetBackupOptionsAsync(token);
        var sent = !string.IsNullOrWhiteSpace(current.TelegramBotToken) &&
            !string.IsNullOrWhiteSpace(current.TelegramChatId);
        return Ok(new BackupTriggerResponse(Path.GetFileName(path), sent));
    }

    /// <summary>Возвращает управляемое расписание без раскрытия bot token и PHB3-ключа.</summary>
    [HttpGet("backups/settings")]
    [ProducesResponseType<BackupSettingsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackupSettingsResponse>> BackupSettings(CancellationToken token)
    {
        var current = await GetBackupOptionsAsync(token);
        return Ok(BackupSettingsResponse.From(current));
    }

    /// <summary>Сохраняет расписание, retention и защищённую Telegram-доставку.</summary>
    [HttpPut("backups/settings")]
    [ProducesResponseType<BackupSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BackupSettingsResponse>> UpdateBackupSettings(
        [FromBody] BackupSettingsRequest request,
        CancellationToken token)
    {
        if (backupConfigurationStore is null)
            return Problem("Runtime-настройки резервного копирования недоступны.", statusCode: 503);
        if (request.IntervalHours is < 1 or > 8_760 ||
            request.RetentionDays is < 1 or > 3_650 ||
            request.HistoryRetentionDays is < 1 or > 3_650 ||
            request.MaxTelegramFileSizeMb is < 1 or > 49)
            return Problem("Проверьте границы интервала, сроков хранения и размера Telegram-файла.", statusCode: 400);

        var current = await backupConfigurationStore.GetAsync(token);
        var tokenValue = request.ClearTelegramCredentials
            ? null
            : string.IsNullOrWhiteSpace(request.TelegramBotToken)
                ? current.TelegramBotToken
                : request.TelegramBotToken.Trim();
        var chatId = request.ClearTelegramCredentials
            ? null
            : string.IsNullOrWhiteSpace(request.TelegramChatId)
                ? current.TelegramChatId
                : request.TelegramChatId.Trim();
        if (!request.SendToTelegram)
        {
            tokenValue = null;
            chatId = null;
        }
        if (request.SendToTelegram &&
            (!BackupOptions.IsTelegramBotTokenValid(tokenValue) || !BackupOptions.IsTelegramChatIdValid(chatId)))
            return Problem("Для Telegram-доставки задайте корректные BotFather token и числовой chat ID.", statusCode: 400);
        if (request.Enabled && !request.SendToTelegram)
            return Problem("Для плановых резервных копий доставка администратору в Telegram обязательна.", statusCode: 400);

        var updated = new BackupOptions
        {
            Enabled = request.Enabled,
            IntervalHours = request.IntervalHours,
            RetentionDays = request.RetentionDays,
            HistoryRetentionDays = request.HistoryRetentionDays,
            MaxTelegramFileSizeMb = request.MaxTelegramFileSizeMb,
            Directory = current.Directory,
            EncryptionKey = current.EncryptionKey,
            TelegramBotToken = tokenValue,
            TelegramChatId = chatId
        };
        await backupConfigurationStore.SaveAsync(updated, token);
        return Ok(BackupSettingsResponse.From(updated));
    }

    /// <summary>Возвращает страницу истории и актуальную доступность локальных encrypted-файлов.</summary>
    [HttpGet("backups")]
    [ProducesResponseType<PagedResult<BackupFileResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<BackupFileResponse>>> Backups(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var current = await GetBackupOptionsAsync(token);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var total = await db.BackupRuns.CountAsync(token);
        var runs = await db.BackupRuns.AsNoTracking()
            .OrderByDescending(run => run.StartedAt).ThenByDescending(run => run.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var items = runs.Select(run => BackupFileResponse.From(
            run, TryGetBackupPath(run, current.Directory, out var path) && System.IO.File.Exists(path))).ToArray();
        return Ok(new PagedResult<BackupFileResponse>(items, page, pageSize, total));
    }

    /// <summary>Скачивает зашифрованный PHB3-файл; расшифровка на сервере не выполняется.</summary>
    [HttpGet("backups/{id:guid}/download")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadBackup(Guid id, CancellationToken token)
    {
        var current = await GetBackupOptionsAsync(token);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var run = await db.BackupRuns.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        if (run is null || !TryGetBackupPath(run, current.Directory, out var path) || !System.IO.File.Exists(path)) return NotFound();

        // Асинхронный поток не загружает многомегабайтный архив в память API и поддерживает range-запросы.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return File(stream, "application/octet-stream", run.FileName!, enableRangeProcessing: true);
    }

    /// <summary>Удаляет выбранный локальный файл и соответствующую строку истории.</summary>
    [HttpDelete("backups/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteBackup(Guid id, CancellationToken token)
    {
        var current = await GetBackupOptionsAsync(token);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var run = await db.BackupRuns.SingleOrDefaultAsync(item => item.Id == id, token);
        if (run is null) return NotFound();
        if (string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase))
            return Conflict(new ProblemDetails { Title = "Нельзя удалить выполняющуюся резервную копию", Status = 409 });
        if (!string.IsNullOrWhiteSpace(run.FileName) && !TryGetBackupPath(run, current.Directory, out _))
            return Conflict(new ProblemDetails { Title = "Имя файла резервной копии не прошло проверку безопасности", Status = 409 });

        // Удаляется именно опубликованный архив в смонтированном server volume,
        // после чего — его audit row. Для уже очищенного retention файла удаляется история.
        if (TryGetBackupPath(run, current.Directory, out var path) && System.IO.File.Exists(path)) System.IO.File.Delete(path);
        db.BackupRuns.Remove(run);
        await db.SaveChangesAsync(token);
        return NoContent();
    }

    private static bool TryGetBackupPath(BackupRun run, string directory, out string path)
    {
        path = string.Empty;
        return !string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase) &&
            BackupService.TryResolvePublishedBackupPath(directory, run.FileName, out path);
    }

    private Task<BackupOptions> GetBackupOptionsAsync(CancellationToken token) =>
        backupConfigurationStore is null
            ? Task.FromResult(backupOptions.Value)
            : backupConfigurationStore.GetAsync(token);
}

/// <summary>Результат одного ручного validation batch.</summary>
public sealed record ValidationTriggerResponse(int Checked, int Alive, int Deferred);

/// <summary>Результат ручного создания и опциональной Telegram-доставки backup.</summary>
public sealed record BackupTriggerResponse(string Created, bool SentToTelegram);

/// <summary>Редактируемые поля backup; пустой token сохраняет уже защищённое значение.</summary>
public sealed record BackupSettingsRequest(
    bool Enabled,
    int IntervalHours,
    int RetentionDays,
    int HistoryRetentionDays,
    int MaxTelegramFileSizeMb,
    bool SendToTelegram,
    string? TelegramBotToken,
    string? TelegramChatId,
    bool ClearTelegramCredentials = false);

/// <summary>Безопасная проекция runtime-настроек для панели администратора.</summary>
public sealed record BackupSettingsResponse(
    bool Enabled,
    int IntervalHours,
    int RetentionDays,
    int HistoryRetentionDays,
    int MaxTelegramFileSizeMb,
    bool SendToTelegram,
    bool TelegramBotTokenConfigured,
    string? TelegramChatId,
    bool EncryptionConfigured,
    string Format)
{
    /// <summary>Секреты заменяются только признаками наличия.</summary>
    public static BackupSettingsResponse From(BackupOptions options) => new(
        options.Enabled,
        options.IntervalHours,
        options.RetentionDays,
        options.HistoryRetentionDays,
        options.MaxTelegramFileSizeMb,
        !string.IsNullOrWhiteSpace(options.TelegramBotToken) && !string.IsNullOrWhiteSpace(options.TelegramChatId),
        !string.IsNullOrWhiteSpace(options.TelegramBotToken),
        options.TelegramChatId,
        BackupOptions.IsNewEncryptionKeyValid(options.EncryptionKey),
        "PHB3 (.phbackup)");
}

/// <summary>Запись истории backup с вычисленной доступностью файла в локальном volume.</summary>
public sealed record BackupFileResponse(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    string? FileName,
    long SizeBytes,
    bool TelegramConfigured,
    bool SentToTelegram,
    string? Error,
    bool Available)
{
    /// <summary>Проецирует audit-запись без раскрытия server-side пути к файлу.</summary>
    public static BackupFileResponse From(BackupRun run, bool available) => new(
        run.Id, run.StartedAt, run.FinishedAt, run.Status, run.FileName, run.SizeBytes,
        run.TelegramConfigured, run.SentToTelegram, run.Error, available);
}

/// <summary>Текущий backlog без уже арендованных строк и rolling validation telemetry.</summary>
public sealed record ValidationQueueResponse(
    int Total,
    int EverAlive,
    int HistoricalDead,
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
    int VpnEndpoints,
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
    /// <inheritdoc />
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
