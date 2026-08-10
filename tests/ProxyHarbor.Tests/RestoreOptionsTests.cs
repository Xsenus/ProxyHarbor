using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>
/// Проверяет ограничения ключа restore без разрыва совместимости с legacy backup.
/// </summary>
public sealed class RestoreOptionsTests
{
    private const string LegacyKey = "legacy-key-16chr";
    private const string StrongKey = "restore-cancellation-key-32-chars";
    private const string Connection = "Host=localhost;Database=proxyharbor;Username=proxyharbor";

    [Fact]
    public void ValidateAcceptsLegacySixteenCharacterDecryptionKey()
    {
        using var input = new TemporaryInput();
        var options = new RestoreOptions(input.Path, Connection, LegacyKey, ConfirmReplace: true, ShowHelp: false);

        options.Validate();
    }

    [Theory]
    [InlineData("short-key")]
    [InlineData("legacy-key-16chr\n")]
    public void ValidateRejectsUnsafeDecryptionKey(string key)
    {
        using var input = new TemporaryInput();
        var options = new RestoreOptions(input.Path, Connection, key, ConfirmReplace: true, ShowHelp: false);

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsOversizedDecryptionKey()
    {
        using var input = new TemporaryInput();
        var options = new RestoreOptions(
            input.Path,
            Connection,
            new string('k', BackupOptions.MaximumEncryptionKeyLength + 1),
            ConfirmReplace: true,
            ShowHelp: false);

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsUnpairedUnicodeSurrogateKey()
    {
        using var input = new TemporaryInput();
        var options = new RestoreOptions(
            input.Path,
            Connection,
            new string('k', BackupOptions.MinimumLegacyDecryptionKeyLength - 1) + '\uDFFF',
            ConfirmReplace: true,
            ShowHelp: false);

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void ParseReadsBoundedAbsoluteEncryptionKeyFile()
    {
        using var input = new TemporaryInput();
        using var keyFile = new TemporaryInput(LegacyKey);

        var options = RestoreOptions.Parse([
            "--input", input.Path,
            "--connection", Connection,
            "--encryption-key-file", keyFile.Path,
            "--replace-existing-data"]);

        Assert.Equal(LegacyKey, options.EncryptionKey);
        options.Validate();
    }

    [Fact]
    public void ParseRejectsAmbiguousInlineAndFileKeys()
    {
        using var input = new TemporaryInput();
        using var keyFile = new TemporaryInput(LegacyKey);

        Assert.Throws<ArgumentException>(() => RestoreOptions.Parse([
            "--input", input.Path,
            "--connection", Connection,
            "--encryption-key", LegacyKey,
            "--encryption-key-file", keyFile.Path,
            "--replace-existing-data"]));
    }

    [Fact]
    public void ParseRejectsExplicitEmptyEncryptionKeyFile()
    {
        using var input = new TemporaryInput();
        using var keyFile = new TemporaryInput();

        var exception = Assert.Throws<ArgumentException>(() => RestoreOptions.Parse([
            "--input", input.Path,
            "--connection", Connection,
            "--encryption-key-file", keyFile.Path,
            "--replace-existing-data"]));

        Assert.Contains("не содержит ключ", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreCancelledRestoreReturnsStandardInterruptedExitCode()
    {
        using var plaintext = new TemporaryInput("representative plaintext");
        var encryptedPath = Path.Combine(Path.GetTempPath(), $"proxyharbor-cancel-{Guid.NewGuid():N}.phbackup");
        try
        {
            await BackupEncryption.EncryptAsync(
                plaintext.Path, encryptedPath, StrongKey, CancellationToken.None);
            using var stopping = new CancellationTokenSource();
            await stopping.CancelAsync();

            var exitCode = await RestoreApplication.RunAsync([
                "--input", encryptedPath,
                "--connection", Connection,
                "--encryption-key", StrongKey,
                "--replace-existing-data"], stopping.Token);

            Assert.Equal(130, exitCode);
        }
        finally
        {
            if (File.Exists(encryptedPath)) File.Delete(encryptedPath);
        }
    }

    private sealed class TemporaryInput : IDisposable
    {
        public TemporaryInput(string content = "")
        {
            Path = System.IO.Path.GetTempFileName();
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
