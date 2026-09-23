using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

/// <summary>Извлечение ciphertext без restore БД; повреждённый source может быть quarantined.</summary>
internal static class BackupMaterializeApplication
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        try { return await RunAsync(BackupMaterializeOptions.Parse(args), token); }
        catch (Exception)
        {
            Console.Error.WriteLine("Чтение backup не удалось: проверьте параметры.");
            return 1;
        }
    }

    internal static async Task<int> RunAsync(BackupMaterializeOptions options, CancellationToken token)
    {
        try
        {
            if (options.ShowHelp)
            {
                Console.WriteLine(BackupMaterializeOptions.Help);
                return 0;
            }
            options.Validate();
            token.ThrowIfCancellationRequested();

            var databaseOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(options.ConnectionString).Options;
            var factory = new MaterializeDbFactory(databaseOptions);
            var protection = DataProtectionProvider.Create(
                new DirectoryInfo(options.DataProtectionKeysDirectory!),
                builder => builder.SetApplicationName("ProxyHarbor")
                    .DisableAutomaticKeyGeneration());
            var registry = new BackupDestinationRegistry([
                new S3BackupDestinationAdapter(new S3BackupObjectStorageTransport(), protection),
                new NoReadTelegramAdapter()
            ]);
            var materializer = new BackupCopyMaterializer(
                factory, registry, new BackupDestinationHealth());
            var result = await materializer.MaterializeAsync(
                options.BackupRunId!.Value, options.OutputPath!,
                TimeSpan.FromSeconds(options.DeadlineSeconds), token);
            Console.WriteLine($"Зашифрованный backup проверен и сохранён: {Path.GetFileName(result.Path)}; " +
                $"copy={result.BackupCopyId}; sha256={result.Sha256}.");
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Console.Error.WriteLine("Чтение backup прервано.");
            return 130;
        }
        catch (BackupDestinationOperationException exception)
        {
            Console.Error.WriteLine($"Чтение backup не удалось: {exception.Failure.Code}.");
            return 1;
        }
        catch (Exception)
        {
            // Исключения provider/БД могут содержать endpoint или connection string.
            Console.Error.WriteLine("Чтение backup не удалось: проверьте параметры, доступ к БД и копиям.");
            return 1;
        }
    }

    private sealed class MaterializeDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    /// <summary>Registry требует полный allowlist; Telegram materialization не поддерживается.</summary>
    private sealed class NoReadTelegramAdapter : IBackupDestinationAdapter
    {
        public string Kind => "telegram";
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(false), new(false), new(false), false, false, false);
    }
}

/// <summary>Параметры отдельной read-only команды без inline secrets.</summary>
internal sealed record BackupMaterializeOptions(
    Guid? BackupRunId,
    string? OutputPath,
    string? ConnectionString,
    string? DataProtectionKeysDirectory,
    int DeadlineSeconds,
    bool ShowHelp)
{
    internal const string Help = """
        ProxyHarbor materialize — получение существующей verified-копии без restore БД.

        dotnet run --project src/ProxyHarbor.Restore -- materialize \
          --backup-run-id <uuid> --output /private/path/backup.phbackup \
          --data-protection-keys-directory /isolated/copy/of/key-ring

        Строка БД читается из ConnectionStrings__Postgres (с поддержкой
        SecretFiles__PostgresPassword). Используйте только
        изолированную копию Data Protection key ring; команда не заменяет БД,
        но может перевести доказанно повреждённую copy в quarantined.
        Дедлайн по умолчанию 120 секунд, --deadline-seconds: 1..1800.
        Полученный PHB3 — только проверенный ciphertext; для restore отдельно
        используйте существующую команду --input и ключ расшифровки.
        """;

    internal static BackupMaterializeOptions Parse(string[] args)
    {
        Guid? runId = null;
        string? output = null;
        string? connection = null;
        string? keysDirectory = null;
        var deadline = 120;
        var help = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option))
                throw new ArgumentException("Параметр materialize указан повторно.");
            switch (option)
            {
                case "--backup-run-id":
                    if (!Guid.TryParse(NextValue(args, ref index, option), out var parsedId))
                        throw new ArgumentException("Некорректный backup-run-id.");
                    runId = parsedId;
                    break;
                case "--output": output = NextValue(args, ref index, option); break;
                case "--data-protection-keys-directory":
                    keysDirectory = NextValue(args, ref index, option);
                    break;
                case "--deadline-seconds":
                    if (!int.TryParse(NextValue(args, ref index, option),
                        NumberStyles.None, CultureInfo.InvariantCulture, out deadline))
                        throw new ArgumentException("Некорректный deadline-seconds.");
                    break;
                case "--help" or "-h": help = true; break;
                default: throw new ArgumentException("Неизвестный параметр materialize.");
            }
        }
        if (!help)
        {
            connection = RuntimeSecretConfiguration.ApplyPostgresPasswordFile(
                Environment.GetEnvironmentVariable("ConnectionStrings__Postgres"),
                Environment.GetEnvironmentVariable("SecretFiles__PostgresPassword"));
            keysDirectory ??= Environment.GetEnvironmentVariable("Security__DataProtectionKeysDirectory");
        }
        return new BackupMaterializeOptions(runId, output, connection, keysDirectory, deadline, help);
    }

    internal void Validate()
    {
        if (BackupRunId is not { } runId || runId == Guid.Empty)
            throw new ArgumentException("Укажите корректный backup-run-id.");
        if (string.IsNullOrWhiteSpace(OutputPath) || !Path.IsPathFullyQualified(OutputPath) ||
            !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(OutputPath))) ||
            File.Exists(OutputPath) || Directory.Exists(OutputPath))
            throw new ArgumentException("Укажите абсолютный output в существующем каталоге.");
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new ArgumentException("Не задана строка БД.");
        if (string.IsNullOrWhiteSpace(DataProtectionKeysDirectory) ||
            !Path.IsPathFullyQualified(DataProtectionKeysDirectory) ||
            !Directory.Exists(DataProtectionKeysDirectory) ||
            !Directory.EnumerateFiles(DataProtectionKeysDirectory, "key-*.xml").Any())
            throw new ArgumentException("Укажите существующую изолированную копию Data Protection key ring.");
        if (DeadlineSeconds is < 1 or > 1800)
            throw new ArgumentException("deadline-seconds должен быть в диапазоне 1..1800.");
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"Для {option} требуется значение.");
        return args[index];
    }
}
