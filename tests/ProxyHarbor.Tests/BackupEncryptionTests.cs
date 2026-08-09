using System.Security.Cryptography;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Доказывает восстановимость и криптографическую целостность форматов backup.</summary>
public sealed class BackupEncryptionTests
{
    private const string Password = "integration-backup-key-32-characters";

    [Fact]
    public async Task Phb3RoundTripPreservesMultiChunkFile()
    {
        using var files = new TemporaryFiles();
        var expected = RandomNumberGenerator.GetBytes(2_300_000);
        await File.WriteAllBytesAsync(files.Source, expected);

        await BackupEncryption.EncryptAsync(files.Source, files.Encrypted, Password, CancellationToken.None);
        await BackupEncryption.DecryptAsync(files.Encrypted, files.Decrypted, Password, CancellationToken.None);

        Assert.Equal(expected, await File.ReadAllBytesAsync(files.Decrypted));
        Assert.Equal("PHB3", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(files.Encrypted))[..4]));
    }

    [Fact]
    public async Task Phb3RejectsModifiedCiphertextAndDeletesPartialOutput()
    {
        using var files = new TemporaryFiles();
        await File.WriteAllBytesAsync(files.Source, RandomNumberGenerator.GetBytes(512));
        await BackupEncryption.EncryptAsync(files.Source, files.Encrypted, Password, CancellationToken.None);
        var encrypted = await File.ReadAllBytesAsync(files.Encrypted);
        encrypted[64] ^= 0x40;
        await File.WriteAllBytesAsync(files.Encrypted, encrypted);

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
            BackupEncryption.DecryptAsync(files.Encrypted, files.Decrypted, Password, CancellationToken.None));
        Assert.False(File.Exists(files.Decrypted));
    }

    [Fact]
    public async Task Phb3RejectsTruncatedFinalMarker()
    {
        using var files = new TemporaryFiles();
        await File.WriteAllBytesAsync(files.Source, RandomNumberGenerator.GetBytes(128));
        await BackupEncryption.EncryptAsync(files.Source, files.Encrypted, Password, CancellationToken.None);
        var encrypted = await File.ReadAllBytesAsync(files.Encrypted);
        await File.WriteAllBytesAsync(files.Encrypted, encrypted[..^1]);

        await Assert.ThrowsAnyAsync<EndOfStreamException>(() =>
            BackupEncryption.DecryptAsync(files.Encrypted, files.Decrypted, Password, CancellationToken.None));
        Assert.False(File.Exists(files.Decrypted));
    }

    [Fact]
    public async Task EncryptionCancellationDeletesPartialCiphertext()
    {
        using var files = new TemporaryFiles();
        await File.WriteAllBytesAsync(files.Source, RandomNumberGenerator.GetBytes(128));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BackupEncryption.EncryptAsync(files.Source, files.Encrypted, Password, cancellation.Token));

        Assert.False(File.Exists(files.Encrypted));
    }

    [Fact]
    public async Task Phb3EncryptsANonSeekableStreamWithoutAPlaintextFile()
    {
        using var files = new TemporaryFiles();
        var expected = RandomNumberGenerator.GetBytes(1_300_000);
        await using var input = new NonSeekableReadStream(expected);

        await BackupEncryption.EncryptAsync(input, files.Encrypted, Password, CancellationToken.None);
        await BackupEncryption.DecryptAsync(files.Encrypted, files.Decrypted, Password, CancellationToken.None);

        Assert.False(File.Exists(files.Source));
        Assert.Equal(expected, await File.ReadAllBytesAsync(files.Decrypted));
    }

    [Fact]
    public async Task LegacyPhb2RemainsDecryptable()
    {
        using var files = new TemporaryFiles();
        var expected = RandomNumberGenerator.GetBytes(777);
        await WriteLegacyPhb2Async(files.Encrypted, expected);

        await BackupEncryption.DecryptAsync(files.Encrypted, files.Decrypted, Password, CancellationToken.None);

        Assert.Equal(expected, await File.ReadAllBytesAsync(files.Decrypted));
    }

    private static async Task WriteLegacyPhb2Async(string path, byte[] plaintext)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Password, salt, 200_000, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[plaintext.Length];
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            await using var output = File.Create(path);
            await output.WriteAsync("PHB2"u8.ToArray());
            await output.WriteAsync(salt);
            await output.WriteAsync(BitConverter.GetBytes(1024 * 1024));
            await output.WriteAsync(BitConverter.GetBytes(plaintext.Length));
            await output.WriteAsync(nonce);
            await output.WriteAsync(tag);
            await output.WriteAsync(ciphertext);
            await output.WriteAsync(BitConverter.GetBytes(0));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"proxyharbor-crypto-{Guid.NewGuid():N}");
        public TemporaryFiles() => Directory.CreateDirectory(_directory);
        public string Source => Path.Combine(_directory, "source.zip");
        public string Encrypted => Path.Combine(_directory, "backup.phbackup");
        public string Decrypted => Path.Combine(_directory, "restored.zip");
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
