using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует fail-closed startup-контракт новых зашифрованных backup.</summary>
public sealed class BackupOptionsValidationTests
{
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

    private static ServiceProvider BuildProvider(string key, string directory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=proxyharbor;Username=proxyharbor",
            ["Backup:Enabled"] = "true",
            ["Backup:EncryptionKey"] = key,
            ["Backup:Directory"] = directory
        }).Build();
        var services = new ServiceCollection();
        services.AddProxyHarborInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
