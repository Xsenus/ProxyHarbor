using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

return await RestoreApplication.RunAsync(args);

/// <summary>Изолированная CLI-команда полного восстановления БД из зашифрованного backup.</summary>
internal static class RestoreApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = RestoreOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(RestoreOptions.Help);
                return 0;
            }

            options.Validate();
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"proxyharbor-restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var zipPath = Path.Combine(temporaryDirectory, "snapshot.zip");
                Console.WriteLine("Проверка целостности и расшифровка backup...");
                await BackupEncryption.DecryptAsync(options.InputFile!, zipPath, options.EncryptionKey!, CancellationToken.None);
                var counts = await RestoreDatabaseAsync(zipPath, options.ConnectionString!, CancellationToken.None);
                Console.WriteLine($"Восстановление завершено: {counts.Proxies:N0} прокси, {counts.Sources:N0} источников, " +
                    $"{counts.Runs:N0} циклов сбора, {counts.ValidationRuns:N0} validation-партий, " +
                    $"{counts.BackupRuns:N0} циклов backup.");
                return 0;
            }
            finally
            {
                // Расшифрованный ZIP содержит данные БД и никогда не должен оставаться во временном каталоге.
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Восстановление отменено: {exception.Message}");
            return 1;
        }
    }

    private static async Task<RestoreCounts> RestoreDatabaseAsync(string zipPath, string connectionString, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        BackupArchiveValidator.Validate(archive);
        var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        await using var strategyDb = new ProxyHarborDbContext(dbOptions);
        // Используем тот же startup-gate, что API: параллельный запуск реплик не может
        // одновременно применить pending DDL, пока restore готовит целевую схему.
        await DatabaseSeeder.MigrateSchemaAsync(strategyDb, token);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var db = new ProxyHarborDbContext(dbOptions);
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            // Замена выполняется в одной транзакции: при любой ошибке старая БД остаётся целой.
            await db.BackupRuns.ExecuteDeleteAsync(token);
            await db.ValidationRuns.ExecuteDeleteAsync(token);
            await db.Runs.ExecuteDeleteAsync(token);
            await db.Proxies.ExecuteDeleteAsync(token);
            await db.Sources.ExecuteDeleteAsync(token);

            // PostgreSQL binary COPY сохраняет потоковый характер restore и на больших снимках
            // на порядки быстрее отдельных INSERT, создаваемых ChangeTracker/SaveChanges.
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var proxyCount = await ImportAsync<ProxyEndpoint>(
                archive,
                "database/proxies.json",
                connection,
                """COPY "Proxies" ("Id", "Host", "Port", "Protocol", "Status", "LatencyMs", "ExitIp", "CountryCode", "IsAnonymous", "FirstSeenAt", "LastSeenAt", "LastCheckedAt", "LastValidationAttemptAt", "LastValidationDeferred", "NextCheckAt", "CheckLeaseUntil", "CheckLeaseId", "SuccessfulChecks", "FailedChecks", "ConsecutiveFailedChecks", "LastError") FROM STDIN (FORMAT BINARY)""",
                RestoreEntityValidator.ValidateProxy,
                WriteProxyAsync,
                token);
            var sourceCount = await ImportAsync<ProxySource>(
                archive,
                "database/sources.json",
                connection,
                """COPY "Sources" ("Id", "Name", "Url", "DefaultProtocol", "Enabled", "Priority", "LastFetchedAt", "LastSucceededAt", "NextFetchAt", "HttpETag", "HttpLastModifiedAt", "LastItemCount", "LastResultTruncated", "ConsecutiveFailures", "LastError") FROM STDIN (FORMAT BINARY)""",
                RestoreEntityValidator.ValidateSource,
                WriteSourceAsync,
                token);
            var runCount = await ImportAsync<CollectionRun>(
                archive,
                "database/runs.json",
                connection,
                """COPY "Runs" ("Id", "StartedAt", "FinishedAt", "SourcesProcessed", "SourcesSucceeded", "SourcesFailed", "SourcesSkipped", "SourcesTruncated", "CandidatesFound", "CandidateLimitReached", "NewProxies", "AliveProxies", "Status", "Error") FROM STDIN (FORMAT BINARY)""",
                RestoreEntityValidator.ValidateCollectionRun,
                WriteRunAsync,
                token);
            var validationRunCount = archive.GetEntry("database/validation-runs.json") is null
                ? 0
                : await ImportAsync<ValidationRun>(
                    archive,
                    "database/validation-runs.json",
                    connection,
                    """COPY "ValidationRuns" ("Id", "LeaseId", "StartedAt", "FinishedAt", "Claimed", "Checked", "Alive", "Deferred", "Status", "Error") FROM STDIN (FORMAT BINARY)""",
                    RestoreEntityValidator.ValidateValidationRun,
                    WriteValidationRunAsync,
                    token);
            var backupRunCount = archive.GetEntry("database/backup-runs.json") is null
                ? 0
                : await ImportAsync<BackupRun>(
                    archive,
                    "database/backup-runs.json",
                    connection,
                    """COPY "BackupRuns" ("Id", "StartedAt", "FinishedAt", "Status", "FileName", "SizeBytes", "TelegramConfigured", "SentToTelegram", "Error") FROM STDIN (FORMAT BINARY)""",
                    RestoreEntityValidator.ValidateBackupRun,
                    WriteBackupRunAsync,
                    token);
            await transaction.CommitAsync(token);
            return new RestoreCounts(proxyCount, sourceCount, runCount, validationRunCount, backupRunCount);
        });
    }

    private static async Task<int> ImportAsync<TEntity>(
        ZipArchive archive,
        string entryName,
        NpgsqlConnection connection,
        string copyCommand,
        Action<TEntity> validateEntity,
        Func<NpgsqlBinaryImporter, TEntity, CancellationToken, ValueTask> writeEntity,
        CancellationToken token)
    {
        var count = 0;
        await using var stream = BackupArchiveValidator.RequiredEntry(archive, entryName).Open();
        await using var importer = await connection.BeginBinaryImportAsync(copyCommand, token);
        await foreach (var entity in JsonSerializer.DeserializeAsyncEnumerable<TEntity>(stream, JsonOptions, token))
        {
            if (entity is null) throw new InvalidDataException($"Файл {entryName} содержит пустой объект.");
            validateEntity(entity);
            await writeEntity(importer, entity, token);
            count = checked(count + 1);
        }
        await importer.CompleteAsync(token);
        return count;
    }

    private static async ValueTask WriteProxyAsync(NpgsqlBinaryImporter writer, ProxyEndpoint entity, CancellationToken token)
    {
        await writer.StartRowAsync(token);
        await writer.WriteAsync(entity.Id, token);
        await writer.WriteAsync(entity.Host, token);
        await writer.WriteAsync(entity.Port, token);
        await writer.WriteAsync((int)entity.Protocol, token);
        await writer.WriteAsync((int)entity.Status, token);
        await WriteNullableValueAsync(writer, entity.LatencyMs, token);
        await WriteNullableReferenceAsync(writer, entity.ExitIp, token);
        await WriteNullableReferenceAsync(writer, entity.CountryCode, token);
        await writer.WriteAsync(entity.IsAnonymous, token);
        await writer.WriteAsync(entity.FirstSeenAt, token);
        await writer.WriteAsync(entity.LastSeenAt, token);
        await WriteNullableValueAsync(writer, entity.LastCheckedAt, token);
        await WriteNullableValueAsync(writer, entity.LastValidationAttemptAt, token);
        await writer.WriteAsync(entity.LastValidationDeferred, token);
        await WriteNullableValueAsync(writer, entity.NextCheckAt, token);
        await WriteNullableValueAsync(writer, entity.CheckLeaseUntil, token);
        await WriteNullableValueAsync(writer, entity.CheckLeaseId, token);
        await writer.WriteAsync(entity.SuccessfulChecks, token);
        await writer.WriteAsync(entity.FailedChecks, token);
        await writer.WriteAsync(entity.ConsecutiveFailedChecks, token);
        await WriteNullableReferenceAsync(writer, entity.LastError, token);
    }

    private static async ValueTask WriteSourceAsync(NpgsqlBinaryImporter writer, ProxySource entity, CancellationToken token)
    {
        await writer.StartRowAsync(token);
        await writer.WriteAsync(entity.Id, token);
        await writer.WriteAsync(entity.Name, token);
        await writer.WriteAsync(entity.Url, token);
        await writer.WriteAsync((int)entity.DefaultProtocol, token);
        await writer.WriteAsync(entity.Enabled, token);
        await writer.WriteAsync(entity.Priority, token);
        await WriteNullableValueAsync(writer, entity.LastFetchedAt, token);
        await WriteNullableValueAsync(writer, entity.LastSucceededAt, token);
        await WriteNullableValueAsync(writer, entity.NextFetchAt, token);
        await WriteNullableReferenceAsync(writer, entity.HttpETag, token);
        await WriteNullableValueAsync(writer, entity.HttpLastModifiedAt, token);
        await writer.WriteAsync(entity.LastItemCount, token);
        await writer.WriteAsync(entity.LastResultTruncated, token);
        await writer.WriteAsync(entity.ConsecutiveFailures, token);
        await WriteNullableReferenceAsync(writer, entity.LastError, token);
    }

    private static async ValueTask WriteRunAsync(NpgsqlBinaryImporter writer, CollectionRun entity, CancellationToken token)
    {
        await writer.StartRowAsync(token);
        await writer.WriteAsync(entity.Id, token);
        await writer.WriteAsync(entity.StartedAt, token);
        await WriteNullableValueAsync(writer, entity.FinishedAt, token);
        await writer.WriteAsync(entity.SourcesProcessed, token);
        await writer.WriteAsync(entity.SourcesSucceeded, token);
        await writer.WriteAsync(entity.SourcesFailed, token);
        await writer.WriteAsync(entity.SourcesSkipped, token);
        await writer.WriteAsync(entity.SourcesTruncated, token);
        await writer.WriteAsync(entity.CandidatesFound, token);
        await writer.WriteAsync(entity.CandidateLimitReached, token);
        await writer.WriteAsync(entity.NewProxies, token);
        await writer.WriteAsync(entity.AliveProxies, token);
        await writer.WriteAsync(entity.Status, token);
        await WriteNullableReferenceAsync(writer, entity.Error, token);
    }

    private static async ValueTask WriteBackupRunAsync(NpgsqlBinaryImporter writer, BackupRun entity, CancellationToken token)
    {
        await writer.StartRowAsync(token);
        await writer.WriteAsync(entity.Id, token);
        await writer.WriteAsync(entity.StartedAt, token);
        await WriteNullableValueAsync(writer, entity.FinishedAt, token);
        await writer.WriteAsync(entity.Status, token);
        await WriteNullableReferenceAsync(writer, entity.FileName, token);
        await writer.WriteAsync(entity.SizeBytes, token);
        await writer.WriteAsync(entity.TelegramConfigured, token);
        await writer.WriteAsync(entity.SentToTelegram, token);
        await WriteNullableReferenceAsync(writer, entity.Error, token);
    }

    private static async ValueTask WriteValidationRunAsync(
        NpgsqlBinaryImporter writer,
        ValidationRun entity,
        CancellationToken token)
    {
        await writer.StartRowAsync(token);
        await writer.WriteAsync(entity.Id, token);
        await writer.WriteAsync(entity.LeaseId, token);
        await writer.WriteAsync(entity.StartedAt, token);
        await WriteNullableValueAsync(writer, entity.FinishedAt, token);
        await writer.WriteAsync(entity.Claimed, token);
        await writer.WriteAsync(entity.Checked, token);
        await writer.WriteAsync(entity.Alive, token);
        await writer.WriteAsync(entity.Deferred, token);
        await writer.WriteAsync(entity.Status, token);
        await WriteNullableReferenceAsync(writer, entity.Error, token);
    }

    private static async ValueTask WriteNullableValueAsync<T>(NpgsqlBinaryImporter writer, T? value, CancellationToken token)
        where T : struct
    {
        if (!value.HasValue) await writer.WriteNullAsync(token);
        else await writer.WriteAsync(value.Value, token);
    }

    private static async ValueTask WriteNullableReferenceAsync<T>(NpgsqlBinaryImporter writer, T? value, CancellationToken token)
        where T : class
    {
        if (value is null) await writer.WriteNullAsync(token);
        else await writer.WriteAsync(value, token);
    }

    private sealed record RestoreCounts(int Proxies, int Sources, int Runs, int ValidationRuns, int BackupRuns);
}

