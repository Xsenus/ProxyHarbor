using Microsoft.AspNetCore.DataProtection;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class BackupMaterializeOptionsTests
{
    [Fact]
    public void ValidateAcceptsReadOnlyRequestWithIsolatedKeyRing()
    {
        var directory = NewDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "key-synthetic.xml"), "synthetic");
            var options = new BackupMaterializeOptions(
                Guid.NewGuid(), Path.Combine(directory, "retrieved.phbackup"),
                "Host=localhost;Database=test", directory, 120, ShowHelp: false);

            options.Validate();

            Assert.False(File.Exists(options.OutputPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ValidateRejectsMissingKeyRingAndExistingOutput()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "retrieved.phbackup");
            var options = new BackupMaterializeOptions(
                Guid.NewGuid(), path, "Host=localhost;Database=test", directory, 120, false);
            Assert.Throws<ArgumentException>(options.Validate);

            File.WriteAllText(Path.Combine(directory, "key-synthetic.xml"), "synthetic");
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Throws<ArgumentException>(options.Validate);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("--connection", "Host=evil")]
    [InlineData("--encryption-key", "inline-secret")]
    [InlineData("--input", "backup.phbackup")]
    [InlineData("--replace-existing-data", "yes")]
    public void ParserRejectsUnrelatedOrInlineSecretOptions(string option, string value)
    {
        Assert.Throws<ArgumentException>(() => BackupMaterializeOptions.Parse([option, value]));
    }

    [Fact]
    public void ParserRejectsDuplicateOrOutOfBoundsDeadline()
    {
        Assert.Throws<ArgumentException>(() => BackupMaterializeOptions.Parse([
            "--deadline-seconds", "5", "--deadline-seconds", "6"]));
        var options = BackupMaterializeOptions.Parse(["--deadline-seconds", "1801"]);
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public async Task PreCancelledMaterializationReturnsInterruptedWithoutDatabaseOrProviderIo()
    {
        var directory = NewDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "key-synthetic.xml"), "synthetic");
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            var options = new BackupMaterializeOptions(
                Guid.NewGuid(), Path.Combine(directory, "retrieved.phbackup"),
                "Host=localhost;Database=test", directory, 120, false);
            var exitCode = await BackupMaterializeApplication.RunAsync(options, cancellation.Token);

            Assert.Equal(130, exitCode);
            Assert.Empty(Directory.GetFiles(directory, "*.phbackup"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void IsolatedReadOnlyKeyRingCanDecryptWithoutCreatingAnotherKey()
    {
        var directory = NewDirectory();
        try
        {
            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(directory), builder => builder.SetApplicationName("ProxyHarbor"));
            var ciphertext = provider.CreateProtector("ProxyHarbor.BackupDestination.Secrets.v1")
                .Protect("synthetic-provider-secret");
            var before = Directory.GetFiles(directory, "key-*.xml");
            Assert.NotEmpty(before);

            var readOnly = DataProtectionProvider.Create(
                new DirectoryInfo(directory), builder => builder.SetApplicationName("ProxyHarbor")
                    .DisableAutomaticKeyGeneration());
            Assert.Equal("synthetic-provider-secret",
                readOnly.CreateProtector("ProxyHarbor.BackupDestination.Secrets.v1")
                    .Unprotect(ciphertext));
            Assert.Equal(before, Directory.GetFiles(directory, "key-*.xml"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"materialize-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
