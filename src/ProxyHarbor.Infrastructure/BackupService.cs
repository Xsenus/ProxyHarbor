using System.Globalization;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    ILogger<BackupService> logger) : IDisposable
{
    private static readonly TimeSpan AuditWriteTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, string, Exception?> BackupCreated =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1201, "BackupCreated"), "Резервная копия создана: {BackupFile}");
    private static readonly Action<ILogger, Exception?> BackupAuditFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1203, "BackupAuditFailed"), "Не удалось сохранить аудит резервного копирования.");
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Создаёт один снимок; секреты намеренно не сериализуются.</summary>
    public async Task<string> CreateAndSendAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new OperationAlreadyRunningException("резервное копирование");
        try
        {
            await using var clusterLock = await PostgresAdvisoryLock.TryAcquireAsync(
                dbFactory, PostgresAdvisoryLock.BackupKey, cancellationToken)
                ?? throw new OperationAlreadyRunningException("резервное копирование");
            var options = backupOptions.Value;
            if (string.IsNullOrWhiteSpace(options.EncryptionKey) || options.EncryptionKey.Length < 16)
                throw new InvalidOperationException("Backup__EncryptionKey должен содержать не менее 16 символов.");

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

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-ffff", CultureInfo.InvariantCulture);
            var encryptedPath = Path.Combine(options.Directory, $"proxyharbor-{stamp}.phbackup");
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

                // Финальное имя публикуется атомарно: наблюдатель каталога никогда не увидит
                // недописанный backup с расширением .phbackup.
                File.Move(partialEncryptedPath, encryptedPath);

                // Локальная retention-политика не зависит от доступности Telegram. Иначе
                // продолжительный внешний сбой оставлял бы новый архив на каждом цикле,
                // никогда не удаляя старые файлы и в итоге мог исчерпать backup volume.
                DeleteExpired(options.Directory, options.RetentionDays);
                var sentToTelegram = false;
                if (telegramConfigured)
                {
                    await SendToTelegramAsync(encryptedPath, options, cancellationToken);
                    // Значение становится true только после подтверждения ok=true для файла
                    // либо для каждой части; частичная отправка остаётся failed в audit.
                    sentToTelegram = true;
                }

                await CompleteAuditAsync(backupRun.Id, encryptedPath, sentToTelegram, options.HistoryRetentionDays);
                BackupCreated(logger, encryptedPath, null);
                return encryptedPath;
            }
            catch (Exception exception)
            {
                await FailAuditAsync(backupRun.Id, encryptedPath, exception);
                throw;
            }
            finally
            {
                if (File.Exists(partialEncryptedPath)) File.Delete(partialEncryptedPath);
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
        await db.BackupRuns.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.FinishedAt, finishedAt)
            .SetProperty(x => x.Status, "completed")
            .SetProperty(x => x.FileName, file.Name)
            .SetProperty(x => x.SizeBytes, file.Length)
            .SetProperty(x => x.SentToTelegram, sentToTelegram)
            .SetProperty(x => x.Error, (string?)null), token);

        var cutoff = finishedAt.AddDays(-historyRetentionDays);
        await db.BackupRuns.Where(x => x.StartedAt < cutoff).ExecuteDeleteAsync(token);
    }

    private async Task FailAuditAsync(Guid id, string encryptedPath, Exception exception)
    {
        try
        {
            using var timeout = new CancellationTokenSource(AuditWriteTimeout);
            var token = timeout.Token;
            await using var db = await dbFactory.CreateDbContextAsync(token);
            var error = exception.ToString();
            var file = File.Exists(encryptedPath) ? new FileInfo(encryptedPath) : null;
            await db.BackupRuns.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.Status, "failed")
                .SetProperty(x => x.FileName, file == null ? null : file.Name)
                .SetProperty(x => x.SizeBytes, file == null ? 0 : file.Length)
                .SetProperty(x => x.Error, error[..Math.Min(2000, error.Length)]), token);
        }
        catch (Exception auditException)
        {
            // Сбой вторичного аудита не должен скрывать исходную причину отказа backup.
            BackupAuditFailed(logger, auditException);
        }
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
                await WriteJsonAsync(archive, "database/runs.json", db.Runs.AsNoTracking().AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "database/validation-runs.json",
                    db.ValidationRuns.AsNoTracking().AsAsyncEnumerable(), token);
                // Текущий аудит завершается только после шифрования и Telegram-доставки,
                // поэтому в снимок входят лишь полностью определённые предыдущие попытки.
                await WriteJsonAsync(archive, "database/backup-runs.json",
                    db.BackupRuns.AsNoTracking().Where(x => x.Id != backupRunId).AsAsyncEnumerable(), token);
                await WriteJsonAsync(archive, "settings/collector.json", collectorOptions.Value, token);
                await WriteJsonAsync(archive, "settings/backup.json",
                    BackupSettingsSnapshot.FromOptions(options, telegramConfigured), token);
                await WriteJsonAsync(archive, "settings/runtime.json",
                    BackupRuntimeSettings.FromConfiguration(configuration), token);
                await WriteJsonAsync(archive, "manifest.json",
                    new
                    {
                        version = 5,
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
            await writer.CompleteAsync(failure);
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
            await reader.CompleteAsync(failure);
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
        if (length <= partLimit)
        {
            await SendDocumentAsync(path, "ProxyHarbor: зашифрованная резервная копия", options, token);
            return;
        }

        await foreach (var part in BackupFileSplitter.SplitAsync(path, partLimit, token))
            await SendDocumentAsync(part.Path, $"ProxyHarbor backup — часть {part.Number}/{part.Total}", options, token);
    }

    private async Task SendDocumentAsync(string path, string caption, BackupOptions options, CancellationToken token)
    {
        var client = httpClientFactory.CreateClient("telegram");
        await TelegramBackupSender.SendAsync(
            client, path, caption, options.TelegramBotToken!, options.TelegramChatId!, token);
    }

    private static void DeleteExpired(string directory, int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
        foreach (var file in Directory.EnumerateFiles(directory, "*.phbackup"))
            if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
    }

    /// <summary>Удаляет только незавершённые служебные файлы, никогда не затрагивая готовый encrypted backup.</summary>
    internal static int DeleteOrphanArtifacts(string directory)
    {
        var removed = 0;
        string[] patterns = ["proxyharbor-*.zip", "proxyharbor-*.phbackup.partial", "proxyharbor-*.phbackup.part*-of-*"];
        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                File.Delete(path);
                removed++;
            }
        }
        return removed;
    }

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
    bool ConnectionStringIncluded)
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
        ConnectionStringIncluded: false);
}

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
        [EnumeratorCancellation] CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partLimit, 1);
        var length = new FileInfo(path).Length;
        var totalParts = checked((int)Math.Ceiling((double)length / partLimit));
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
                if (File.Exists(partPath)) File.Delete(partPath);
            }
        }
    }
}

/// <summary>Запускает резервное копирование по расписанию только при явном включении.</summary>
public sealed class BackupWorker(BackupService backup, IOptions<BackupOptions> options, ILogger<BackupWorker> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    private static readonly Action<ILogger, Exception?> BackupFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1202, "BackupFailed"), "Не удалось создать резервную копию.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.Value.IntervalHours)));
        do
        {
            try { await backup.CreateAndSendAsync(stoppingToken); }
            catch (OperationAlreadyRunningException) { }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { BackupFailed(logger, ex); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
