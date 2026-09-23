using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
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
    IBackupConfigurationStore? backupConfigurationStore = null,
    ITelegramBackupDeliveryResolver? telegramBackupDeliveryResolver = null,
    ProxyMetricsSnapshotCache? proxySnapshotCache = null,
    VpnMetricsSnapshotCache? vpnSnapshotCache = null,
    IDataProtectionProvider? credentialProtectionProvider = null,
    IOptions<BackupRoutingOptions>? backupRoutingOptions = null,
    BackupProtectionEvaluator? backupProtectionEvaluator = null) : ControllerBase
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
        IQueryable<ProxySource> query = db.Sources.AsNoTracking().Include(source => source.Credential);

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
        var source = await db.Sources.AsNoTracking().Include(item => item.Credential)
            .SingleOrDefaultAsync(item => item.Id == id, token);
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
        var paid = PaidProxySourceCatalog.IsPaid(source);
        if (paid &&
            (!string.Equals(normalizedUrl, PaidProxySourceCatalog.BestProxiesUrl, StringComparison.Ordinal) ||
                request.Protocol != ProxyProtocol.Http ||
                !string.Equals(request.Name.Trim(), PaidProxySourceCatalog.BestProxiesName, StringComparison.Ordinal) ||
                request.Priority != PaidProxySourceCatalog.BestProxiesPriority))
            return Conflict(new ProblemDetails
            {
                Title = "Метаданные платного источника неизменяемы; можно менять ключ и активность",
                Status = 409
            });
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
        if (builtIn is null && !paid && !await NetworkSafety.IsSafePublicHttpsUrlAsync(normalizedUrl, token))
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
        if (BuiltInSourceCatalog.FindByUrl(source.Url) is not null || PaidProxySourceCatalog.IsPaid(source))
            source.Enabled = false;
        else
            db.Sources.Remove(source);
        await db.SaveChangesAsync(token);
        return NoContent();
    }

    /// <summary>Заменяет зашифрованный API key платного источника и сбрасывает backoff.</summary>
    [HttpPut("sources/{id:guid}/credential")]
    [ProducesResponseType<SourceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SourceResponse>> UpdateSourceCredential(
        Guid id,
        [FromBody] ProxySourceCredentialRequest request,
        CancellationToken token)
    {
        if (!ProxySourceApiKeyPolicy.IsValid(request.ApiKey))
            return Problem(
                "API key должен содержать 16–256 printable ASCII-символов без URL-разделителей.",
                statusCode: 400);
        if (credentialProtectionProvider is null)
            return Problem("Шифрование credentials недоступно.", statusCode: 503);
        await using var mutationLease = await sourceMutationCoordinator.TryAcquireAsync(token);
        if (mutationLease is null) return SourceMutationConflict();
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var source = await db.Sources.Include(item => item.Credential)
            .SingleOrDefaultAsync(item => item.Id == id, token);
        if (source is null) return NotFound();
        if (!PaidProxySourceCatalog.IsPaid(source))
            return Problem("Credentials поддерживаются только для управляемого платного источника.", statusCode: 400);

        var protector = ProxySourceCredentialProtection.Create(credentialProtectionProvider);
        var credential = source.Credential ?? new ProxySourceCredential
        {
            ProxySourceId = source.Id,
            ProtectedApiKey = string.Empty
        };
        if (source.Credential is null) db.ProxySourceCredentials.Add(credential);
        credential.ProtectedApiKey = protector.Protect(request.ApiKey);
        credential.Status = "not_configured";
        credential.ExpiresAt = null;
        credential.CheckedAt = null;
        credential.UpdatedAt = DateTimeOffset.UtcNow;
        credential.LastError = null;
        source.Enabled = request.Enabled;
        ResetSourceFetchState(source);
        await db.SaveChangesAsync(token);
        source.Credential = credential;
        return Ok(SourceResponse.From(source));
    }

    private static void ResetSourceFetchState(ProxySource source)
    {
        source.LastFetchedAt = null;
        source.LastSucceededAt = null;
        source.LastContentFetchedAt = null;
        source.NextFetchAt = null;
        source.HttpETag = null;
        source.HttpLastModifiedAt = null;
        source.LastItemCount = 0;
        source.LastResultTruncated = false;
        source.ConsecutiveFailures = 0;
        source.LastError = null;
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
        // Дорогие process-wide aggregates обновляются до короткого database snapshot:
        // холодный scan не удерживает лишнюю транзакцию diagnostics и её MVCC horizon.
        var proxySnapshot = proxySnapshotCache is null
            ? null
            : await proxySnapshotCache.GetAsync(requestToken);
        var vpnSnapshot = vpnSnapshotCache is null
            ? null
            : await vpnSnapshotCache.GetAsync(requestToken);
        await using var db = await dbFactory.CreateDbContextAsync(requestToken);
        return await BufferedReadSnapshot.ExecuteAsync(
            db, token => GetDiagnosticsSnapshotAsync(db, proxySnapshot, vpnSnapshot, token), requestToken);
    }

    /// <summary>Строит весь database-derived операторский ответ внутри одного read snapshot.</summary>
    private async Task<ActionResult<DiagnosticsResponse>> GetDiagnosticsSnapshotAsync(
        ProxyHarborDbContext db,
        ProxyMetricsSnapshot? proxySnapshot,
        VpnMetricsSnapshot? vpnSnapshot,
        CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var validationWindowStart = now.AddMinutes(-5);
        var unseenRetentionCutoff = now.AddDays(-Math.Max(1, collectorOptions.Value.DeadRetentionDays));
        var builtInUrls = BuiltInSourceCatalog.Sources.Select(source => source.Url).ToArray();
        var databaseSnapshot = await DiagnosticsDatabaseSnapshotReader.ReadAsync(
            db, builtInUrls, validationWindowStart, token);
        // Production использует общий минутный snapshot административной VPN-страницы.
        // Null сохраняет provider-neutral и транзакционную семантику изолированных тестов.
        var vpnEndpoints = vpnSnapshot is null
            ? await db.VpnEndpoints.AsNoTracking().CountAsync(token)
            : SaturatingInt(vpnSnapshot.Total);
        // Production использует тот же минутный aggregate, что /metrics и /stats.
        // Поэтому refresh любой открытой admin-страницы не запускает отдельный полный
        // scan Proxies. Null остаётся provider-neutral тестам для snapshot-проверки.
        var queue = proxySnapshot is null
            ? await ReadValidationQueueAsync(db, now, unseenRetentionCutoff, token)
            : ValidationQueueAggregate.From(proxySnapshot);
        var validationTelemetry = ValidationTelemetry.Calculate(
            databaseSnapshot.ValidationRuns, validationWindowStart, queue?.Due ?? 0);
        var validationQueue = queue is null ? null : new ValidationQueueResponse(
            queue.Total,
            queue.EverAlive,
            queue.HistoricalDead,
            queue.Leased,
            queue.NeverChecked,
            queue.NeverAttempted,
            queue.Due,
            queue.Scheduled,
            queue.RepeatedlyFailing,
            queue.StaleUnseen,
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
            queue.LastAttemptAt);
        var sourceCatalog = SourceCatalogHealth.Calculate(
            databaseSnapshot.Sources,
            now,
            SourceCatalogHealth.FreshnessWindow(collectorOptions.Value.CollectionIntervalMinutes));
        return Ok(new DiagnosticsResponse(
            now,
            databaseSnapshot.DatabaseBytes,
            vpnEndpoints,
            validationQueue,
            sourceCatalog,
            databaseSnapshot.RecentRuns,
            databaseSnapshot.RecentValidationRuns,
            databaseSnapshot.RecentBackups));
    }

    private static async Task<ValidationQueueAggregate?> ReadValidationQueueAsync(
        ProxyHarborDbContext db,
        DateTimeOffset now,
        DateTimeOffset unseenRetentionCutoff,
        CancellationToken token)
    {
        var query =
            from proxy in db.Proxies.AsNoTracking()
            join lease in db.ProxyValidationLeases.AsNoTracking()
                on proxy.Id equals lease.ProxyId into proxyLeases
            from lease in proxyLeases.DefaultIfEmpty()
            select new { Proxy = proxy, Lease = lease };
        return await query.GroupBy(_ => 1).Select(group => new ValidationQueueAggregate(
            group.Count(),
            group.Count(item => item.Proxy.FirstAliveAt != null || item.Proxy.SuccessfulChecks > 0),
            group.Count(item => item.Proxy.Status == ProxyStatus.Dead &&
                (item.Proxy.FirstAliveAt != null || item.Proxy.SuccessfulChecks > 0)),
            group.Count(item => item.Lease != null && item.Lease.LeaseUntil >= now),
            group.Count(item => item.Proxy.LastCheckedAt == null),
            group.Count(item => item.Proxy.LastValidationAttemptAt == null),
            group.Count(item => (item.Proxy.NextCheckAt == null || item.Proxy.NextCheckAt <= now) &&
                (item.Lease == null || item.Lease.LeaseUntil < now)),
            group.Count(item => item.Proxy.NextCheckAt > now),
            group.Count(item => item.Proxy.ConsecutiveFailedChecks >= 3),
            group.Count(item =>
                (item.Proxy.Status == ProxyStatus.Pending || item.Proxy.Status == ProxyStatus.Dead) &&
                item.Proxy.LastSeenAt < unseenRetentionCutoff &&
                (item.Lease == null || item.Lease.LeaseUntil < now)),
            group.Max(item => item.Proxy.LastValidationAttemptAt)))
        .SingleOrDefaultAsync(token);
    }

    private sealed record ValidationQueueAggregate(
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
        DateTimeOffset? LastAttemptAt)
    {
        internal static ValidationQueueAggregate From(ProxyMetricsSnapshot snapshot) => new(
            ToInt(snapshot.Groups.Sum(row => row.Count)),
            ToInt(snapshot.Groups.Sum(row => row.EverAlive)),
            ToInt(snapshot.Groups.Sum(row => row.HistoricalDead)),
            ToInt(snapshot.Leased),
            ToInt(snapshot.Groups.Sum(row => row.NeverChecked)),
            ToInt(snapshot.NeverAttempted),
            ToInt(snapshot.Due),
            ToInt(snapshot.Groups.Sum(row => row.Scheduled)),
            ToInt(snapshot.Groups.Sum(row => row.RepeatedlyFailing)),
            ToInt(snapshot.StaleUnseen),
            snapshot.LastAttemptAt);

        private static int ToInt(long value) => (int)Math.Min(int.MaxValue, Math.Max(0, value));
    }

    private static int SaturatingInt(long value) => (int)Math.Min(int.MaxValue, Math.Max(0, value));

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
        if (result.Checked + result.Deferred > 0) proxySnapshotCache?.RequestRefresh();
        return Ok(new ValidationTriggerResponse(result.Checked, result.Alive, result.Deferred));
    }

    /// <summary>Создаёт, self-verify шифрует и доставляет администратору один backup.</summary>
    [HttpPost("backup")]
    [ProducesResponseType<BackupTriggerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<BackupTriggerResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<BackupTriggerResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Backup(CancellationToken token)
    {
        string path;
        try { path = await backup.CreateAndSendAsync(token); }
        catch (OperationAlreadyRunningException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message, Status = 409 });
        }
        catch (BackupStagingCapacityException exception)
        {
            await using var failureDb = await dbFactory.CreateDbContextAsync(token);
            var failed = await failureDb.BackupRuns.AsNoTracking()
                .Where(item => item.FileName == exception.FileName)
                .OrderByDescending(item => item.StartedAt)
                .Select(item => new
                {
                    item.Id,
                    item.RequiredVerifiedCopies,
                    item.DesiredVerifiedCopies
                })
                .FirstOrDefaultAsync(token);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new BackupTriggerResponse(
                    exception.FileName,
                    SentToTelegram: false,
                    BackupRunId: failed?.Id,
                    ProtectionState: "unavailable",
                    Degraded: false,
                    VerifiedIndependentCopies: 0,
                    RequiredVerifiedCopies: failed?.RequiredVerifiedCopies,
                    DesiredVerifiedCopies: failed?.DesiredVerifiedCopies,
                    RequiredCopyDebt: failed?.RequiredVerifiedCopies,
                    DesiredCopyDebt: failed?.DesiredVerifiedCopies));
        }
        var current = await GetBackupOptionsAsync(token);
        var sent = current.TelegramRecipientId.HasValue ||
            !string.IsNullOrWhiteSpace(current.TelegramBotToken) &&
            !string.IsNullOrWhiteSpace(current.TelegramChatId);
        var fileName = Path.GetFileName(path);
        if (backupRoutingOptions?.Value.Enabled != true)
            return Ok(new BackupTriggerResponse(fileName, sent));
        if (backupProtectionEvaluator is null)
            throw new InvalidOperationException("Backup protection evaluator не зарегистрирован.");

        await using var db = await dbFactory.CreateDbContextAsync(token);
        var run = await db.BackupRuns.AsNoTracking()
            .Where(item => item.FileName == fileName)
            .OrderByDescending(item => item.StartedAt)
            .Select(item => new { item.Id, item.SentToTelegram })
            .FirstOrDefaultAsync(token)
            ?? throw new InvalidOperationException("Завершённый backup run не найден.");
        var evaluation = await backupProtectionEvaluator.EvaluateAsync(
            run.Id,
            hasDurableLocalStaging: System.IO.File.Exists(path),
            token);
        var response = BackupTriggerResponse.From(fileName, run.SentToTelegram, evaluation);
        var statusCode = BackupTriggerResponse.StatusCodeFor(evaluation.State);
        return statusCode == StatusCodes.Status200OK
            ? Ok(response)
            : StatusCode(statusCode, response);
    }

    /// <summary>Возвращает управляемое расписание без раскрытия bot token и PHB3-ключа.</summary>
    [HttpGet("backups/settings")]
    [ProducesResponseType<BackupSettingsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackupSettingsResponse>> BackupSettings(CancellationToken token)
    {
        var current = await GetBackupOptionsAsync(token);
        return Ok(await CreateBackupSettingsResponseAsync(current, token));
    }

    /// <summary>Возвращает активные личные диалоги основного бота для выбора получателя backup.</summary>
    [HttpGet("backups/telegram-recipients")]
    [ProducesResponseType<TelegramBackupRecipientResponse[]>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TelegramBackupRecipientResponse[]>> BackupTelegramRecipients(
        [FromQuery] string? query = null,
        CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var chats = db.TelegramChats.AsNoTracking().Where(chat => !chat.IsBlocked);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim()[..Math.Min(query.Trim().Length, 120)];
            chats = chats.Where(chat => chat.DisplayName.Contains(term) ||
                chat.Username != null && chat.Username.Contains(term));
        }
        // Эти ToLower overload'ы переводятся EF Core в SQL lower() и сохраняют
        // одинаковое поведение InMemory-тестов; StringComparison SQL не переводит.
#pragma warning disable CA1304, CA1311, CA1862
        var items = await chats
            .OrderByDescending(chat => chat.Username != null && chat.Username.ToLower() == "xsenus")
            .ThenByDescending(chat => chat.LastInteractionAt)
            .ThenBy(chat => chat.Id)
            .Take(100)
            .Select(chat => new TelegramBackupRecipientResponse(
                chat.Id, chat.DisplayName, chat.Username, chat.LastInteractionAt,
                chat.Username != null && chat.Username.ToLower() == "xsenus"))
            .ToArrayAsync(token);
#pragma warning restore CA1304, CA1311, CA1862
        return Ok(items);
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
        if (request.SendToTelegram && !request.TelegramRecipientId.HasValue)
            return Problem("Выберите получателя среди диалогов основного Telegram-бота.", statusCode: 400);
        if (request.SendToTelegram)
        {
            if (telegramBackupDeliveryResolver is null)
                return Problem("Основной Telegram-бот недоступен.", statusCode: 503);
            try
            {
                _ = await telegramBackupDeliveryResolver.ResolveAsync(
                    request.TelegramRecipientId!.Value, token);
            }
            catch (InvalidOperationException exception)
            {
                return Problem(exception.Message, statusCode: 400);
            }
        }
        var objectStorageAccessKey = request.ClearObjectStorageCredentials ? null :
            string.IsNullOrWhiteSpace(request.ObjectStorageAccessKey) ? current.ObjectStorageAccessKey : request.ObjectStorageAccessKey.Trim();
        var objectStorageSecretKey = request.ClearObjectStorageCredentials ? null :
            string.IsNullOrWhiteSpace(request.ObjectStorageSecretKey) ? current.ObjectStorageSecretKey : request.ObjectStorageSecretKey;
        var objectStorageCandidate = new BackupOptions
        {
            SendToObjectStorage = request.SendToObjectStorage,
            ObjectStorageEndpoint = request.ObjectStorageEndpoint?.Trim(),
            ObjectStorageRegion = request.ObjectStorageRegion?.Trim() ?? string.Empty,
            ObjectStorageBucket = request.ObjectStorageBucket?.Trim(),
            ObjectStoragePrefix = request.ObjectStoragePrefix?.Trim('/') ?? string.Empty,
            ObjectStorageUsePathStyle = request.ObjectStorageUsePathStyle,
            ObjectStorageAccessKey = objectStorageAccessKey,
            ObjectStorageSecretKey = objectStorageSecretKey
        };
        if (request.SendToObjectStorage && !BackupOptions.IsObjectStorageConfigurationValid(objectStorageCandidate))
            return Problem("Проверьте HTTPS endpoint, region, bucket, безопасный префикс и ключи S3-совместимого хранилища.", statusCode: 400);
        if (request.Enabled && !request.SendToTelegram && !request.SendToObjectStorage)
            return Problem("Для плановых резервных копий включите хотя бы Telegram или S3-совместимое хранилище.", statusCode: 400);

        var updated = new BackupOptions
        {
            Enabled = request.Enabled,
            IntervalHours = request.IntervalHours,
            RetentionDays = request.RetentionDays,
            HistoryRetentionDays = request.HistoryRetentionDays,
            MaxTelegramFileSizeMb = request.MaxTelegramFileSizeMb,
            Directory = current.Directory,
            EncryptionKey = current.EncryptionKey,
            // Token и числовой chat_id больше не копируются в backup-настройки.
            // BackupService разрешает их из основного бота перед каждой отправкой.
            TelegramBotToken = null,
            TelegramChatId = null,
            TelegramRecipientId = request.SendToTelegram ? request.TelegramRecipientId : null,
            SendToObjectStorage = request.SendToObjectStorage,
            ObjectStorageEndpoint = objectStorageCandidate.ObjectStorageEndpoint,
            ObjectStorageRegion = objectStorageCandidate.ObjectStorageRegion,
            ObjectStorageBucket = objectStorageCandidate.ObjectStorageBucket,
            ObjectStoragePrefix = objectStorageCandidate.ObjectStoragePrefix,
            ObjectStorageUsePathStyle = objectStorageCandidate.ObjectStorageUsePathStyle,
            ObjectStorageAccessKey = objectStorageAccessKey,
            ObjectStorageSecretKey = objectStorageSecretKey
        };
        await backupConfigurationStore.SaveAsync(updated, token);
        return Ok(await CreateBackupSettingsResponseAsync(updated, token));
    }

    /// <summary>Возвращает безопасный обзор настроенных destination и маршрутов без provider I/O.</summary>
    [HttpGet("backups/destinations")]
    [ProducesResponseType<PagedResult<BackupDestinationOverviewResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<BackupDestinationOverviewResponse>>> BackupDestinations(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken token = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var total = await db.BackupDestinations.CountAsync(token);
        var destinations = await db.BackupDestinations.AsNoTracking()
            .OrderBy(destination => destination.Priority).ThenBy(destination => destination.Name)
            .ThenBy(destination => destination.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(destination => new
            {
                destination.Id,
                destination.Name,
                destination.Kind,
                destination.Enabled,
                destination.Priority,
                CredentialsConfigured = destination.ProtectedSecrets != "",
                FailureDomainConfigured = destination.FailureDomain != ""
            }).ToArrayAsync(token);
        var ids = destinations.Select(destination => destination.Id).ToArray();
        var routes = await db.BackupPoolDestinations.AsNoTracking()
            .Where(route => ids.Contains(route.BackupDestinationId))
            .Include(route => route.BackupPool)
            .ToArrayAsync(token);
        var lastOutcomes = await db.BackupDestinationHealthOutcomes.AsNoTracking()
            .Where(outcome => ids.Contains(outcome.BackupDestinationId))
            .GroupBy(outcome => outcome.BackupDestinationId)
            .Select(group => group.OrderByDescending(outcome => outcome.ObservedAt)
                .ThenByDescending(outcome => outcome.Id).First())
            .ToDictionaryAsync(outcome => outcome.BackupDestinationId, token);
        var routesByDestination = routes.GroupBy(route => route.BackupDestinationId)
            .ToDictionary(group => group.Key, group => group.OrderBy(route => route.Priority)
                .ThenBy(route => route.BackupPool.Name, StringComparer.Ordinal)
                .Select(BackupDestinationRouteResponse.From).ToArray());
        var items = destinations.Select(destination => new BackupDestinationOverviewResponse(
            destination.Id, destination.Name, destination.Kind, destination.Enabled,
            destination.Priority, destination.CredentialsConfigured,
            destination.FailureDomainConfigured,
            routesByDestination.GetValueOrDefault(destination.Id) ?? [],
            lastOutcomes.TryGetValue(destination.Id, out var outcome)
                ? BackupDestinationOutcomeResponse.From(outcome) : null)).ToArray();
        return Ok(new PagedResult<BackupDestinationOverviewResponse>(items, page, pageSize, total));
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

    /// <summary>Показывает защиту и состояния копий одного backup без provider I/O и locator.</summary>
    [HttpGet("backups/{id:guid}/protection")]
    [ProducesResponseType<BackupProtectionDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BackupProtectionDetailResponse>> BackupProtectionDetail(
        Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var run = await db.BackupRuns.AsNoTracking()
            .Include(item => item.Copies).ThenInclude(copy => copy.BackupDestination)
            .Include(item => item.Copies).ThenInclude(copy => copy.Jobs)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == id, token);
        if (run is null) return NotFound();

        var routes = run.BackupPoolId is { } poolId
            ? await db.BackupPoolDestinations.AsNoTracking()
                .Where(route => route.BackupPoolId == poolId)
                .ToDictionaryAsync(route => route.BackupDestinationId, token)
            : new Dictionary<Guid, BackupPoolDestination>();
        var copies = run.Copies
            .OrderBy(copy => routes.TryGetValue(copy.BackupDestinationId, out var route)
                ? route.Priority : int.MaxValue)
            .ThenBy(copy => copy.BackupDestination.Name, StringComparer.Ordinal)
            .Select(copy => BackupCopyStatusResponse.From(
                copy, routes.GetValueOrDefault(copy.BackupDestinationId)))
            .ToArray();

        if (run.BackupPoolId is null || run.ProtectionPolicyVersion is null ||
            run.RequiredVerifiedCopies is null || run.DesiredVerifiedCopies is null ||
            string.IsNullOrWhiteSpace(run.ContentSha256))
            return Ok(new BackupProtectionDetailResponse(run.Id, "legacy_unassessed", null,
                null, null, null, null, null, copies));

        if (backupProtectionEvaluator is null)
            throw new InvalidOperationException("Backup protection evaluator не зарегистрирован.");
        var current = await GetBackupOptionsAsync(token);
        var hasStaging = TryGetBackupPath(run, current.Directory, out var path) &&
            System.IO.File.Exists(path);
        try
        {
            var evaluation = backupProtectionEvaluator.EvaluateLoaded(run, routes, hasStaging);
            return Ok(new BackupProtectionDetailResponse(
                run.Id, "evaluated", evaluation.State.ToString().ToLowerInvariant(),
                evaluation.VerifiedIndependentCopies, evaluation.RequiredVerifiedCopies,
                evaluation.DesiredVerifiedCopies, evaluation.RequiredCopyDebt,
                evaluation.DesiredCopyDebt,
                copies));
        }
        catch (BackupDestinationRouteException)
        {
            return Ok(new BackupProtectionDetailResponse(run.Id, "unknown_adapter", null,
                null, null, null, null, null, copies));
        }
        catch (ArgumentException)
        {
            return Ok(new BackupProtectionDetailResponse(run.Id, "invalid_policy", null,
                null, null, null, null, null, copies));
        }
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

    private async Task<BackupSettingsResponse> CreateBackupSettingsResponseAsync(
        BackupOptions options,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        // См. BackupTelegramRecipients: ToLower нужен для SQL/InMemory parity.
#pragma warning disable CA1304, CA1311, CA1862
        var recipient = options.TelegramRecipientId.HasValue
            ? await db.TelegramChats.AsNoTracking()
                .SingleOrDefaultAsync(chat => chat.Id == options.TelegramRecipientId.Value, token)
            : await db.TelegramChats.AsNoTracking()
                .Where(chat => !chat.IsBlocked && chat.Username != null && chat.Username.ToLower() == "xsenus")
                .OrderByDescending(chat => chat.LastInteractionAt)
                .FirstOrDefaultAsync(token);
#pragma warning restore CA1304, CA1311, CA1862
        var botConfigured = false;
        if (recipient is not null && telegramBackupDeliveryResolver is not null)
        {
            try
            {
                _ = await telegramBackupDeliveryResolver.ResolveAsync(recipient.Id, token);
                botConfigured = true;
            }
            catch (InvalidOperationException) { }
        }
        return BackupSettingsResponse.From(options, recipient, botConfigured);
    }
}

/// <summary>Результат одного ручного validation batch.</summary>
public sealed record ValidationTriggerResponse(int Checked, int Alive, int Deferred);

/// <summary>Результат ручного создания и доказанная защита backup.</summary>
public sealed record BackupTriggerResponse(
    string Created,
    bool SentToTelegram,
    Guid? BackupRunId = null,
    string ProtectionState = "legacy",
    bool Degraded = false,
    int? VerifiedIndependentCopies = null,
    int? RequiredVerifiedCopies = null,
    int? DesiredVerifiedCopies = null,
    int? RequiredCopyDebt = null,
    int? DesiredCopyDebt = null)
{
    /// <summary>Создаёт API acknowledgement из fail-closed результата evaluator.</summary>
    public static BackupTriggerResponse From(
        string created,
        bool sentToTelegram,
        BackupProtectionEvaluation evaluation) => new(
            created,
            sentToTelegram,
            evaluation.BackupRunId,
            evaluation.State.ToString().ToLowerInvariant(),
            evaluation.Degraded,
            evaluation.VerifiedIndependentCopies,
            evaluation.RequiredVerifiedCopies,
            evaluation.DesiredVerifiedCopies,
            evaluation.RequiredCopyDebt,
            evaluation.DesiredCopyDebt);

    /// <summary>HTTP acknowledgement: success только после обязательной защиты.</summary>
    public static int StatusCodeFor(BackupProtectionState state) => state switch
    {
        BackupProtectionState.Protected or BackupProtectionState.Degraded => StatusCodes.Status200OK,
        BackupProtectionState.Pending => StatusCodes.Status202Accepted,
        BackupProtectionState.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}

/// <summary>Редактируемые поля backup; пустой token сохраняет уже защищённое значение.</summary>
public sealed record BackupSettingsRequest(
    bool Enabled,
    int IntervalHours,
    int RetentionDays,
    int HistoryRetentionDays,
    int MaxTelegramFileSizeMb,
    bool SendToTelegram,
    Guid? TelegramRecipientId,
    bool SendToObjectStorage = false,
    string? ObjectStorageEndpoint = null,
    string? ObjectStorageRegion = null,
    string? ObjectStorageBucket = null,
    string? ObjectStoragePrefix = null,
    bool ObjectStorageUsePathStyle = true,
    string? ObjectStorageAccessKey = null,
    string? ObjectStorageSecretKey = null,
    bool ClearObjectStorageCredentials = false);

/// <summary>Безопасная проекция runtime-настроек для панели администратора.</summary>
public sealed record BackupSettingsResponse(
    bool Enabled,
    int IntervalHours,
    int RetentionDays,
    int HistoryRetentionDays,
    int MaxTelegramFileSizeMb,
    bool SendToTelegram,
    bool TelegramBotConfigured,
    Guid? TelegramRecipientId,
    string? TelegramRecipientDisplayName,
    string? TelegramRecipientUsername,
    bool SendToObjectStorage,
    string? ObjectStorageEndpoint,
    string ObjectStorageRegion,
    string? ObjectStorageBucket,
    string ObjectStoragePrefix,
    bool ObjectStorageUsePathStyle,
    bool ObjectStorageCredentialsConfigured,
    bool EncryptionConfigured,
    string Format)
{
    /// <summary>Возвращает только безопасную ссылку на CRM-диалог, без token и chat_id.</summary>
    public static BackupSettingsResponse From(
        BackupOptions options,
        TelegramChat? recipient,
        bool telegramBotConfigured) => new(
        options.Enabled,
        options.IntervalHours,
        options.RetentionDays,
        options.HistoryRetentionDays,
        options.MaxTelegramFileSizeMb,
        options.TelegramRecipientId.HasValue ||
            !string.IsNullOrWhiteSpace(options.TelegramBotToken) && !string.IsNullOrWhiteSpace(options.TelegramChatId),
        telegramBotConfigured,
        recipient?.Id,
        recipient?.DisplayName,
        recipient?.Username,
        options.SendToObjectStorage,
        options.ObjectStorageEndpoint,
        options.ObjectStorageRegion,
        options.ObjectStorageBucket,
        options.ObjectStoragePrefix,
        options.ObjectStorageUsePathStyle,
        !string.IsNullOrWhiteSpace(options.ObjectStorageAccessKey) &&
            !string.IsNullOrWhiteSpace(options.ObjectStorageSecretKey),
        BackupOptions.IsNewEncryptionKeyValid(options.EncryptionKey),
        "PHB3 (.phbackup)");
}

/// <summary>Безопасный вариант получателя из CRM основного Telegram-бота.</summary>
public sealed record TelegramBackupRecipientResponse(
    Guid Id,
    string DisplayName,
    string? Username,
    DateTimeOffset LastInteractionAt,
    bool IsDefault);

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
    bool ObjectStorageConfigured,
    bool SentToObjectStorage,
    string? ObjectStorageKey,
    string? Error,
    bool Available)
{
    /// <summary>Проецирует audit-запись без раскрытия server-side пути к файлу.</summary>
    public static BackupFileResponse From(BackupRun run, bool available) => new(
        run.Id, run.StartedAt, run.FinishedAt, run.Status, run.FileName, run.SizeBytes,
        run.TelegramConfigured, run.SentToTelegram, run.ObjectStorageConfigured,
        run.SentToObjectStorage, run.ObjectStorageKey, run.Error, available);
}

/// <summary>Read-only assessment; null counts mean protection was not proven.</summary>
public sealed record BackupProtectionDetailResponse(
    Guid BackupRunId,
    string Assessment,
    string? State,
    int? VerifiedIndependentCopies,
    int? RequiredVerifiedCopies,
    int? DesiredVerifiedCopies,
    int? RequiredCopyDebt,
    int? DesiredCopyDebt,
    BackupCopyStatusResponse[] Copies);

/// <summary>Состояние физической копии без credentials, provider locator или сырых ошибок.</summary>
public sealed record BackupCopyStatusResponse(
    Guid Id,
    Guid DestinationId,
    string DestinationName,
    string DestinationKind,
    string State,
    string? ErrorCode,
    DateTimeOffset? VerifiedAt,
    bool HasNativeLocator,
    string? RouteRole,
    bool RouteEnabled,
    bool RouteDraining,
    string? LatestJobState,
    string? CatalogState)
{
    /// <summary>Проецирует только ограниченные поля для защищённой админки.</summary>
    public static BackupCopyStatusResponse From(BackupCopy copy, BackupPoolDestination? route)
    {
        var error = Enum.TryParse<BackupDestinationErrorCode>(copy.LastErrorCode, out var code) &&
            Enum.IsDefined(code) ? code.ToString() : null;
        return new BackupCopyStatusResponse(
            copy.Id,
            copy.BackupDestinationId,
            copy.BackupDestination.Name,
            copy.BackupDestination.Kind,
            copy.State,
            error,
            copy.VerifiedAt,
            !string.IsNullOrWhiteSpace(copy.NativeLocator),
            route?.Role,
            route?.Enabled == true,
            route?.Draining == true,
            copy.Jobs.OrderByDescending(job => job.UpdatedAt).FirstOrDefault()?.State,
            copy.CatalogState);
    }
}

/// <summary>Безопасное состояние настроенного назначения без provider locator и credentials.</summary>
public sealed record BackupDestinationOverviewResponse(
    Guid Id,
    string Name,
    string Kind,
    bool Enabled,
    int Priority,
    bool CredentialsConfigured,
    bool FailureDomainConfigured,
    BackupDestinationRouteResponse[] Routes,
    BackupDestinationOutcomeResponse? LastOutcome);

/// <summary>Публичная для администратора часть маршрута pool → destination.</summary>
public sealed record BackupDestinationRouteResponse(
    Guid PoolId,
    string PoolName,
    int PolicyVersion,
    int RequiredVerifiedCopies,
    int DesiredVerifiedCopies,
    string Role,
    string AllowedOperations,
    int Priority,
    bool Enabled,
    bool Draining)
{
    /// <summary>Проецирует маршрут без provider-specific настроек.</summary>
    public static BackupDestinationRouteResponse From(BackupPoolDestination route) => new(
        route.BackupPoolId, route.BackupPool.Name, route.BackupPool.PolicyVersion,
        route.BackupPool.RequiredVerifiedCopies, route.BackupPool.DesiredVerifiedCopies,
        route.Role, route.AllowedOperations, route.Priority, route.Enabled, route.Draining);
}

/// <summary>Последняя типизированная provider-операция; сырой ответ не возвращается.</summary>
public sealed record BackupDestinationOutcomeResponse(
    string Operation,
    bool Succeeded,
    string? ProbeOutcome,
    string? ErrorCode,
    DateTimeOffset ObservedAt)
{
    /// <summary>Разрешает только известные коды из durable audit.</summary>
    public static BackupDestinationOutcomeResponse From(BackupDestinationHealthOutcome outcome)
    {
        var error = Enum.TryParse<BackupDestinationErrorCode>(outcome.ErrorCode, out var code) &&
            Enum.IsDefined(code) ? code.ToString() : null;
        var probe = outcome.ProbeOutcome is "matching" or "missing" or "mismatching" or
            "inconclusive" or "invalid" ? outcome.ProbeOutcome : null;
        return new BackupDestinationOutcomeResponse(
            outcome.Operation is "put" or "verify" ? outcome.Operation : "unknown",
            outcome.Succeeded, probe, error, outcome.ObservedAt);
    }
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

/// <summary>Новый secret платного provider; существующее значение никогда не возвращается.</summary>
public sealed record ProxySourceCredentialRequest(
    [Required, StringLength(256, MinimumLength = 16)] string ApiKey,
    bool Enabled = true);

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
    int? CatalogRank,
    bool IsPaid,
    bool CredentialConfigured,
    string? CredentialStatus,
    DateTimeOffset? CredentialExpiresAt,
    DateTimeOffset? CredentialCheckedAt,
    string? CredentialError)
{
    /// <summary>Обогащает изменяемую запись БД каноническими метаданными каталога.</summary>
    public static SourceResponse From(ProxySource source)
    {
        var builtIn = BuiltInSourceCatalog.FindByUrl(source.Url);
        var paid = PaidProxySourceCatalog.IsPaid(source);
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
            paid ? "Best Proxies" : builtIn?.Provider,
            paid ? "best-proxies.ru" : builtIn?.ProviderIdentity,
            builtIn?.Rank,
            paid,
            source.Credential is not null,
            source.Credential?.Status,
            source.Credential?.ExpiresAt,
            source.Credential?.CheckedAt,
            source.Credential?.LastError);
    }
}