/// <summary>Проверяет семантические инварианты backup-строк до записи очередной COPY row.</summary>
internal static class RestoreEntityValidator
{
    private static readonly HashSet<string> RunStatuses = ["running", "completed", "failed"];

    internal static void ValidateProxy(ProxyEndpoint entity)
    {
        RequireId(entity.Id, "proxy.id");
        if (!IPAddress.TryParse(entity.Host, out var host) || !NetworkSafety.IsPublicAddress(host) ||
            !string.Equals(host.ToString(), entity.Host, StringComparison.OrdinalIgnoreCase))
            Invalid("proxy.host должен быть каноническим публичным IP.");
        if (entity.Port is < 1 or > 65_535) Invalid("proxy.port должен находиться в диапазоне 1..65535.");
        RequireEnum(entity.Protocol, "proxy.protocol");
        RequireEnum(entity.Status, "proxy.status");
        if (entity.LatencyMs is < 0) Invalid("proxy.latencyMs не может быть отрицательным.");
        if (entity.ExitIp is not null &&
            (!IPAddress.TryParse(entity.ExitIp, out var exitIp) || !NetworkSafety.IsPublicAddress(exitIp) ||
                !string.Equals(exitIp.ToString(), entity.ExitIp, StringComparison.OrdinalIgnoreCase)))
            Invalid("proxy.exitIp должен быть каноническим публичным IP.");
        RequireOptionalText(entity.ExitIp, 64, "proxy.exitIp");
        RequireOptionalText(entity.CountryCode, 2, "proxy.countryCode");
        RequireOptionalText(entity.LastError, 500, "proxy.lastError", allowControlCharacters: true);
        if (entity.FirstSeenAt == default || entity.LastSeenAt < entity.FirstSeenAt)
            Invalid("proxy firstSeenAt/lastSeenAt имеют некорректный порядок.");
        if (entity.CheckLeaseUntil.HasValue != entity.CheckLeaseId.HasValue)
            Invalid("proxy check lease должен содержать одновременно время и token.");
        if (entity.LastValidationDeferred && entity.LastValidationAttemptAt is null)
            Invalid("deferred proxy должен содержать lastValidationAttemptAt.");
        RequireNonNegative(entity.SuccessfulChecks, "proxy.successfulChecks");
        RequireNonNegative(entity.FailedChecks, "proxy.failedChecks");
        RequireNonNegative(entity.ConsecutiveFailedChecks, "proxy.consecutiveFailedChecks");
        if ((long)entity.SuccessfulChecks + entity.FailedChecks > int.MaxValue)
            Invalid("proxy check counters переполняют вычисление successRate.");
        if (entity.ConsecutiveFailedChecks > entity.FailedChecks)
            Invalid("proxy.consecutiveFailedChecks не может превышать failedChecks.");
    }

