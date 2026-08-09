using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
                Console.WriteLine($"Восстановление завершено: {counts.Proxies:N0} прокси, {counts.Sources:N0} источников, {counts.Runs:N0} циклов.");
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
        ValidateManifest(archive);
        var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        await using var strategyDb = new ProxyHarborDbContext(dbOptions);
        await strategyDb.Database.MigrateAsync(token);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var db = new ProxyHarborDbContext(dbOptions);
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            // Замена выполняется в одной транзакции: при любой ошибке старая БД остаётся целой.
            await db.Runs.ExecuteDeleteAsync(token);
            await db.Proxies.ExecuteDeleteAsync(token);
            await db.Sources.ExecuteDeleteAsync(token);

            var proxyCount = await ImportAsync(archive, "database/proxies.json", db, db.Proxies, token);
            var sourceCount = await ImportAsync(archive, "database/sources.json", db, db.Sources, token);
            var runCount = await ImportAsync(archive, "database/runs.json", db, db.Runs, token);
            await transaction.CommitAsync(token);
            return new RestoreCounts(proxyCount, sourceCount, runCount);
        });
    }

    private static void ValidateManifest(ZipArchive archive)
    {
        var entry = RequiredEntry(archive, "manifest.json");
        using var stream = entry.Open();
        using var manifest = JsonDocument.Parse(stream);
        if (!manifest.RootElement.TryGetProperty("version", out var version) || version.GetInt32() != 2)
            throw new InvalidDataException("Версия manifest backup не поддерживается.");
        _ = RequiredEntry(archive, "database/proxies.json");
        _ = RequiredEntry(archive, "database/sources.json");
        _ = RequiredEntry(archive, "database/runs.json");
    }

    private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name) =>
        archive.GetEntry(name) ?? throw new InvalidDataException($"В backup отсутствует обязательный файл {name}.");

    private static async Task<int> ImportAsync<TEntity>(
        ZipArchive archive,
        string entryName,
        ProxyHarborDbContext db,
        DbSet<TEntity> destination,
        CancellationToken token) where TEntity : class
    {
        const int batchSize = 5_000;
        var count = 0;
        var batch = new List<TEntity>(batchSize);
        await using var stream = RequiredEntry(archive, entryName).Open();
        await foreach (var entity in JsonSerializer.DeserializeAsyncEnumerable<TEntity>(stream, JsonOptions, token))
        {
            if (entity is null) throw new InvalidDataException($"Файл {entryName} содержит пустой объект.");
            batch.Add(entity);
            if (batch.Count < batchSize) continue;
            await destination.AddRangeAsync(batch, token);
            await db.SaveChangesAsync(token);
            count += batch.Count;
            batch.Clear();
            db.ChangeTracker.Clear();
        }

        if (batch.Count > 0)
        {
            await destination.AddRangeAsync(batch, token);
            await db.SaveChangesAsync(token);
            count += batch.Count;
            db.ChangeTracker.Clear();
        }
        return count;
    }

    private sealed record RestoreCounts(int Proxies, int Sources, int Runs);
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
