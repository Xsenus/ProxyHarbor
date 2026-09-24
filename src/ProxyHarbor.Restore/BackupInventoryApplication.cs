using System.Data;
using System.Text.Json;
using Npgsql;
using ProxyHarbor.Infrastructure;

/// <summary>
/// Read-only comparison of historical BackupRuns and local published files. A matching
/// name and size is only an inventory observation, never a backup-integrity verdict.
/// </summary>
internal static class BackupInventoryApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal const string Help = """
        ProxyHarbor inventory — read-only comparison of BackupRuns and local PHB3 files.

          inventory --backups-directory <existing-absolute-directory>

        Requires ConnectionStrings__Postgres; password may be supplied via
        SecretFiles__PostgresPassword. Prints JSON to stdout; no file or DB writes.
        A name/size match does not verify ciphertext, decryption, restore or remote copies.
        """;

    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"] or ["-h"])
            {
                Console.WriteLine(Help);
                return 0;
            }
            if (args.Length != 2 || args[0] != "--backups-directory" ||
                !Path.IsPathFullyQualified(args[1]) || !Directory.Exists(args[1]))
                throw new ArgumentException("Invalid inventory options.");

            var connectionString = RuntimeSecretConfiguration.ApplyPostgresPasswordFile(
                Environment.GetEnvironmentVariable("ConnectionStrings__Postgres"),
                Environment.GetEnvironmentVariable("SecretFiles__PostgresPassword"));
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Missing database configuration.");

            var runs = await ReadLegacyRunsAsync(connectionString, token);
            var files = ReadLocalFiles(args[1]);
            var report = Analyze(runs, files);
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Console.Error.WriteLine("Inventory interrupted.");
            return 130;
        }
        catch (Exception)
        {
            // Provider errors and connection strings may contain credentials or paths.
            Console.Error.WriteLine("Inventory failed; check directory and database configuration.");
            return 1;
        }
    }

    internal static async Task<IReadOnlyList<LegacyBackupRun>> ReadLegacyRunsAsync(
        string connectionString, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(token);
        // Deliberately uses only columns present in the deployed v7 schema.
        await using var command = new NpgsqlCommand("""
            SELECT "Id", "Status", "FileName", "SizeBytes", "SentToTelegram", "SentToObjectStorage"
            FROM "BackupRuns"
            ORDER BY "StartedAt" DESC, "Id" DESC
            LIMIT 100001
            """, connection, transaction) { CommandTimeout = 30 };
        var runs = new List<LegacyBackupRun>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (runs.Count == 100000)
                throw new InvalidOperationException("Inventory row limit exceeded.");
            runs.Add(new LegacyBackupRun(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        }
        return runs;
    }

    internal static IReadOnlyList<LocalBackupFile> ReadLocalFiles(string directory)
    {
        var files = new List<LocalBackupFile>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.phbackup", SearchOption.TopDirectoryOnly))
        {
            // Never dereference reparse points (including symlinks outside the backup volume).
            var isLink = (file.Attributes & FileAttributes.ReparsePoint) != 0;
            files.Add(new LocalBackupFile(file.Name, isLink ? null : file.Length));
            if (files.Count > 100000)
                throw new InvalidOperationException("Inventory file limit exceeded.");
        }
        return files;
    }

    internal static BackupInventoryReport Analyze(
        IReadOnlyList<LegacyBackupRun> runs, IReadOnlyList<LocalBackupFile> files)
    {
        var fileMap = files.ToDictionary(file => file.Name, StringComparer.Ordinal);
        var nameCounts = runs.Where(run => run.FileName is not null)
            .GroupBy(run => run.FileName!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var entries = runs.Select(run =>
        {
            var validName = BackupService.TryResolvePublishedBackupPath(
                Path.GetTempPath(), run.FileName, out _);
            var state = run.Status != "completed" ? "not-completed"
                : !validName ? "invalid-name"
                : nameCounts[run.FileName!] > 1 ? "duplicate-run-name"
                : !fileMap.TryGetValue(run.FileName!, out var file) ? "missing-local-file"
                : file.SizeBytes is null ? "linked-file-skipped"
                : file.SizeBytes != run.SizeBytes ? "size-mismatch"
                : "name-size-match-unverified";
            // Invalid DB-provided names and operational Error text are never echoed.
            return new BackupInventoryEntry(run.Id, validName ? run.FileName : null,
                state, run.SentToTelegram, run.SentToObjectStorage);
        }).ToArray();
        var orphanFiles = files.Where(file =>
                BackupService.TryResolvePublishedBackupPath(Path.GetTempPath(), file.Name, out _) &&
                !nameCounts.ContainsKey(file.Name))
            .Select(file => file.Name).Order(StringComparer.Ordinal).ToArray();
        var ignoredFiles = files.Count(file =>
            !BackupService.TryResolvePublishedBackupPath(Path.GetTempPath(), file.Name, out _));
        return new BackupInventoryReport("metadata-only-unverified", entries, orphanFiles, ignoredFiles);
    }
}

internal sealed record LegacyBackupRun(Guid Id, string Status, string? FileName,
    long SizeBytes, bool SentToTelegram, bool SentToObjectStorage);
internal sealed record LocalBackupFile(string Name, long? SizeBytes);
internal sealed record BackupInventoryEntry(Guid RunId, string? FileName, string State,
    bool SentToTelegram, bool SentToObjectStorage);
internal sealed record BackupInventoryReport(string Assurance, BackupInventoryEntry[] Runs,
    string[] OrphanFiles, int IgnoredFiles);
