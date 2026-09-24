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
    public void InspectSettingsDoesNotRequireDatabaseOrDestructiveConfirmation()
    {
        using var input = new TemporaryInput();
        var options = new RestoreOptions(
            input.Path,
            ConnectionString: null,
            LegacyKey,
            ConfirmReplace: false,
            ShowHelp: false,
            InspectSettings: true);

        options.Validate();
    }

    [Fact]
    public void InspectSettingsRejectsDestructiveConfirmationFlag()
    {
        using var input = new TemporaryInput();
        var options = new RestoreOptions(
            input.Path,
            Connection,
            LegacyKey,
            ConfirmReplace: true,
            ShowHelp: false,
            InspectSettings: true);

        var exception = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Contains("нельзя объединять", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRecognizesReadOnlySettingsInspection()
    {
        using var input = new TemporaryInput();

        var options = RestoreOptions.Parse([
            "--input", input.Path,
            "--encryption-key", LegacyKey,
            "--inspect-settings"]);

        Assert.True(options.InspectSettings);
        Assert.Null(options.ConnectionString);
        options.Validate();
    }

    [Fact]
    public void IsolatedTargetGuardAcceptsExactHostPortAndDatabase()
    {
        using var input = new TemporaryInput();
        var options = RestoreOptions.Parse([
            "--input", input.Path,
            "--connection", "Host=drill-db.example;Port=55441;Database=proxyharbor_drill;Username=restore",
            "--encryption-key", LegacyKey,
            "--replace-existing-data",
            "--expected-target-host", "DRILL-DB.EXAMPLE",
            "--expected-target-port", "55441",
            "--expected-target-database", "proxyharbor_drill"]);

        options.Validate();
    }

    [Theory]
    [InlineData("other.example", 55441, "proxyharbor_drill")]
    [InlineData("drill-db.example", 5432, "proxyharbor_drill")]
    [InlineData("drill-db.example", 55441, "production")]
    [InlineData("drill-db.example,production.example", 55441, "proxyharbor_drill")]
    public void IsolatedTargetGuardRejectsMismatchOrMultipleHosts(
        string expectedHost, int expectedPort, string expectedDatabase)
    {
        using var input = new TemporaryInput();
        var options = new RestoreOptions(input.Path,
            "Host=drill-db.example;Port=55441;Database=proxyharbor_drill;Username=restore",
            LegacyKey, ConfirmReplace: true, ShowHelp: false,
            ExpectedTargetHost: expectedHost, ExpectedTargetPort: expectedPort,
            ExpectedTargetDatabase: expectedDatabase);

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void IsolatedTargetGuardRejectsPartialOrInvalidPort()
    {
        using var input = new TemporaryInput();
        var partial = new RestoreOptions(input.Path, Connection, LegacyKey,
            ConfirmReplace: true, ShowHelp: false, ExpectedTargetHost: "localhost");
        Assert.Throws<ArgumentException>(partial.Validate);
        Assert.Throws<ArgumentException>(() => RestoreOptions.Parse([
            "--input", input.Path, "--connection", Connection,
            "--encryption-key", LegacyKey, "--replace-existing-data",
            "--expected-target-port", "not-a-port"]));
        var outOfRange = new RestoreOptions(input.Path, Connection, LegacyKey,
            ConfirmReplace: true, ShowHelp: false, ExpectedTargetHost: "localhost",
            ExpectedTargetPort: 65536, ExpectedTargetDatabase: "proxyharbor");
        Assert.Throws<ArgumentException>(outOfRange.Validate);
        var multiHost = new RestoreOptions(input.Path,
            "Host=localhost,production.example;Database=proxyharbor;Username=restore",
            LegacyKey, ConfirmReplace: true, ShowHelp: false,
            ExpectedTargetHost: "localhost", ExpectedTargetPort: 5432,
            ExpectedTargetDatabase: "proxyharbor");
        Assert.Throws<ArgumentException>(multiHost.Validate);
    }

    [Fact]
    public void IsolatedTargetGuardCannotBeUsedWithSettingsInspection()
    {
        using var input = new TemporaryInput();
        var options = new RestoreOptions(input.Path, null, LegacyKey,
            ConfirmReplace: false, ShowHelp: false, InspectSettings: true,
            ExpectedTargetHost: "localhost", ExpectedTargetPort: 5432,
            ExpectedTargetDatabase: "proxyharbor");
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public async Task IsolatedTargetMismatchStopsBeforeDecryptionOrDatabaseAccess()
    {
        using var input = new TemporaryInput("not an encrypted archive");
        var temporaryCreated = false;
        var result = await RestoreApplication.RunAsync([
            "--input", input.Path,
            "--connection", Connection,
            "--encryption-key", LegacyKey,
            "--replace-existing-data",
            "--expected-target-host", "isolated.example",
            "--expected-target-port", "5432",
            "--expected-target-database", "proxyharbor"],
            new RestoreExecutionHooks(TemporaryDirectoryCreated: _ => temporaryCreated = true),
            CancellationToken.None);

        Assert.Equal(1, result);
        Assert.False(temporaryCreated);
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
