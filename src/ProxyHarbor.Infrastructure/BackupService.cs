using System.Globalization;
using System.IO.Compression;
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
            var zipPath = Path.Combine(options.Directory, $"proxyharbor-{stamp}.zip");
            var encryptedPath = zipPath + ".phbackup";
            var partialEncryptedPath = encryptedPath + ".partial";

            try
            {
                Directory.CreateDirectory(options.Directory);
                await using var strategyDb = await dbFactory.CreateDbContextAsync(cancellationToken);
                var strategy = strategyDb.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                    await using var file = File.Create(zipPath);
                    using var archive = new ZipArchive(file, ZipArchiveMode.Create);
                    await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
                    await WriteJsonAsync(archive, "database/proxies.json", db.Proxies.AsNoTracking().AsAsyncEnumerable(), cancellationToken);
                    await WriteJsonAsync(archive, "database/sources.json", db.Sources.AsNoTracking().AsAsyncEnumerable(), cancellationToken);
                    await WriteJsonAsync(archive, "database/runs.json", db.Runs.AsNoTracking().AsAsyncEnumerable(), cancellationToken);
                    // Текущий аудит завершается только после шифрования и Telegram-доставки,
                    // поэтому в снимок входят лишь полностью определённые предыдущие попытки.
                    await WriteJsonAsync(archive, "database/backup-runs.json",
                        db.BackupRuns.AsNoTracking().Where(x => x.Id != backupRun.Id).AsAsyncEnumerable(), cancellationToken);
                    await WriteJsonAsync(archive, "settings/collector.json", collectorOptions.Value, cancellationToken);
                    await WriteJsonAsync(archive, "settings/backup.json", new
                    {
                        options.Enabled,
                        options.IntervalHours,
                        options.RetentionDays,
                        options.HistoryRetentionDays,
                        options.MaxTelegramFileSizeMb,
                        telegramConfigured,
                        secretsIncluded = false
                    }, cancellationToken);
                    await WriteJsonAsync(archive, "settings/runtime.json",
                        BackupRuntimeSettings.FromConfiguration(configuration), cancellationToken);
                    await WriteJsonAsync(archive, "manifest.json", new { version = 3, createdAt = DateTimeOffset.UtcNow, secretsIncluded = false }, cancellationToken);
                    await snapshot.CommitAsync(cancellationToken);
                });

                // Финальное имя публикуется атомарно: наблюдатель каталога никогда не увидит
                // недописанный backup с расширением .phbackup.
                await BackupEncryption.EncryptAsync(zipPath, partialEncryptedPath, options.EncryptionKey, cancellationToken);
                File.Move(partialEncryptedPath, encryptedPath);
                File.Delete(zipPath);
                var sentToTelegram = false;
                if (telegramConfigured)
                {
                    await SendToTelegramAsync(encryptedPath, options, cancellationToken);
                    // Значение становится true только после подтверждения ok=true для файла
                    // либо для каждой части; частичная отправка остаётся failed в audit.
                    sentToTelegram = true;
                }

                DeleteExpired(options.Directory, options.RetentionDays);
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
                if (File.Exists(zipPath)) File.Delete(zipPath);
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
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var finishedAt = DateTimeOffset.UtcNow;
        var file = new FileInfo(path);
        await db.BackupRuns.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.FinishedAt, finishedAt)
            .SetProperty(x => x.Status, "completed")
            .SetProperty(x => x.FileName, file.Name)
            .SetProperty(x => x.SizeBytes, file.Length)
            .SetProperty(x => x.SentToTelegram, sentToTelegram)
            .SetProperty(x => x.Error, (string?)null), CancellationToken.None);

        var cutoff = finishedAt.AddDays(-historyRetentionDays);
        await db.BackupRuns.Where(x => x.StartedAt < cutoff).ExecuteDeleteAsync(CancellationToken.None);
    }

    private async Task FailAuditAsync(Guid id, string encryptedPath, Exception exception)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var error = exception.ToString();
            var file = File.Exists(encryptedPath) ? new FileInfo(encryptedPath) : null;
            await db.BackupRuns.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.Status, "failed")
                .SetProperty(x => x.FileName, file == null ? null : file.Name)
                .SetProperty(x => x.SizeBytes, file == null ? 0 : file.Length)
                .SetProperty(x => x.Error, error[..Math.Min(2000, error.Length)]), CancellationToken.None);
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

    public void Dispose() => _runGate.Dispose();
}

/// <summary>Безопасная часть runtime-конфигурации; значения секретов отсутствуют на уровне типа.</summary>
internal sealed record BackupRuntimeSettings(
    string[] CorsOrigins,
    string[] ForwardedHeaderKnownNetworks,
    bool AdminApiKeyConfigured,
    bool AdminApiKeyIncluded,
    bool ConnectionStringIncluded)
{
    internal static BackupRuntimeSettings FromConfiguration(IConfiguration configuration) => new(
        configuration.GetSection("Cors:Origins").Get<string[]>() ?? [],
        configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [],
        !string.IsNullOrWhiteSpace(configuration["Security:AdminApiKey"]),
        AdminApiKeyIncluded: false,
        ConnectionStringIncluded: false);
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
