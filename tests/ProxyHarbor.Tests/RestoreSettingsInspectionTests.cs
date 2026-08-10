using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Защищает полный и безопасный JSON-контракт аварийного извлечения настроек.</summary>
public sealed class RestoreSettingsInspectionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string EncryptionKey = "settings-inspection-key-32-characters";

    [Fact]
    public void ReadsValidatedVersionFiveSettingsWithoutSecrets()
    {
        using var archive = CreateArchive(version: 5);
        BackupArchiveValidator.Validate(archive);

        var inspection = RestoreApplication.ReadSettingsInspection(archive);
        var json = JsonSerializer.Serialize(inspection, JsonOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(5, root.GetProperty("manifest").GetProperty("version").GetInt32());
        Assert.Equal(800, root.GetProperty("collector").GetProperty("validationConcurrency").GetInt32());
        Assert.False(root.GetProperty("backup").GetProperty("secretsIncluded").GetBoolean());
        Assert.False(root.GetProperty("runtime").GetProperty("adminApiKeyIncluded").GetBoolean());
        Assert.False(root.GetProperty("runtime").GetProperty("connectionStringIncluded").GetBoolean());
        Assert.DoesNotContain("database-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("telegram-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsLegacyArchiveAsIncompleteSettingsSnapshot()
    {
        using var archive = CreateArchive(version: 4);
        BackupArchiveValidator.Validate(archive);

        var exception = Assert.Throws<InvalidDataException>(
            () => RestoreApplication.ReadSettingsInspection(archive));

        Assert.Contains("только для backup manifest v5", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliWritesOnlyJsonToStandardOutputWithoutDatabaseConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-inspection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var zipPath = Path.Combine(directory, "snapshot.zip");
        var backupPath = Path.Combine(directory, "snapshot.phbackup");
        try
        {
            await File.WriteAllBytesAsync(zipPath, CreateArchiveBytes(version: 5));
            await BackupEncryption.EncryptAsync(zipPath, backupPath, EncryptionKey, CancellationToken.None);
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(typeof(RestoreApplication).Assembly.Location);
            startInfo.ArgumentList.Add("--input");
            startInfo.ArgumentList.Add(backupPath);
            startInfo.ArgumentList.Add("--encryption-key");
            startInfo.ArgumentList.Add(EncryptionKey);
            startInfo.ArgumentList.Add("--inspect-settings");
            startInfo.Environment.Remove("ConnectionStrings__Postgres");
            startInfo.Environment.Remove("SecretFiles__PostgresPassword");

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Restore CLI не запущен.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await standardOutput;
            var error = await standardError;

            Assert.Equal(0, process.ExitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal(5, document.RootElement.GetProperty("manifest").GetProperty("version").GetInt32());
            Assert.DoesNotContain("Проверка целостности", output, StringComparison.Ordinal);
            Assert.Contains("Проверка целостности", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ZipArchive CreateArchive(int version)
    {
        var stream = new MemoryStream(CreateArchiveBytes(version));
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static byte[] CreateArchiveBytes(int version)
    {
        var collector = new CollectorOptions { ValidationConcurrency = 800 };
        var backup = BackupSettingsSnapshot.FromOptions(new BackupOptions(), telegramConfigured: false);
        var runtime = new BackupRuntimeSettings(
            ["https://dashboard.example.com"],
            ["10.0.0.0/8"],
            "proxy.example.com",
            new Dictionary<string, string?> { ["Default"] = "Information" },
            AdminApiKeyConfigured: true,
            AdminApiKeyIncluded: false,
            ConnectionStringConfigured: true,
            ConnectionStringIncluded: false);
        var stream = new MemoryStream();
        using (var writer = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (version == 5)
                AddJson(writer, "manifest.json",
                    new { version, settingsSchemaVersion = 1, createdAt = DateTimeOffset.UtcNow, secretsIncluded = false });
            else
                AddJson(writer, "manifest.json",
                    new { version, createdAt = DateTimeOffset.UtcNow, secretsIncluded = false });
            AddJson(writer, "database/proxies.json", Array.Empty<object>());
            AddJson(writer, "database/sources.json", Array.Empty<object>());
            AddJson(writer, "database/runs.json", Array.Empty<object>());
            AddJson(writer, "database/validation-runs.json", Array.Empty<object>());
            AddJson(writer, "database/backup-runs.json", Array.Empty<object>());
            AddJson(writer, "settings/collector.json", collector);
            AddJson(writer, "settings/backup.json", backup);
            AddJson(writer, "settings/runtime.json", runtime);
        }
        return stream.ToArray();
    }

    private static void AddJson<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name);
        using var output = entry.Open();
        using var writer = new StreamWriter(output, Encoding.UTF8);
        writer.Write(JsonSerializer.Serialize(value, JsonOptions));
    }
}
