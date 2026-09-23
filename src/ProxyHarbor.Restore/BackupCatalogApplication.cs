using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

/// <summary>Экспорт и офлайн-проверка подписанного несекретного sidecar catalog.</summary>
internal static class BackupCatalogApplication
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        try { return await RunAsync(BackupCatalogOptions.Parse(args), token); }
        catch (Exception)
        {
            Console.Error.WriteLine("Команда catalog отклонена: проверьте параметры.");
            return 1;
        }
    }

    internal static async Task<int> RunAsync(BackupCatalogOptions options, CancellationToken token)
    {
        try
        {
            if (options.Mode == "help")
            {
                Console.WriteLine(BackupCatalogOptions.Help);
                return 0;
            }
            options.Validate();
            token.ThrowIfCancellationRequested();
            var key = RuntimeSecretConfiguration.ReadOptionalFile(options.KeyFile, "--key-file")
                ?? throw new ArgumentException("Файл ключа пуст.");
            if (options.Mode == "inspect")
            {
                var file = new FileInfo(options.InputPath!);
                if (file.Length is < 1 or > 256 * 1024)
                    throw new InvalidDataException("Недопустимый размер каталога.");
                var bytes = await File.ReadAllBytesAsync(file.FullName, token);
                var snapshot = BackupCatalogService.Open(bytes, key);
                Console.WriteLine($"catalog v1; run={snapshot.BackupRunId}; " +
                    $"sha256={snapshot.Sha256}; keyRef={snapshot.KeyReference}; copies={snapshot.Copies.Length}");
                foreach (var copy in snapshot.Copies)
                    Console.WriteLine($"copy={copy.CopyId}; destination={copy.DestinationId}; " +
                        $"kind={copy.Kind}; locator={copy.NativeLocator}; verifiedAt={copy.VerifiedAt:O}");
                return 0;
            }

            var databaseOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(options.ConnectionString).Options;
            var service = new BackupCatalogService(new CatalogDbFactory(databaseOptions));
            var created = await service.CreateAsync(
                options.BackupRunId!.Value, options.KeyReference!, token);
            var encoded = BackupCatalogService.Seal(created, key);
            await WriteNewFileAsync(options.OutputPath!, encoded, token);
            Console.WriteLine($"Подписанный каталог создан: {Path.GetFileName(options.OutputPath)}; " +
                $"run={created.BackupRunId}; copies={created.Copies.Length}.");
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Console.Error.WriteLine("Команда catalog прервана.");
            return 130;
        }
        catch (Exception)
        {
            // Exception provider/БД/JSON не печатается: там могут быть пути или secrets.
            Console.Error.WriteLine("Команда catalog не выполнена: проверьте key file и inventory.");
            return 1;
        }
    }

    internal static async Task WriteNewFileAsync(string outputPath, byte[] bytes, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        var partial = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(
                partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await output.WriteAsync(bytes, token);
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, fullPath);
        }
        finally
        {
            try { File.Delete(partial); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Fail-safe: caller's final file is never removed.
            }
        }
    }

    private sealed class CatalogDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

/// <summary>Не принимает inline signing key или connection string.</summary>
internal sealed record BackupCatalogOptions(
    string Mode,
    Guid? BackupRunId,
    string? InputPath,
    string? OutputPath,
    string? KeyFile,
    string? ConnectionString,
    string? KeyReference)
{
    internal const string Help = """
        ProxyHarbor catalog — HMAC-подписанный inventory verified S3-копий.

        Экспорт из текущей БД в новый приватный файл:
          catalog export --backup-run-id <uuid> --output <absolute.catalog.json> \
            --key-file <absolute-backup-key-file> [--key-ref legacy]

        Проверка/просмотр без БД и provider I/O:
          catalog inspect --input <absolute.catalog.json> \
            --key-file <absolute-backup-key-file>

        Можно задать SecretFiles__BackupEncryptionKey вместо --key-file.
        Экспорт дополнительно требует ConnectionStrings__Postgres, пароль допускается
        в SecretFiles__PostgresPassword. Каталог не содержит bucket/endpoint/secrets.
        Храните каталог и файл ключа в разных защищённых местах.
        """;

    internal static BackupCatalogOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
            return new BackupCatalogOptions("help", null, null, null, null, null, null);
        if (args[0] is not ("export" or "inspect"))
            throw new ArgumentException("Неизвестная команда catalog.");
        var mode = args[0];
        Guid? runId = null;
        string? input = null;
        string? output = null;
        string? keyFile = null;
        string? keyReference = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option))
                throw new ArgumentException("Параметр catalog указан повторно.");
            switch (option)
            {
                case "--backup-run-id":
                    if (!Guid.TryParse(NextValue(args, ref index, option), out var parsed))
                        throw new ArgumentException("Некорректный backup-run-id.");
                    runId = parsed;
                    break;
                case "--input": input = NextValue(args, ref index, option); break;
                case "--output": output = NextValue(args, ref index, option); break;
                case "--key-file": keyFile = NextValue(args, ref index, option); break;
                case "--key-ref": keyReference = NextValue(args, ref index, option); break;
                default: throw new ArgumentException("Неизвестный параметр catalog.");
            }
        }
        keyFile ??= Environment.GetEnvironmentVariable("SecretFiles__BackupEncryptionKey");
        var connection = mode == "export"
            ? RuntimeSecretConfiguration.ApplyPostgresPasswordFile(
                Environment.GetEnvironmentVariable("ConnectionStrings__Postgres"),
                Environment.GetEnvironmentVariable("SecretFiles__PostgresPassword"))
            : null;
        return new BackupCatalogOptions(
            mode, runId, input, output, keyFile, connection, keyReference ?? "legacy");
    }

    internal void Validate()
    {
        if (Mode is not ("export" or "inspect"))
            throw new ArgumentException("Неизвестная команда catalog.");
        if (string.IsNullOrWhiteSpace(KeyFile) || !Path.IsPathFullyQualified(KeyFile) ||
            !File.Exists(KeyFile))
            throw new ArgumentException("Укажите существующий абсолютный key file.");
        if (Mode == "inspect")
        {
            if (BackupRunId is not null || OutputPath is not null ||
                KeyReference != "legacy" || string.IsNullOrWhiteSpace(InputPath) ||
                !Path.IsPathFullyQualified(InputPath) || !File.Exists(InputPath))
                throw new ArgumentException("Для inspect укажите только существующий --input.");
            return;
        }
        if (BackupRunId is not { } runId || runId == Guid.Empty || InputPath is not null ||
            string.IsNullOrWhiteSpace(ConnectionString) ||
            string.IsNullOrWhiteSpace(OutputPath) || !Path.IsPathFullyQualified(OutputPath) ||
            !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(OutputPath))) ||
            File.Exists(OutputPath) || Directory.Exists(OutputPath))
            throw new ArgumentException("Для export нужны run ID, БД и новый абсолютный output.");
        if (KeyReference is not { Length: >= 1 and <= 64 } ||
            !KeyReference.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.'))
            throw new ArgumentException("Некорректный key-ref.");
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"Для {option} требуется значение.");
        return args[index];
    }
}
