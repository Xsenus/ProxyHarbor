using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует fail-closed startup-контракт новых зашифрованных backup.</summary>
public sealed class BackupOptionsValidationTests
{
    private const string ValidTelegramToken = "9000000000000000000:CI_ONLY_PLACEHOLDER_NOT_A_REAL_TOKEN";

    [Fact]
    public void EnabledBackupRejectsKeyShorterThanThirtyTwoCharacters()
    {
        using var provider = BuildProvider(new string('k', BackupOptions.MinimumEncryptionKeyLength - 1),
            Path.GetFullPath("backup-options-test"));

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("32", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk\n")]
    public void EnabledBackupRejectsControlCharacterKey(string key)
    {
        using var provider = BuildProvider(key, Path.GetFullPath("backup-options-test"));

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupOptions>>().Value);
    }

    [Fact]
    public void EnabledBackupRejectsOversizedKey()
    {
        using var provider = BuildProvider(new string('k', BackupOptions.MaximumEncryptionKeyLength + 1),
            Path.GetFullPath("backup-options-test"));

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupOptions>>().Value);
    }

    [Fact]
    public void EnabledBackupRejectsUnpairedUnicodeSurrogateKey()
    {
        var key = new string('k', BackupOptions.MinimumEncryptionKeyLength - 1) + '\uD800';
        using var provider = BuildProvider(key, Path.GetFullPath("backup-options-test"));

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupOptions>>().Value);
    }

    [Fact]
    public void EnabledBackupAcceptsPairedUnicodeSurrogateKey()
    {
        var key = new string('k', BackupOptions.MinimumEncryptionKeyLength - 2) + "\U0001F680";
        using var provider = BuildProvider(key, Path.GetFullPath("backup-options-test"));

        Assert.Equal(key, provider.GetRequiredService<IOptions<BackupOptions>>().Value.EncryptionKey);
    }

    [Theory]
    [InlineData("backups")]
    [InlineData(" ")]
    [InlineData("unsafe\npath")]
    public void EnabledBackupRejectsRelativeEmptyOrControlCharacterDirectory(string directory)
    {
        using var provider = BuildProvider(new string('k', BackupOptions.MinimumEncryptionKeyLength), directory);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupOptions>>().Value);

        Assert.Contains(exception.Failures,
            failure => failure.Contains("абсолютным безопасным путём", StringComparison.Ordinal));
    }

    [Fact]
    public void EnabledBackupAcceptsStrongKeyAndPlatformAbsoluteDirectory()
    {
        var directory = Path.GetFullPath("backup-options-test");
        using var provider = BuildProvider(new string('k', BackupOptions.MinimumEncryptionKeyLength), directory);

        var options = provider.GetRequiredService<IOptions<BackupOptions>>().Value;

        Assert.Equal(directory, options.Directory);
        Assert.Equal(BackupOptions.MinimumEncryptionKeyLength, options.EncryptionKey!.Length);
    }

    [Fact]
    public void EnabledBackupRejectsFileSystemRootDirectory()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("backup-options-test"))!;
        using var provider = BuildProvider(new string('k', BackupOptions.MinimumEncryptionKeyLength), root);

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupOptions>>().Value);
    }

    [Theory]
    [InlineData("invalid-short-token")]
    [InlineData("123456789:too-short")]
    [InlineData("123456789:ABCDEFGHIJKLMNOPQRST/unsafe")]
    [InlineData("123456789:ABCDEFGHIJKLMNOPQRST?unsafe")]
    [InlineData("123456789:ABCDEFGHIJKLMNOPQRST\nunsafe")]
    [InlineData("123456789:ABCDEFGHIJKLMNOPQRST\\unsafe")]
    [InlineData("123456789:ABCDEFGHIJKLMNOPQRST%2Funsafe")]
    public void RejectsMalformedTelegramBotToken(string token)
    {
        using var provider = BuildProvider(
            new string('k', BackupOptions.MinimumEncryptionKeyLength),
            Path.GetFullPath("backup-options-test"),
            token,
            "123456");

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupOptions>>().Value);

        Assert.Contains(exception.Failures,
            failure => failure.Contains("TelegramBotToken", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("+123456")]
    [InlineData("123 456")]
    [InlineData("@channel")]
    [InlineData("9223372036854775808")]
    public void RejectsMalformedTelegramChatId(string chatId)
    {
        using var provider = BuildProvider(
            new string('k', BackupOptions.MinimumEncryptionKeyLength),
            Path.GetFullPath("backup-options-test"),
            ValidTelegramToken,
            chatId);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupOptions>>().Value);

        Assert.Contains(exception.Failures,
            failure => failure.Contains("TelegramChatId", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsHalfConfiguredTelegramDelivery()
    {
        using var provider = BuildProvider(
            new string('k', BackupOptions.MinimumEncryptionKeyLength),
            Path.GetFullPath("backup-options-test"),
            ValidTelegramToken,
            null);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupOptions>>().Value);

        Assert.Contains(exception.Failures,
            failure => failure.Contains("задаются только вместе", StringComparison.Ordinal));
    }

    [Fact]
    public void EnabledBackupAllowsTelegramDeliveryStoredInRuntimeConfiguration()
    {
        using var provider = BuildProvider(
            new string('k', BackupOptions.MinimumEncryptionKeyLength),
            Path.GetFullPath("backup-options-test"),
            null,
            null);

        var options = provider.GetRequiredService<IOptions<BackupOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Null(options.TelegramBotToken);
        Assert.Null(options.TelegramChatId);
    }

    [Fact]
    public void DisabledBackupAcceptsMissingTelegramDelivery()
    {
        using var provider = BuildProvider(
            new string('k', BackupOptions.MinimumEncryptionKeyLength),
            Path.GetFullPath("backup-options-test"),
            null,
            null,
            enabled: false);

        var options = provider.GetRequiredService<IOptions<BackupOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Null(options.TelegramBotToken);
        Assert.Null(options.TelegramChatId);
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("-1001234567890")]
    public void AcceptsValidTelegramDeliveryCoordinates(string chatId)
    {
        using var provider = BuildProvider(
            new string('k', BackupOptions.MinimumEncryptionKeyLength),
            Path.GetFullPath("backup-options-test"),
            ValidTelegramToken,
            chatId);

        var options = provider.GetRequiredService<IOptions<BackupOptions>>().Value;

        Assert.Equal(ValidTelegramToken, options.TelegramBotToken);
        Assert.Equal(chatId, options.TelegramChatId);
    }

    private static ServiceProvider BuildProvider(
        string key,
        string directory,
        string? telegramBotToken = ValidTelegramToken,
        string? telegramChatId = "123456",
        bool enabled = true)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=proxyharbor;Username=proxyharbor",
            ["Backup:Enabled"] = enabled ? "true" : "false",
            ["Backup:EncryptionKey"] = key,
            ["Backup:Directory"] = directory,
            ["Backup:TelegramBotToken"] = telegramBotToken,
            ["Backup:TelegramChatId"] = telegramChatId
        }).Build();
        var services = new ServiceCollection();
        services.AddProxyHarborInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
