using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>
/// Проверяет ограничения ключа restore без разрыва совместимости с legacy backup.
/// </summary>
public sealed class RestoreOptionsTests
{
    private const string LegacyKey = "legacy-key-16chr";
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

    private sealed class TemporaryInput : IDisposable
    {
        public TemporaryInput() => Path = System.IO.Path.GetTempFileName();

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
