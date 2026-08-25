using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Загружает container secrets из bounded read-only файлов до регистрации options.
/// Значения добавляются последним configuration provider и поэтому безопасно
/// перекрывают несекретные placeholders, не попадая в environment контейнера.
/// </summary>
public static class RuntimeSecretConfiguration
{
    internal const int MaximumSecretBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Применяет все поддерживаемые secret-file overrides к API configuration.</summary>
    public static void Apply(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var postgresPassword = ReadOptionalFile(
            configuration["SecretFiles:PostgresPassword"],
            "SecretFiles__PostgresPassword");
        if (postgresPassword is not null)
        {
            var connection = configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException(
                    "Для SecretFiles__PostgresPassword требуется ConnectionStrings__Postgres.");
            overrides["ConnectionStrings:Postgres"] = WithPostgresPassword(connection, postgresPassword);
        }

        AddSecretOverride(configuration, overrides, "SecretFiles:AdminApiKey", "Security:AdminApiKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:AdminPassword", "Security:AdminPassword");
        AddSecretOverride(configuration, overrides, "SecretFiles:SmtpPassword", "Email:Password");
        AddSecretOverride(configuration, overrides, "SecretFiles:BackupEncryptionKey", "Backup:EncryptionKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:TelegramBotToken", "Backup:TelegramBotToken");
        AddSecretOverride(configuration, overrides, "SecretFiles:TelegramChatId", "Backup:TelegramChatId");
        AddSecretOverride(configuration, overrides, "SecretFiles:YooKassaSecret", "Payments:Providers:yookassa:SecretKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:YooMoneyNotificationSecret", "Payments:Providers:yoomoney:SecretKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:CloudPaymentsSecret", "Payments:Providers:cloudpayments:SecretKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:RobokassaPassword1", "Payments:Providers:robokassa:SecretKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:RobokassaPassword2", "Payments:Providers:robokassa:SecondarySecret");
        AddSecretOverride(configuration, overrides, "SecretFiles:TBankPassword", "Payments:Providers:tbank:SecretKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:StripeSecret", "Payments:Providers:stripe:SecretKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:StripeWebhookSecret", "Payments:Providers:stripe:SecondarySecret");
        AddSecretOverride(configuration, overrides, "SecretFiles:CryptomusPaymentKey", "Payments:Providers:cryptomus:SecretKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:NowPaymentsApiKey", "Payments:Providers:nowpayments:SecretKey");
        AddSecretOverride(configuration, overrides, "SecretFiles:NowPaymentsIpnSecret", "Payments:Providers:nowpayments:SecondarySecret");

        if (overrides.Count > 0)
            configuration.AddInMemoryCollection(overrides);
    }

    /// <summary>
    /// Добавляет пароль к разобранной Npgsql-строке, не выполняя небезопасную
    /// конкатенацию для значений с `;`, кавычками или пробелами.
    /// </summary>
    public static string ApplyPostgresPasswordFile(string? connectionString, string? filePath)
    {
        var password = ReadOptionalFile(filePath, "SecretFiles__PostgresPassword");
        if (password is null) return connectionString ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Для SecretFiles__PostgresPassword требуется ConnectionStrings__Postgres.");
        return WithPostgresPassword(connectionString, password);
    }

    /// <summary>
    /// Читает небольшой UTF-8 secret. Один завершающий CRLF/LF удаляется для
    /// совместимости с file-based secret managers; внутренние control characters запрещены.
    /// </summary>
    public static string? ReadOptionalFile(string? filePath, string settingName)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        if (!Path.IsPathFullyQualified(filePath))
            throw new InvalidOperationException($"{settingName} должен содержать абсолютный путь.");

        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumSecretBytes)
                throw new InvalidOperationException(
                    $"Файл {settingName} превышает лимит {MaximumSecretBytes} байт.");

            var bytes = new byte[checked((int)stream.Length)];
            try
            {
                stream.ReadExactly(bytes);
                // Файл не должен незаметно вырасти между проверкой Length и чтением.
                if (stream.ReadByte() != -1)
                    throw new InvalidOperationException(
                        $"Файл {settingName} изменился во время чтения.");

                var length = bytes.Length;
                if (length > 0 && bytes[length - 1] == (byte)'\n')
                {
                    length--;
                    if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
                }
                if (length == 0) return null;

                var value = StrictUtf8.GetString(bytes, 0, length);
                if (value.Any(char.IsControl))
                    throw new InvalidOperationException(
                        $"Файл {settingName} содержит управляющие символы.");
                return value;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new InvalidOperationException($"Не удалось безопасно прочитать {settingName}.", exception);
        }
    }

    private static void AddSecretOverride(
        ConfigurationManager configuration,
        Dictionary<string, string?> overrides,
        string fileSetting,
        string targetSetting)
    {
        var value = ReadOptionalFile(
            configuration[fileSetting],
            fileSetting.Replace(":", "__", StringComparison.Ordinal));
        if (value is not null) overrides[targetSetting] = value;
    }

    private static string WithPostgresPassword(string connectionString, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Password = password };
        return builder.ConnectionString;
    }
}
