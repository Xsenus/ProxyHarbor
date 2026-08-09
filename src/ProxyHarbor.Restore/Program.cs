using System.IO.Compression;
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
                    $"{counts.Runs:N0} циклов сбора, {counts.BackupRuns:N0} циклов backup.");
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
                """COPY "Proxies" ("Id", "Host", "Port", "Protocol", "Status", "LatencyMs", "ExitIp", "CountryCode", "IsAnonymous", "FirstSeenAt", "LastSeenAt", "LastCheckedAt", "NextCheckAt", "CheckLeaseUntil", "CheckLeaseId", "SuccessfulChecks", "FailedChecks", "ConsecutiveFailedChecks", "LastError") FROM STDIN (FORMAT BINARY)""",
                WriteProxyAsync,
                token);
            var sourceCount = await ImportAsync<ProxySource>(
                archive,
                "database/sources.json",
                connection,
                """COPY "Sources" ("Id", "Name", "Url", "DefaultProtocol", "Enabled", "Priority", "LastFetchedAt", "LastSucceededAt", "NextFetchAt", "LastItemCount", "ConsecutiveFailures", "LastError") FROM STDIN (FORMAT BINARY)""",
                WriteSourceAsync,
                token);
            var runCount = await ImportAsync<CollectionRun>(
                archive,
                "database/runs.json",
                connection,
                """COPY "Runs" ("Id", "StartedAt", "FinishedAt", "SourcesProcessed", "SourcesSucceeded", "SourcesFailed", "SourcesSkipped", "CandidatesFound", "NewProxies", "AliveProxies", "Status", "Error") FROM STDIN (FORMAT BINARY)""",
                WriteRunAsync,
                token);
            var backupRunCount = archive.GetEntry("database/backup-runs.json") is null
                ? 0
                : await ImportAsync<BackupRun>(
                    archive,
                    "database/backup-runs.json",
                    connection,
                    """COPY "BackupRuns" ("Id", "StartedAt", "FinishedAt", "Status", "FileName", "SizeBytes", "TelegramConfigured", "SentToTelegram", "Error") FROM STDIN (FORMAT BINARY)""",
                    WriteBackupRunAsync,
                    token);
            await transaction.CommitAsync(token);
            return new RestoreCounts(proxyCount, sourceCount, runCount, backupRunCount);
        });
    }

    private static async Task<int> ImportAsync<TEntity>(
        ZipArchive archive,
        string entryName,
        NpgsqlConnection connection,
        string copyCommand,
        Func<NpgsqlBinaryImporter, TEntity, CancellationToken, ValueTask> writeEntity,
        CancellationToken token)
    {
        var count = 0;
        await using var stream = BackupArchiveValidator.RequiredEntry(archive, entryName).Open();
        await using var importer = await connection.BeginBinaryImportAsync(copyCommand, token);
        await foreach (var entity in JsonSerializer.DeserializeAsyncEnumerable<TEntity>(stream, JsonOptions, token))
        {
            if (entity is null) throw new InvalidDataException($"Файл {entryName} содержит пустой объект.");
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
        await writer.WriteAsync(entity.LastItemCount, token);
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
        await writer.WriteAsync(entity.CandidatesFound, token);
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

    private sealed record RestoreCounts(int Proxies, int Sources, int Runs, int BackupRuns);
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
        а ключ — из Backup__EncryptionKey. Их можно передать параметрами
        --connection и --encryption-key, но переменные окружения безопаснее.
        """;

    public static RestoreOptions Parse(string[] args)
    {
        string? input = null;
        string? connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        string? key = Environment.GetEnvironmentVariable("Backup__EncryptionKey");
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
