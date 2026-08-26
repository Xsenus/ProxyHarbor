using System.Globalization;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Создаёт переносимый JSON-снимок БД, архивирует, шифрует и при необходимости отправляет его в Telegram.</summary>
public sealed class BackupService(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<BackupOptions> backupOptions,
    IOptions<CollectorOptions> collectorOptions,
    IConfiguration configuration,
    ILogger<BackupService> logger,
    IBackupConfigurationStore? backupConfigurationStore = null) : IDisposable
{
    internal const string PipeCompletionFailureDataKey = "ProxyHarbor.BackupPipeCompletionFailure";
    private const string PublishedBackupPrefix = "proxyharbor-";
    private const string PublishedBackupSuffix = ".phbackup";
    private const string PublishedBackupTimestampFormat = "yyyyMMdd-HHmmss-ffff";
    internal const int MaximumTelegramParts = 20;
    internal const string DeliveryPolicyErrorMarker = "[delivery-policy] ";
    private static readonly TimeSpan AuditWriteTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, string, Exception?> BackupCreated =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1201, "BackupCreated"), "Резервная копия создана: {BackupFile}");
    private static readonly Action<ILogger, Exception?> BackupAuditFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1203, "BackupAuditFailed"), "Не удалось сохранить аудит резервного копирования.");
    private static readonly Action<ILogger, string, Exception?> BackupCleanupFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1204, "BackupCleanupFailed"),
            "Не удалось удалить временный backup-файл {BackupFile}; следующий запуск повторит точную orphan-очистку.");
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Создаёт один снимок; секреты намеренно не сериализуются.</summary>
    public async Task<string> CreateAndSendAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new OperationAlreadyRunningException("резервное копирование");
        try
        {
            await using var databaseLease = await DatabaseRuntimeGate.TryAcquireOperationLeaseAsync(
                dbFactory, cancellationToken)
                ?? throw new OperationAlreadyRunningException("восстановление базы данных");
            await using var clusterLock = await PostgresAdvisoryLock.TryAcquireAsync(
                dbFactory, PostgresAdvisoryLock.BackupKey, cancellationToken)
                ?? throw new OperationAlreadyRunningException("резервное копирование");
            // Runtime-настройки перечитываются перед каждым запуском. Изменение
            // расписания/retention/Telegram в админке не требует рестарта контейнера.
            var options = backupConfigurationStore is null
                ? backupOptions.Value
                : await backupConfigurationStore.GetAsync(cancellationToken);
            if (!BackupOptions.IsNewEncryptionKeyValid(options.EncryptionKey))
                throw new InvalidOperationException(
                    $"Backup__EncryptionKey должен содержать {BackupOptions.MinimumEncryptionKeyLength}..{BackupOptions.MaximumEncryptionKeyLength} символов с корректной Unicode-кодировкой без управляющих знаков.");
            if (!BackupOptions.IsDirectoryValid(options.Directory))
                throw new InvalidOperationException(
                    "Backup__Directory должен быть абсолютным безопасным путём длиной не более 1024 символов.");

            var telegramConfigured = !string.IsNullOrWhiteSpace(options.TelegramBotToken) &&
                !string.IsNullOrWhiteSpace(options.TelegramChatId);
            var backupRun = new BackupRun { TelegramConfigured = telegramConfigured };
            await using (var auditDb = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                // Advisory lock уже гарантирует отсутствие живого backup в кластере:
                // оставшиеся running-записи могли появиться только после аварийного завершения процесса.
                var recoveredAt = DateTimeOffset.UtcNow;
                await auditDb.BackupRuns
                    .Where(x => x.Status == "running" && x.FinishedAt == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.FinishedAt, recoveredAt)
                        .SetProperty(x => x.Status, "failed")
                        .SetProperty(x => x.Error, "Backup был прерван аварийным завершением предыдущего процесса."),
                        cancellationToken);
                auditDb.BackupRuns.Add(backupRun);
                await auditDb.SaveChangesAsync(cancellationToken);
            }

            var stamp = DateTimeOffset.UtcNow.ToString(PublishedBackupTimestampFormat, CultureInfo.InvariantCulture);
            var encryptedPath = Path.Combine(
                options.Directory, $"{PublishedBackupPrefix}{stamp}{PublishedBackupSuffix}");
            var partialEncryptedPath = encryptedPath + ".partial";

            try
            {
                Directory.CreateDirectory(options.Directory);
                // Advisory lock доказывает отсутствие другого backup этой БД: можно безопасно
                // удалить legacy plaintext/partial артефакты после kill -9 или power loss.
                DeleteOrphanArtifacts(options.Directory);
                await using var strategyDb = await dbFactory.CreateDbContextAsync(cancellationToken);
                var strategy = strategyDb.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(() => CreateEncryptedSnapshotAsync(
                    partialEncryptedPath, backupRun.Id, options, telegramConfigured, cancellationToken));

                // Перечитываем ciphertext целиком и проверяем AEAD-тег каждого блока и
                // финального маркера. До успеха файл остаётся partial: retention и Telegram
                // никогда не увидят усечённую либо повреждённую резервную копию.
                await VerifyAndPublishAsync(
                    partialEncryptedPath, encryptedPath, options.EncryptionKey!, cancellationToken);

                // Локальная retention-политика не зависит от доступности Telegram. Иначе
                // продолжительный внешний сбой оставлял бы новый архив на каждом цикле,
                // никогда не удаляя старые файлы и в итоге мог исчерпать backup volume.
                ApplyRetention(options.Directory, options.RetentionDays, options.IntervalHours);
                var sentToTelegram = false;
                if (telegramConfigured)
                {
                    await SendToTelegramAsync(encryptedPath, options, cancellationToken);
                    // Значение становится true только после подтверждения ok=true для файла
                    // либо для каждой части; частичная отправка остаётся failed в audit.
                    sentToTelegram = true;
                }

                await CompleteAuditAsync(backupRun.Id, encryptedPath, sentToTelegram, options.HistoryRetentionDays);
                OperationalLogBoundary.Write(() => BackupCreated(logger, encryptedPath, null));
                return encryptedPath;
            }
            catch (Exception exception)
            {
                BackupFileCleanup.TryDeletePreservingPrimary(partialEncryptedPath, exception);
                await FailAuditAsync(backupRun.Id, encryptedPath, exception);
                throw;
            }
            finally
            {
                var cleanupFailure = BackupFileCleanup.TryDelete(partialEncryptedPath);
                if (cleanupFailure is not null)
                    OperationalLogBoundary.Write(() =>
                        BackupCleanupFailed(logger, Path.GetFileName(partialEncryptedPath), cleanupFailure));
            }
        }
        finally { _runGate.Release(); }
    }

    private async Task CompleteAuditAsync(
        Guid id,
        string path,
        bool sentToTelegram,
        int historyRetentionDays)
    {
        using var timeout = new CancellationTokenSource(AuditWriteTimeout);
        var token = timeout.Token;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var finishedAt = DateTimeOffset.UtcNow;
        var file = new FileInfo(path);
        // История очищается до completed transition: сбой retention тогда корректно
        // переводит текущую попытку в failed вместо ложного успешного аудита.
        await OperationalRetention.PruneBackupHistoryAsync(
            db, finishedAt, historyRetentionDays, token);
        // Завершить можно только собственную незавершённую попытку. Проверка числа строк
        // не позволяет сообщить об успехе, если audit-запись удалили или изменили параллельно.
        var updated = await db.BackupRuns
            .Where(x => x.Id == id && x.Status == "running")
            .ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.FinishedAt, finishedAt)
            .SetProperty(x => x.Status, "completed")
            .SetProperty(x => x.FileName, file.Name)
            .SetProperty(x => x.SizeBytes, file.Length)
            .SetProperty(x => x.SentToTelegram, sentToTelegram)
            .SetProperty(x => x.Error, (string?)null), token);

        if (updated != 1)
            throw new InvalidOperationException(
                "Backup-аудит потерял ownership своей running-строки.");

    }

    private async Task FailAuditAsync(Guid id, string encryptedPath, Exception exception)
    {
        try
        {
            using var timeout = new CancellationTokenSource(AuditWriteTimeout);
            var token = timeout.Token;
            await using var db = await dbFactory.CreateDbContextAsync(token);
            // Stable marker позволяет scheduler пережить restart без 15-минутного I/O storm,
            // но status остаётся failed и не сдвигает RPO/успешные backup metrics.
            var error = FormatAuditError(exception);
            var file = File.Exists(encryptedPath) ? new FileInfo(encryptedPath) : null;
            // Ошибка также принадлежит только активной попытке: чужой completed/failed
            // результат нельзя перезаписывать при обработке исключения финализации.
            await db.BackupRuns
                .Where(x => x.Id == id && x.Status == "running")
                .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.Status, "failed")
                .SetProperty(x => x.FileName, file == null ? null : file.Name)
                .SetProperty(x => x.SizeBytes, file == null ? 0 : file.Length)
                .SetProperty(x => x.Error, error[..Math.Min(2000, error.Length)]), token);
        }
        catch (Exception auditException)
        {
            // Сбой вторичного аудита не должен скрывать исходную причину отказа backup.
            OperationalLogBoundary.Write(() => BackupAuditFailed(logger, auditException));
        }
    }

    /// <summary>Кодирует только operational policy как стабильный scheduler marker.</summary>
    internal static string FormatAuditError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is BackupDeliveryPolicyException
            ? DeliveryPolicyErrorMarker + exception.Message
            : exception.ToString();
    }

    private static async Task WriteJsonAsync<T>(ZipArchive archive, string name, T value, CancellationToken token)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
    }

    /// <summary>Одновременно создаёт ZIP и шифрует его через bounded pipe без plaintext-файла.</summary>
    internal async Task CreateEncryptedSnapshotAsync(
        string partialEncryptedPath,
        Guid backupRunId,
        BackupOptions options,
        bool telegramConfigured,
        CancellationToken cancellationToken)
    {
        if (File.Exists(partialEncryptedPath)) File.Delete(partialEncryptedPath);
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 2 * 1024 * 1024,
            useSynchronizationContext: false));
        var producer = ProduceSnapshotZipAsync(
            pipe.Writer, backupRunId, options, telegramConfigured, pipelineCancellation.Token);
        var encryptor = EncryptSnapshotPipeAsync(
            pipe.Reader, partialEncryptedPath, options.EncryptionKey!, pipelineCancellation.Token);

        var first = await Task.WhenAny(producer, encryptor);
        if (first == producer)
        {
            try { await producer; }
            catch
            {
                await pipelineCancellation.CancelAsync();
                await IgnorePipelineFailureAsync(encryptor);
                throw;
            }
            await encryptor;
            return;
        }

        try { await encryptor; }
        catch
        {
            await pipelineCancellation.CancelAsync();
            await IgnorePipelineFailureAsync(producer);
            throw;
        }
        await producer;
    }

    private async Task ProduceSnapshotZipAsync(
        PipeWriter writer,
        Guid backupRunId,
        BackupOptions options,
        bool telegramConfigured,
        CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            await using var snapshot = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead, token);
            await using var output = writer.AsStream(leaveOpen: true);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteJsonAsync(archive, "database/proxies.json", db.Proxies.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/sources.json", db.Sources.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/vpn-sources.json", db.VpnSources.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/vpn-endpoints.json", db.VpnEndpoints.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/vpn-endpoint-sources.json", db.VpnEndpointSources.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/runs.json", db.Runs.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/validation-runs.json",
                    db.ValidationRuns.AsNoTracking().AsAsyncEnumerable(), token);
                // Текущий аудит завершается только после шифрования и Telegram-доставки,
                // поэтому в снимок входят лишь полностью определённые предыдущие попытки.
                await WriteJsonAsync(archive, "database/backup-runs.json",
                    db.BackupRuns.AsNoTracking().Where(x => x.Id != backupRunId).AsAsyncEnumerable(), token);
                // Identity rows входят в тот же repeatable-read snapshot. Исходных паролей
                // и reset token в этих таблицах нет; password hash защищён шифрованием PHB3.
                await WriteJsonAsync(archive, "database/users.json", db.Users.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/roles.json", db.Roles.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/user-roles.json", db.UserRoles.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/subscriptions.json", db.Subscriptions.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/payment-orders.json", db.PaymentOrders.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/subscription-admin-actions.json", db.SubscriptionAdminActions.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/proxy-access-buckets.json", db.ProxyAccessBuckets.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/site-visit-logs.json", db.SiteVisitLogs.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/free-proxy-export-grants.json", db.FreeProxyExportGrants.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/access-block-rules.json", db.AccessBlockRules.AsNoTracking().AsAsyncEnumerable(), token);
                // Реквизиты внутри записи уже зашифрованы Data Protection; внешний
                // .phbackup дополнительно шифрует весь архив как единое целое.
                await WriteJsonAsync(archive, "database/payment-configuration.json",
                    db.PaymentConfigurations.AsNoTracking().AsAsyncEnumerable(), token);
                // Telegram token и webhook secret уже защищены Data Protection. Сохраняем
                // также CRM, дедупликацию update и очередь, чтобы восстановление не вызвало
                // повторных оплат, рассылок или потери истории переписки.
                await WriteJsonAsync(archive, "database/telegram-bot-configuration.json",
                    db.TelegramBotConfigurations.AsNoTracking().AsAsyncEnumerable(), token);
                // Runtime backup-настройка сама входит в зашифрованный PHB3-снимок;
                // Telegram-реквизиты внутри неё дополнительно защищены Data Protection.
                await WriteJsonAsync(archive, "database/backup-configuration.json",
                    db.BackupConfigurations.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/telegram-chats.json",
                    db.TelegramChats.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/telegram-update-receipts.json",
                    db.TelegramUpdateReceipts.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/telegram-outbound-messages.json",
                    db.TelegramOutboundMessages.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/telegram-conversation-messages.json",
                    db.TelegramConversationMessages.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "settings/collector.json", collectorOptions.Value, token);
                await WriteJsonAsync(archive, "settings/backup.json",
                    BackupSettingsSnapshot.FromOptions(options, telegramConfigured), token);
                await WriteJsonAsync(archive, "settings/runtime.json",
                    BackupRuntimeSettings.FromConfiguration(configuration), token);
                await WriteJsonAsync(archive, "manifest.json",
                    new
                    {
                        // v6 допускает additive database entries; старые v6 архивы без
                        // payment-orders по-прежнему полностью восстанавливаются.
                        version = 6,
                        settingsSchemaVersion = 1,
                        createdAt = DateTimeOffset.UtcNow,
                        secretsIncluded = false
                    }, token);
            }
            await output.FlushAsync(token);
            await snapshot.CommitAsync(token);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await CompletePipePreservingPrimaryAsync(
                writer.CompleteAsync, failure, "writer");
        }
    }

    private static async Task EncryptSnapshotPipeAsync(
        PipeReader reader,
        string destination,
        string encryptionKey,
        CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            await using var input = reader.AsStream(leaveOpen: true);
            await BackupEncryption.EncryptAsync(input, destination, encryptionKey, token);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await CompletePipePreservingPrimaryAsync(
                reader.CompleteAsync, failure, "reader");
        }
    }

    /// <summary>
    /// Завершает одну сторону pipe. Secondary completion failure не может скрыть producer/
    /// encryptor failure; самостоятельный completion failure по-прежнему fail-closed.
    /// </summary>
    internal static async Task CompletePipePreservingPrimaryAsync(
        Func<Exception?, ValueTask> completeAsync,
        Exception? primaryFailure,
        string stage)
    {
        ArgumentNullException.ThrowIfNull(completeAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        try
        {
            await completeAsync(primaryFailure);
        }
        catch (Exception completionFailure)
        {
            if (primaryFailure is null) throw;
            try
            {
                var detail = $"{stage}: {completionFailure.GetType().Name}";
                primaryFailure.Data[PipeCompletionFailureDataKey] =
                    primaryFailure.Data[PipeCompletionFailureDataKey] is string previous
                        ? $"{previous} | {detail}"
                        : detail;
            }
            catch (Exception)
            {
                // Нестандартное read-only Exception.Data не может заменить primary failure.
            }
        }
    }

    private static async Task IgnorePipelineFailureAsync(Task task)
    {
        try { await task; }
        catch { }
    }

    private async Task SendToTelegramAsync(string path, BackupOptions options, CancellationToken token)
    {
        var partLimit = options.MaxTelegramFileSizeMb * 1024L * 1024L;
        var length = new FileInfo(path).Length;
        // Проверяем предел до создания первого temporary part: слишком большой локальный
        // encrypted backup сохраняется для ручного получения, но не создаёт upload storm.
        _ = BackupFileSplitter.RequiredPartCount(length, partLimit, MaximumTelegramParts);
        if (length <= partLimit)
        {
            await SendDocumentAsync(path, "ProxyHarbor: зашифрованная резервная копия", options, token);
            return;
        }

        await foreach (var part in BackupFileSplitter.SplitAsync(
            path, partLimit, MaximumTelegramParts, token))
            await SendDocumentAsync(part.Path, $"ProxyHarbor backup — часть {part.Number}/{part.Total}", options, token);
    }

    /// <summary>Публикует partial-файл атомарно только после полной криптографической проверки.</summary>
    internal static async Task VerifyAndPublishAsync(
        string partialPath,
        string finalPath,
        string encryptionKey,
        CancellationToken token)
    {
        try
        {
            await BackupEncryption.VerifyAsync(partialPath, encryptionKey, token);
            // Оба имени находятся в одном backup directory, поэтому move является
            // атомарным и наблюдатель никогда не увидит недописанный .phbackup.
            File.Move(partialPath, finalPath);
        }
        catch (Exception exception)
        {
            BackupFileCleanup.TryDeletePreservingPrimary(partialPath, exception);
            throw;
        }
    }

    private async Task SendDocumentAsync(string path, string caption, BackupOptions options, CancellationToken token)
    {
        var client = httpClientFactory.CreateClient("telegram");
        await TelegramBackupSender.SendAsync(
            client, path, caption, options.TelegramBotToken!, options.TelegramChatId!, token);
    }

    /// <summary>Ограничивает backup volume одновременно возрастом и ожидаемым числом плановых снимков.</summary>
    internal static void ApplyRetention(string directory, int retentionDays, int intervalHours)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
        // Совместно смонтированный volume может содержать ручные либо чужие архивы.
        // Retention владеет только точным namespace имён, создаваемых этим сервисом.
        var files = Directory.EnumerateFiles(
                directory, $"{PublishedBackupPrefix}*{PublishedBackupSuffix}", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => IsPublishedBackupName(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();
        var retained = files.Where(file => file.LastWriteTimeUtc >= cutoff).ToList();
        foreach (var expired in files.Where(file => file.LastWriteTimeUtc < cutoff)) expired.Delete();

        // Два дополнительных файла допускают текущий и recovery-снимок, но длительный
        // Telegram outage не превращает 15-минутные повторы в неограниченный рост volume.
        var scheduledCapacity = (int)Math.Ceiling(
            Math.Max(1, retentionDays) * 24d / Math.Max(1, intervalHours));
        var maxFiles = checked(scheduledCapacity + 2);
        foreach (var overflow in retained.Skip(maxFiles)) overflow.Delete();
    }

    /// <summary>Отличает опубликованный сервисом timestamped backup от соседних файлов volume.</summary>
    private static bool IsPublishedBackupName(string name)
    {
        var expectedLength = PublishedBackupPrefix.Length +
            PublishedBackupTimestampFormat.Length + PublishedBackupSuffix.Length;
        return name.Length == expectedLength &&
            name.StartsWith(PublishedBackupPrefix, StringComparison.Ordinal) &&
            name.EndsWith(PublishedBackupSuffix, StringComparison.Ordinal) &&
            DateTime.TryParseExact(
                name.AsSpan(PublishedBackupPrefix.Length, PublishedBackupTimestampFormat.Length),
                PublishedBackupTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _);
    }

    /// <summary>
    /// Безопасно сопоставляет имя опубликованной копии с файлом внутри настроенного каталога.
    /// Метод принимает только точный namespace файлов, создаваемых BackupService, поэтому
    /// административные download/delete endpoint'ы не могут выйти за пределы backup volume.
    /// </summary>
    public static bool TryResolvePublishedBackupPath(string? directory, string? fileName, out string path)
    {
        path = string.Empty;
        if (!BackupOptions.IsDirectoryValid(directory) || string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            !IsPublishedBackupName(fileName))
            return false;

        var normalizedDirectory = Path.GetFullPath(directory!)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(normalizedDirectory, fileName));
        if (!string.Equals(Path.GetDirectoryName(candidate), normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            return false;

        path = candidate;
        return true;
    }

    /// <summary>Удаляет только незавершённые служебные файлы, никогда не затрагивая готовый encrypted backup.</summary>
    internal static int DeleteOrphanArtifacts(string directory)
    {
        var removed = 0;
        // Один широкий enumeration безопаснее нескольких пересекающихся glob только
        // вместе с точным parser'ом ownership: похожий ручной файл не считается orphan.
        foreach (var path in Directory.EnumerateFiles(
            directory, $"{PublishedBackupPrefix}*", SearchOption.TopDirectoryOnly))
        {
            if (!IsOwnedOrphanName(Path.GetFileName(path))) continue;
            File.Delete(path);
            removed++;
        }
        return removed;
    }

    /// <summary>Распознаёт только legacy ZIP, unpublished partial и splitter parts собственного backup.</summary>
    private static bool IsOwnedOrphanName(string name)
    {
        const string legacyZipSuffix = ".zip";
        const string partialSuffix = ".partial";
        const string partPrefix = ".part";
        const string partSeparator = "-of-";
        var publishedNameLength = PublishedBackupPrefix.Length +
            PublishedBackupTimestampFormat.Length + PublishedBackupSuffix.Length;

        if (name.EndsWith(legacyZipSuffix, StringComparison.Ordinal))
            return IsGeneratedBackupStem(name.AsSpan(0, name.Length - legacyZipSuffix.Length));
        if (name.Length == publishedNameLength + partialSuffix.Length &&
            name.EndsWith(partialSuffix, StringComparison.Ordinal))
            return IsPublishedBackupName(name[..publishedNameLength]);
        if (name.Length <= publishedNameLength + partPrefix.Length ||
            !IsPublishedBackupName(name[..publishedNameLength]))
            return false;

        var partTail = name.AsSpan(publishedNameLength);
        if (!partTail.StartsWith(partPrefix, StringComparison.Ordinal)) return false;
        var separatorIndex = partTail.IndexOf(partSeparator, StringComparison.Ordinal);
        if (separatorIndex <= partPrefix.Length) return false;
        var partNumber = partTail[partPrefix.Length..separatorIndex];
        var totalParts = partTail[(separatorIndex + partSeparator.Length)..];
        return TryParseCanonicalPartNumber(partNumber, out var number) &&
            TryParseCanonicalPartNumber(totalParts, out var total) &&
            total >= 2 && number <= total;
    }

    private static bool IsGeneratedBackupStem(ReadOnlySpan<char> stem) =>
        stem.Length == PublishedBackupPrefix.Length + PublishedBackupTimestampFormat.Length &&
        stem.StartsWith(PublishedBackupPrefix, StringComparison.Ordinal) &&
        DateTime.TryParseExact(
            stem[PublishedBackupPrefix.Length..],
            PublishedBackupTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    /// <summary>D3 означает ровно три цифры до 999 и естественное расширение после 999.</summary>
    private static bool TryParseCanonicalPartNumber(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;
        return digits.Length >= 3 &&
            (digits.Length == 3 || digits[0] != '0') &&
            int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value >= 1;
    }

    /// <inheritdoc />
    public void Dispose() => _runGate.Dispose();
}

/// <summary>Безопасная часть runtime-конфигурации; значения секретов отсутствуют на уровне типа.</summary>
internal sealed record BackupRuntimeSettings(
    string[] CorsOrigins,
    string[] ForwardedHeaderKnownNetworks,
    string? AllowedHosts,
    IReadOnlyDictionary<string, string?> LogLevels,
    bool AdminApiKeyConfigured,
    bool AdminApiKeyIncluded,
    bool ConnectionStringConfigured,
    bool ConnectionStringIncluded,
    bool PaymentsEnabled,
    IReadOnlyDictionary<string, BackupPaymentProductSettings> PaymentProducts,
    string[] EnabledPaymentProviders,
    bool PaymentSecretsIncluded)
{
    internal static BackupRuntimeSettings FromConfiguration(IConfiguration configuration) => new(
        configuration.GetSection("Cors:Origins").Get<string[]>() ?? [],
        configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [],
        configuration["AllowedHosts"],
        configuration.GetSection("Logging:LogLevel").GetChildren()
            .ToDictionary(child => child.Key, child => child.Value, StringComparer.OrdinalIgnoreCase),
        !string.IsNullOrWhiteSpace(configuration["Security:AdminApiKey"]),
        AdminApiKeyIncluded: false,
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres")),
        ConnectionStringIncluded: false,
        configuration.GetValue<bool>("Payments:Enabled"),
        configuration.GetSection("Payments:Products").GetChildren().ToDictionary(
            child => child.Key,
            child => new BackupPaymentProductSettings(
                child.GetValue<bool>("Enabled"), child["Name"] ?? string.Empty,
                child["Plan"] ?? string.Empty, child.GetValue<int>("DurationDays"),
                child.GetValue<long>("AmountMinor"), child["Currency"] ?? string.Empty,
                child["Description"] ?? string.Empty),
            StringComparer.OrdinalIgnoreCase),
        configuration.GetSection("Payments:Providers").GetChildren()
            .Where(child => child.GetValue<bool>("Enabled")).Select(child => child.Key).ToArray(),
        PaymentSecretsIncluded: false);
}

/// <summary>Несекретная коммерческая часть одного продукта в backup.</summary>
internal sealed record BackupPaymentProductSettings(
    bool Enabled,
    string Name,
    string Plan,
    int DurationDays,
    long AmountMinor,
    string Currency,
    string Description);

/// <summary>
/// Полный безопасный срез BackupOptions. FromOptions fail-closed требует явно
/// классифицировать каждое новое свойство как экспортируемое либо секретное.
/// </summary>
internal sealed record BackupSettingsSnapshot(
    bool Enabled,
    int IntervalHours,
    string Directory,
    int RetentionDays,
    int HistoryRetentionDays,
    int MaxTelegramFileSizeMb,
    bool TelegramConfigured,
    bool SecretsIncluded)
{
    private static readonly HashSet<string> SecretOptionNames =
    [
        nameof(BackupOptions.EncryptionKey),
        nameof(BackupOptions.TelegramBotToken),
        nameof(BackupOptions.TelegramChatId)
    ];

    internal static BackupSettingsSnapshot FromOptions(BackupOptions options, bool telegramConfigured)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureEveryOptionIsClassified();
        return new BackupSettingsSnapshot(
            options.Enabled,
            options.IntervalHours,
            options.Directory,
            options.RetentionDays,
            options.HistoryRetentionDays,
            options.MaxTelegramFileSizeMb,
            telegramConfigured,
            SecretsIncluded: false);
    }

    private static void EnsureEveryOptionIsClassified()
    {
        var snapshotOnly = new HashSet<string>(
            [nameof(TelegramConfigured), nameof(SecretsIncluded)], StringComparer.Ordinal);
        var classified = typeof(BackupSettingsSnapshot).GetProperties()
            .Select(property => property.Name)
            .Where(name => !snapshotOnly.Contains(name))
            .Concat(SecretOptionNames)
            .ToHashSet(StringComparer.Ordinal);
        var actual = typeof(BackupOptions).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!classified.SetEquals(actual))
            throw new InvalidOperationException(
                "Каждое свойство BackupOptions должно быть явно включено в безопасный snapshot либо secret allowlist.");
    }
}

/// <summary>Потоково создаёт по одной временной части и гарантированно удаляет её после отправки.</summary>
internal static class BackupFileSplitter
{
    internal sealed record Part(string Path, int Number, int Total);

    internal static async IAsyncEnumerable<Part> SplitAsync(
        string path,
        long partLimit,
        int maximumParts,
        [EnumeratorCancellation] CancellationToken token)
    {
        var length = new FileInfo(path).Length;
        var totalParts = RequiredPartCount(length, partLimit, maximumParts);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        for (var partNumber = 1; partNumber <= totalParts; partNumber++)
        {
            var partPath = $"{path}.part{partNumber:D3}-of-{totalParts:D3}";
            try
            {
                await using (var output = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    buffer.Length, FileOptions.Asynchronous))
                {
                    var remaining = Math.Min(partLimit, length - input.Position);
                    while (remaining > 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                        if (read == 0) throw new EndOfStreamException("Резервная копия неожиданно оборвалась при разделении.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        remaining -= read;
                    }
                }
                yield return new Part(partPath, partNumber, totalParts);
            }
            finally
            {
                // Ошибка отправки/отмены важнее cleanup; точный orphan parser удалит
                // оставшуюся часть перед следующим cluster-locked backup.
                _ = BackupFileCleanup.TryDelete(partPath);
            }
        }
    }

    /// <summary>Точно и без floating-point вычисляет bounded число частей до filesystem writes.</summary>
    internal static int RequiredPartCount(long length, long partLimit, int maximumParts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(partLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumParts, 1);
        var totalParts = length == 0 ? 1 : 1 + ((length - 1) / partLimit);
        if (totalParts > maximumParts)
            throw new BackupDeliveryPolicyException(
                $"Backup требует {totalParts:N0} Telegram-частей при допустимом пределе {maximumParts:N0}; " +
                "зашифрованный локальный файл сохранён для ручного получения.");
        return checked((int)totalParts);
    }
}

/// <summary>Постоянный operational отказ доставки, который не исправится быстрым retry.</summary>
internal sealed class BackupDeliveryPolicyException(string message) : InvalidOperationException(message);

/// <summary>Запускает резервное копирование по расписанию только при явном включении.</summary>
public sealed class BackupWorker(
    BackupService backup,
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<BackupOptions> options,
    ILogger<BackupWorker> logger,
    IBackupConfigurationStore? backupConfigurationStore = null) : Microsoft.Extensions.Hosting.BackgroundService
{
    internal enum CycleOutcome { Succeeded, PeerOwned, Failed, DeliveryPolicyRejected }
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SettingsRefreshDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumScheduleWaitChunk = TimeSpan.FromDays(1);
    private static readonly Action<ILogger, Exception?> BackupFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1202, "BackupFailed"), "Не удалось создать резервную копию.");
    private static readonly Action<ILogger, Exception?> BackupScheduleReadFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1204, "BackupScheduleReadFailed"),
            "Не удалось восстановить расписание резервного копирования из PostgreSQL.");
    private static readonly Action<ILogger, Exception?> BackupDeliveryPolicyRejected =
        LoggerMessage.Define(LogLevel.Error, new EventId(1205, "BackupDeliveryPolicyRejected"),
            "Локальный backup создан, но превышает bounded policy Telegram-доставки.");
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = await GetOptionsAsync(stoppingToken);
            if (!current.Enabled)
            {
                // Worker не завершается: администратор может включить расписание без
                // перезапуска приложения. Минутный poll не создаёт нагрузки на БД.
                await Task.Delay(SettingsRefreshDelay, stoppingToken);
                continue;
            }
            await WaitUntilDueAsync(stoppingToken);
            current = await GetOptionsAsync(stoppingToken);
            if (!current.Enabled) continue;
            var outcome = CycleOutcome.Failed;
            try
            {
                await backup.CreateAndSendAsync(stoppingToken);
                outcome = CycleOutcome.Succeeded;
            }
            catch (OperationAlreadyRunningException)
            {
                // Другая реплика уже владеет cluster lock; не создаём retry-storm.
                outcome = CycleOutcome.PeerOwned;
            }
            catch (BackupDeliveryPolicyException exception)
            {
                OperationalLogBoundary.Write(() => BackupDeliveryPolicyRejected(logger, exception));
                outcome = CycleOutcome.DeliveryPolicyRejected;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                OperationalLogBoundary.Write(() => BackupFailed(logger, ex));
            }

            var cooldown = NextDelay(current.IntervalHours, outcome);
            // Длительный интервал разбиваем на короткие части, чтобы изменения
            // расписания и выключение worker применялись максимум за минуту.
            while (cooldown > TimeSpan.Zero && !stoppingToken.IsCancellationRequested)
            {
                var chunk = cooldown <= SettingsRefreshDelay ? cooldown : SettingsRefreshDelay;
                await Task.Delay(chunk, stoppingToken);
                cooldown -= chunk;
                var refreshed = await GetOptionsAsync(stoppingToken);
                if (!refreshed.Enabled || refreshed.IntervalHours != current.IntervalHours) break;
            }
        }
    }

    /// <summary>
    /// На старте и после каждого cooldown повторно читает persistent audit. Поэтому
    /// restart либо ручной backup не создаёт ещё один архив раньше полного интервала.
    /// </summary>
    private async Task WaitUntilDueAsync(CancellationToken token)
    {
        while (true)
        {
            try
            {
                var current = await GetOptionsAsync(token);
                if (!current.Enabled) return;
                var scheduleAnchorAt = await ReadLastScheduleAnchorAtAsync(dbFactory, token);
                var delay = InitialDelay(current.IntervalHours, scheduleAnchorAt, DateTimeOffset.UtcNow);
                if (delay == TimeSpan.Zero) return;
                await Task.Delay(delay <= SettingsRefreshDelay ? delay : SettingsRefreshDelay, token);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                // Недоступная БД не должна завершать весь Generic Host. Повторяем чтение
                // раньше суточного интервала, но с bounded паузой без tight loop.
                OperationalLogBoundary.Write(() => BackupScheduleReadFailed(logger, exception));
                await Task.Delay(FailureRetryDelay, token);
            }
        }
    }

    private Task<BackupOptions> GetOptionsAsync(CancellationToken token) =>
        backupConfigurationStore is null
            ? Task.FromResult(options.Value)
            : backupConfigurationStore.GetAsync(token);

    /// <summary>Читает только подтверждённый completed audit; failed/running не сдвигают RPO.</summary>
    internal static async Task<DateTimeOffset?> ReadLastCompletedAtAsync(
        IDbContextFactory<ProxyHarborDbContext> factory,
        CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.BackupRuns.AsNoTracking()
            .Where(run => run.Status == "completed" && run.FinishedAt != null)
            .MaxAsync(run => (DateTimeOffset?)run.FinishedAt, token);
    }

    /// <summary>
    /// Permanent delivery-policy rejection ограничивает cadence, но не считается успехом:
    /// diagnostics, freshness alarms и RPO по-прежнему используют только completed.
    /// </summary>
    internal static async Task<DateTimeOffset?> ReadLastScheduleAnchorAtAsync(
        IDbContextFactory<ProxyHarborDbContext> factory,
        CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.BackupRuns.AsNoTracking()
            .Where(run => run.FinishedAt != null &&
                (run.Status == "completed" ||
                    (run.Status == "failed" && run.Error != null &&
                        run.Error.StartsWith(BackupService.DeliveryPolicyErrorMarker))))
            .MaxAsync(run => (DateTimeOffset?)run.FinishedAt, token);
    }

    /// <summary>Вычисляет bounded остаток интервала, устойчивый к backward clock skew.</summary>
    internal static TimeSpan InitialDelay(
        int intervalHours,
        DateTimeOffset? lastCompletedAt,
        DateTimeOffset now)
    {
        if (!lastCompletedAt.HasValue) return TimeSpan.Zero;
        var interval = TimeSpan.FromHours(Math.Max(1, intervalHours));
        var remaining = lastCompletedAt.Value.Add(interval) - now;
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
        return remaining >= interval ? interval : remaining;
    }

    /// <summary>
    /// Длинный разрешённый интервал до года разбивается на переносимые суточные
    /// timer chunks; между ними worker замечает новый ручной backup или clock shift.
    /// </summary>
    internal static TimeSpan WaitChunk(TimeSpan delay) =>
        delay <= MaximumScheduleWaitChunk ? delay : MaximumScheduleWaitChunk;

    /// <summary>
    /// После ошибки или занятого cluster-lock повторно читает persistent audit через
    /// bounded cooldown. Успех peer восстановит остаток штатного интервала, а его
    /// авария не оставит backup-просрок незамеченным до следующих суток.
    /// </summary>
    internal static TimeSpan NextDelay(int intervalHours, CycleOutcome outcome)
    {
        var regularDelay = TimeSpan.FromHours(Math.Max(1, intervalHours));
        return outcome switch
        {
            CycleOutcome.Succeeded => regularDelay,
            CycleOutcome.DeliveryPolicyRejected => regularDelay,
            CycleOutcome.PeerOwned or CycleOutcome.Failed =>
                regularDelay <= FailureRetryDelay ? regularDelay : FailureRetryDelay,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }
}
