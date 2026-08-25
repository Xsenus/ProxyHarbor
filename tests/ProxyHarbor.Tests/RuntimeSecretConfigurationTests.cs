using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует приоритет, bounds и безопасный разбор runtime secret-файлов.</summary>
public sealed class RuntimeSecretConfigurationTests
{
    [Fact]
    public void ApplyOverridesEverySecretAndEscapesPostgresPassword()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var postgresPath = WriteSecret(directory, "postgres", "p;a'ss word\r\n");
            var adminPath = WriteSecret(directory, "admin", "admin-key-at-least-24-characters\n");
            var adminPasswordPath = WriteSecret(directory, "admin-password", "admin-password-at-least-24-characters\n");
            var encryptionPath = WriteSecret(directory, "encryption", "encryption-key-at-least-32-characters");
            var tokenPath = WriteSecret(directory, "telegram-token", "123456:bot-token");
            var chatPath = WriteSecret(directory, "telegram-chat", "-1001234567890");
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=db;Database=harbor;Username=user;Password=environment-value",
                ["Security:AdminApiKey"] = "environment-admin-value",
                ["Backup:EncryptionKey"] = "environment-encryption-value",
                ["SecretFiles:PostgresPassword"] = postgresPath,
                ["SecretFiles:AdminApiKey"] = adminPath,
                ["SecretFiles:AdminPassword"] = adminPasswordPath,
                ["SecretFiles:BackupEncryptionKey"] = encryptionPath,
                ["SecretFiles:TelegramBotToken"] = tokenPath,
                ["SecretFiles:TelegramChatId"] = chatPath
            });

            RuntimeSecretConfiguration.Apply(configuration);

            var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Postgres"));
            Assert.Equal("p;a'ss word", connection.Password);
            Assert.Equal("db", connection.Host);
            Assert.Equal("admin-key-at-least-24-characters", configuration["Security:AdminApiKey"]);
            Assert.Equal("admin-password-at-least-24-characters", configuration["Security:AdminPassword"]);
            Assert.Equal("encryption-key-at-least-32-characters", configuration["Backup:EncryptionKey"]);
            Assert.Equal("123456:bot-token", configuration["Backup:TelegramBotToken"]);
            Assert.Equal("-1001234567890", configuration["Backup:TelegramChatId"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingOrRelativeSecretFailsWithoutEchoingPathInMessage()
    {
        var relative = Assert.Throws<InvalidOperationException>(() =>
            RuntimeSecretConfiguration.ReadOptionalFile("relative-secret", "SecretFiles__AdminApiKey"));
        Assert.Contains("SecretFiles__AdminApiKey", relative.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("relative-secret", relative.Message, StringComparison.Ordinal);

        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");
        var missing = Assert.Throws<InvalidOperationException>(() =>
            RuntimeSecretConfiguration.ReadOptionalFile(missingPath, "SecretFiles__AdminApiKey"));
        Assert.Contains("SecretFiles__AdminApiKey", missing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(missingPath, missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedInvalidUtf8AndEmbeddedControlCharactersFailClosed()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var oversized = Path.Combine(directory, "oversized");
            File.WriteAllBytes(oversized, new byte[RuntimeSecretConfiguration.MaximumSecretBytes + 1]);
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeSecretConfiguration.ReadOptionalFile(oversized, "SecretFiles__AdminApiKey"));

            var invalidUtf8 = Path.Combine(directory, "invalid-utf8");
            File.WriteAllBytes(invalidUtf8, [0xC3, 0x28]);
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeSecretConfiguration.ReadOptionalFile(invalidUtf8, "SecretFiles__AdminApiKey"));

            var embeddedNewline = WriteSecret(directory, "embedded-newline", "first\nsecond");
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeSecretConfiguration.ReadOptionalFile(embeddedNewline, "SecretFiles__AdminApiKey"));

            var standaloneCarriageReturn = WriteSecret(directory, "standalone-cr", "secret\r");
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeSecretConfiguration.ReadOptionalFile(standaloneCarriageReturn, "SecretFiles__AdminApiKey"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmptyOptionalFileIsIgnoredAndExplicitConnectionCanRemainPasswordless()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var empty = WriteSecret(directory, "empty", "\r\n");
            Assert.Null(RuntimeSecretConfiguration.ReadOptionalFile(empty, "SecretFiles__TelegramBotToken"));
            Assert.Equal(
                "Host=db;Username=user",
                RuntimeSecretConfiguration.ApplyPostgresPasswordFile("Host=db;Username=user", null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string WriteSecret(string directory, string name, string value)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
