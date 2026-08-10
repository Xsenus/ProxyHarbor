using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ProxyHarbor.Infrastructure;

/// <summary>Потоковое шифрование backup-файлов с обратной совместимостью формата PHB2.</summary>
public static class BackupEncryption
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DefaultChunkSize = 1024 * 1024;
    private const int Iterations = 200_000;
    private static ReadOnlySpan<byte> CurrentMagic => "PHB3"u8;

    /// <summary>
    /// Создаёт PHB3. Каждый блок, его позиция и завершающий маркер аутентифицированы,
    /// поэтому удаление, перестановка или незаметное усечение блоков обнаруживаются.
    /// </summary>
    public static async Task EncryptAsync(string source, string destination, string password, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await EncryptAsync(input, destination, password, token);
    }

    /// <summary>Шифрует последовательный поток без требования plaintext-файла или seek.</summary>
    public static async Task EncryptAsync(Stream input, string destination, string password, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new ArgumentException("Исходный поток должен поддерживать чтение.", nameof(input));
        ValidateEncryptionPassword(password);
        if (File.Exists(destination)) throw new IOException("Файл назначения уже существует.");
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        // Один bounded набор буферов обслуживает весь поток. Иначе каждый блок создавал
        // новые мегабайтные LOH-объекты и вызывал дорогие full GC на больших снимках.
        var plaintext = new byte[DefaultChunkSize];
        var ciphertext = new byte[DefaultChunkSize];
        // Размер блока уже ограничен заголовком, поэтому два массива выделяются один раз,
        // а не на каждой итерации; секретный буфер гарантированно очищается в finally.
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var associatedData = new byte[4 + SaltSize + sizeof(int) + sizeof(long) + sizeof(int)];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await output.WriteAsync(CurrentMagic.ToArray(), token);
            await output.WriteAsync(salt, token);
            await WriteInt32Async(output, DefaultChunkSize, token);

            long index = 0;
            while (true)
            {
                var length = await input.ReadAtLeastAsync(plaintext, 1, throwOnEndOfStream: false, token);
                if (length == 0) break;
                RandomNumberGenerator.Fill(nonce);
                FillAssociatedData(associatedData, salt, DefaultChunkSize, index, length);
                aes.Encrypt(nonce, plaintext.AsSpan(0, length), ciphertext.AsSpan(0, length), tag, associatedData);
                await WriteInt32Async(output, length, token);
                await output.WriteAsync(nonce, token);
                await output.WriteAsync(tag, token);
                await output.WriteAsync(ciphertext.AsMemory(0, length), token);
                index++;
            }

            // Пустой аутентифицированный блок фиксирует точное число предыдущих блоков.
            RandomNumberGenerator.Fill(nonce);
            FillAssociatedData(associatedData, salt, DefaultChunkSize, index, 0);
            aes.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag, associatedData);
            await WriteInt32Async(output, 0, token);
            await output.WriteAsync(nonce, token);
            await output.WriteAsync(tag, token);
        }
        catch
        {
            // Частичный ciphertext никогда не должен выглядеть как готовая резервная копия.
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Расшифровывает PHB3 и существующие PHB2; частичный результат при ошибке удаляется.</summary>
    public static async Task DecryptAsync(string source, string destination, string password, CancellationToken token)
    {
        ValidateDecryptionPassword(password);
        if (File.Exists(destination)) throw new IOException("Файл назначения уже существует.");
        try
        {
            await DecryptCoreAsync(source, destination, password, token);
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    /// <summary>
    /// Перечитывает и аутентифицирует каждый блок backup без записи plaintext.
    /// Используется до публикации файла и его отправки за пределы сервиса.
    /// </summary>
    public static Task VerifyAsync(string source, string password, CancellationToken token)
    {
        ValidateDecryptionPassword(password);
        return DecryptCoreAsync(source, destination: null, password, token);
    }

    private static async Task DecryptCoreAsync(
        string source,
        string? destination,
        string password,
        CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var magic = new byte[4];
        await input.ReadExactlyAsync(magic, token);
        var isPhb3 = magic.AsSpan().SequenceEqual(CurrentMagic);
        var isPhb2 = magic.AsSpan().SequenceEqual("PHB2"u8);
        if (!isPhb2 && !isPhb3) throw new InvalidDataException("Неизвестный формат backup ProxyHarbor.");

        var salt = new byte[SaltSize];
        await input.ReadExactlyAsync(salt, token);
        var chunkSize = await ReadInt32Async(input, token);
        if (chunkSize is < 65_536 or > 16_777_216)
            throw new InvalidDataException("Недопустимый размер блока backup.");

        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        // На restore использованная часть plaintext очищается после каждого блока,
        // а весь bounded буфер — перед выходом из метода при любом результате.
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[chunkSize];
        var plaintext = new byte[chunkSize];
        var associatedData = isPhb3
            ? new byte[4 + SaltSize + sizeof(int) + sizeof(long) + sizeof(int)]
            : null;
        try
        {
            using var aes = new AesGcm(key, TagSize);
            await using var output = destination is null
                ? null
                : new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long index = 0;
            while (true)
            {
                var length = await ReadInt32Async(input, token);
                if (length is < 0 || length > chunkSize)
                    throw new InvalidDataException("Повреждён размер блока backup.");
                if (length == 0 && isPhb2) break;

                await input.ReadExactlyAsync(nonce, token);
                await input.ReadExactlyAsync(tag, token);
                if (length > 0) await input.ReadExactlyAsync(ciphertext.AsMemory(0, length), token);
                try
                {
                    if (isPhb3)
                    {
                        FillAssociatedData(associatedData!, salt, chunkSize, index, length);
                        aes.Decrypt(
                            nonce,
                            ciphertext.AsSpan(0, length),
                            tag,
                            plaintext.AsSpan(0, length),
                            associatedData);
                    }
                    else
                    {
                        aes.Decrypt(
                            nonce,
                            ciphertext.AsSpan(0, length),
                            tag,
                            plaintext.AsSpan(0, length));
                    }
                    if (length > 0 && output is not null)
                        await output.WriteAsync(plaintext.AsMemory(0, length), token);
                }
                finally { CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, length)); }

                if (length == 0) break;
                index++;
            }

            if (input.ReadByte() != -1)
                throw new InvalidDataException("После завершающего блока backup обнаружены лишние данные.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void FillAssociatedData(Span<byte> data, byte[] salt, int chunkSize, long index, int length)
    {
        CurrentMagic.CopyTo(data);
        salt.AsSpan().CopyTo(data[4..]);
        BinaryPrimitives.WriteInt32LittleEndian(data[20..], chunkSize);
        BinaryPrimitives.WriteInt64LittleEndian(data[24..], index);
        BinaryPrimitives.WriteInt32LittleEndian(data[32..], length);
    }

    private static async Task WriteInt32Async(Stream stream, int value, CancellationToken token)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, token);
    }

    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken token)
    {
        var bytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(bytes, token);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static void ValidateEncryptionPassword(string password)
    {
        if (!BackupOptions.IsNewEncryptionKeyValid(password))
            throw new ArgumentException(
                $"Ключ шифрования нового backup должен содержать {BackupOptions.MinimumEncryptionKeyLength}..{BackupOptions.MaximumEncryptionKeyLength} символов без управляющих знаков.",
                nameof(password));
    }

    private static void ValidateDecryptionPassword(string password)
    {
        if (!BackupOptions.IsLegacyDecryptionKeyValid(password))
            throw new ArgumentException(
                $"Ключ расшифрования должен содержать {BackupOptions.MinimumLegacyDecryptionKeyLength}..{BackupOptions.MaximumEncryptionKeyLength} символов без управляющих знаков.",
                nameof(password));
    }
}