    internal static void ValidateSource(ProxySource entity)
    {
        RequireId(entity.Id, "source.id");
        RequireText(entity.Name, 120, "source.name", minimumLength: 2);
        RequireText(entity.Url, 2048, "source.url");
        if (!Uri.TryCreate(entity.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            (!uri.IsDefaultPort && uri.Port != 443) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
            Invalid("source.url должен иметь допустимую HTTPS-форму без credentials/fragment.");
        RequireEnum(entity.DefaultProtocol, "source.defaultProtocol");
        if (entity.Priority is < -10_000 or > 10_000) Invalid("source.priority выходит за диапазон -10000..10000.");
        RequireNonNegative(entity.LastItemCount, "source.lastItemCount");
        RequireNonNegative(entity.ConsecutiveFailures, "source.consecutiveFailures");
        RequireOptionalText(entity.HttpETag, 512, "source.httpETag");
        if (entity.HttpETag is not null && !EntityTagHeaderValue.TryParse(entity.HttpETag, out _))
            Invalid("source.httpETag имеет некорректный HTTP-формат.");
        RequireOptionalText(entity.LastError, 500, "source.lastError", allowControlCharacters: true);
        if (entity.LastSucceededAt is not null &&
            (entity.LastFetchedAt is null || entity.LastSucceededAt > entity.LastFetchedAt))
            Invalid("source.lastSucceededAt не может быть новее lastFetchedAt.");
    }

    internal static void ValidateCollectionRun(CollectionRun entity)
    {
        RequireId(entity.Id, "collectionRun.id");
        RequireRunState(entity.Status, entity.StartedAt, entity.FinishedAt, "collectionRun");
        RequireNonNegative(entity.SourcesProcessed, "collectionRun.sourcesProcessed");
        RequireNonNegative(entity.SourcesSucceeded, "collectionRun.sourcesSucceeded");
        RequireNonNegative(entity.SourcesFailed, "collectionRun.sourcesFailed");
        RequireNonNegative(entity.SourcesSkipped, "collectionRun.sourcesSkipped");
        RequireNonNegative(entity.SourcesTruncated, "collectionRun.sourcesTruncated");
        RequireNonNegative(entity.CandidatesFound, "collectionRun.candidatesFound");
        RequireNonNegative(entity.NewProxies, "collectionRun.newProxies");
        RequireNonNegative(entity.AliveProxies, "collectionRun.aliveProxies");
        RequireOptionalText(entity.Error, 2000, "collectionRun.error", allowControlCharacters: true);
        if ((long)entity.SourcesSucceeded + entity.SourcesFailed != entity.SourcesProcessed)
            Invalid("collectionRun source counters не согласованы.");
        if (entity.SourcesTruncated > entity.SourcesSucceeded)
            Invalid("collectionRun.sourcesTruncated не может превышать sourcesSucceeded.");
        if (entity.NewProxies > entity.CandidatesFound)
            Invalid("collectionRun.newProxies не может превышать candidatesFound.");
    }

    internal static void ValidateValidationRun(ValidationRun entity)
    {
        RequireId(entity.Id, "validationRun.id");
        RequireId(entity.LeaseId, "validationRun.leaseId");
        RequireRunState(entity.Status, entity.StartedAt, entity.FinishedAt, "validationRun");
        RequireNonNegative(entity.Claimed, "validationRun.claimed");
        RequireNonNegative(entity.Checked, "validationRun.checked");
        RequireNonNegative(entity.Alive, "validationRun.alive");
        RequireNonNegative(entity.Deferred, "validationRun.deferred");
        RequireOptionalText(entity.Error, 2000, "validationRun.error", allowControlCharacters: true);
        if ((long)entity.Checked + entity.Deferred > entity.Claimed)
            Invalid("validationRun checked/deferred превышают claimed.");
        if (entity.Alive > entity.Checked)
            Invalid("validationRun.alive не может превышать checked.");
    }

    internal static void ValidateBackupRun(BackupRun entity)
    {
        RequireId(entity.Id, "backupRun.id");
        RequireRunState(entity.Status, entity.StartedAt, entity.FinishedAt, "backupRun");
        RequireNonNegative(entity.SizeBytes, "backupRun.sizeBytes");
        RequireOptionalText(entity.FileName, 255, "backupRun.fileName");
        RequireOptionalText(entity.Error, 2000, "backupRun.error", allowControlCharacters: true);
        if (entity.FileName is not null && entity.FileName.IndexOfAny(['/', '\\']) >= 0)
            Invalid("backupRun.fileName не может содержать путь.");
        if (entity.SentToTelegram && !entity.TelegramConfigured)
            Invalid("backupRun не может быть доставлен без Telegram-конфигурации.");
        if (entity.Status == "completed" && entity.TelegramConfigured && !entity.SentToTelegram)
            Invalid("завершённый backup с Telegram должен иметь подтверждение доставки.");
    }

    private static void RequireRunState(
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt,
        string field)
    {
        if (!RunStatuses.Contains(status)) Invalid($"{field}.status содержит неизвестное значение.");
        if ((status == "running") != (finishedAt is null))
            Invalid($"{field}.finishedAt не согласован со status.");
        if (startedAt == default || finishedAt < startedAt)
            Invalid($"{field} имеет некорректный порядок startedAt/finishedAt.");
    }

    private static void RequireEnum<T>(T value, string field) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) Invalid($"{field} содержит неизвестное enum-значение.");
    }

    private static void RequireId(Guid value, string field)
    {
        if (value == Guid.Empty) Invalid($"{field} не может быть пустым UUID.");
    }

    private static void RequireText(string? value, int maximumLength, string field, int minimumLength = 1)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < minimumLength || value.Length > maximumLength ||
            value.Any(char.IsControl))
            Invalid($"{field} имеет недопустимое содержимое или длину.");
    }

    private static void RequireOptionalText(
        string? value,
        int maximumLength,
        string field,
        bool allowControlCharacters = false)
    {
        if (value is not null &&
            (value.Length > maximumLength || !allowControlCharacters && value.Any(char.IsControl)))
            Invalid($"{field} имеет недопустимое содержимое или длину.");
    }

    private static void RequireNonNegative(int value, string field)
    {
        if (value < 0) Invalid($"{field} не может быть отрицательным.");
    }

    private static void RequireNonNegative(long value, string field)
    {
        if (value < 0) Invalid($"{field} не может быть отрицательным.");
    }

    private static void Invalid(string message) =>
        throw new InvalidDataException($"Backup содержит некорректную строку: {message}");
}

/// <summary>Минимальный разбор аргументов без дополнительных runtime-зависимостей.</summary>
internal sealed record RestoreOptions(
    string? InputFile,
    string? ConnectionString,
    string? EncryptionKey,
    bool ConfirmReplace,
    bool ShowHelp)
{
    public const string Help = """
        ProxyHarbor restore

        dotnet run --project src/ProxyHarbor.Restore -- \
          --input ./proxyharbor.phbackup --replace-existing-data

        По умолчанию строка БД читается из ConnectionStrings__Postgres,
        а ключ — из Backup__EncryptionKey. Docker использует bounded файлы
        SecretFiles__PostgresPassword и SecretFiles__BackupEncryptionKey.
        Явные --connection и --encryption-key имеют наивысший приоритет.
        """;

    public static RestoreOptions Parse(string[] args)
    {
        string? input = null;
        string? connection = null;
        string? key = null;
        var confirm = false;
        var help = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input": input = NextValue(args, ref index, "--input"); break;
                case "--connection": connection = NextValue(args, ref index, "--connection"); break;
                case "--encryption-key": key = NextValue(args, ref index, "--encryption-key"); break;
                case "--replace-existing-data": confirm = true; break;
                case "--help" or "-h": help = true; break;
                default: throw new ArgumentException($"Неизвестный аргумент: {args[index]}");
            }
        }

        // Help и явно переданные CLI-secrets не должны зависеть от доступности
        // container secret mounts. Файлы читаются только для отсутствующих defaults.
        if (!help)
        {
            connection ??= RuntimeSecretConfiguration.ApplyPostgresPasswordFile(
                Environment.GetEnvironmentVariable("ConnectionStrings__Postgres"),
                Environment.GetEnvironmentVariable("SecretFiles__PostgresPassword"));
            key ??= RuntimeSecretConfiguration.ReadOptionalFile(
                Environment.GetEnvironmentVariable("SecretFiles__BackupEncryptionKey"),
                "SecretFiles__BackupEncryptionKey")
                ?? Environment.GetEnvironmentVariable("Backup__EncryptionKey");
        }
        return new RestoreOptions(input, connection, key, confirm, help);
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(InputFile) || !File.Exists(InputFile))
            throw new ArgumentException("Укажите существующий backup через --input.");
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new ArgumentException("Не задана ConnectionStrings__Postgres.");
        if (string.IsNullOrWhiteSpace(EncryptionKey) || EncryptionKey.Length < 16)
            throw new ArgumentException("Не задан Backup__EncryptionKey длиной не менее 16 символов.");
        if (!ConfirmReplace)
            throw new ArgumentException("Операция заменяет данные БД; добавьте --replace-existing-data.");
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"Для {option} требуется значение.");
        return args[index];
    }
}
