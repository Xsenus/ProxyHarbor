using System.Security.Cryptography;
using System.Text.Json;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class OfflineCatalogMaterializeTests
{
    private const string Key = "synthetic-offline-catalog-key-32-chars";

    [Fact]
    public async Task UnavailablePreferredCopyFallsBackWithoutDatabaseOrKeyRing()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new FakeTransport((call, path, options) =>
            {
                if (call == 1) throw Unavailable();
                File.WriteAllBytes(path, fixture.Body);
                return Result(path, fixture.Body);
            });
            var result = await fixture.RunAsync(transport);
            Assert.Equal(fixture.SecondCopyId, result.CopyId);
            Assert.Equal(fixture.Body, await File.ReadAllBytesAsync(result.Path));
            Assert.Equal(2, transport.Calls);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task CorruptPreferredCopyFallsBackAndRemovesCandidate()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new FakeTransport((call, path, options) =>
            {
                File.WriteAllBytes(path, call == 1 ? [9, 9, 9] : fixture.Body);
                return Result(path, fixture.Body);
            });
            var result = await fixture.RunAsync(transport);
            Assert.Equal(fixture.SecondCopyId, result.CopyId);
            Assert.Empty(Directory.GetFiles(fixture.Directory, "*.candidate"));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task MidBodyReadFailureFallsBackToSecondCopy()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new MidBodyThenHealthyTransport(fixture.Body);
            var result = await fixture.RunAsync(transport);

            Assert.Equal(fixture.SecondCopyId, result.CopyId);
            Assert.Equal(fixture.Body, await File.ReadAllBytesAsync(result.Path));
            Assert.Equal(2, transport.Calls);
            Assert.Empty(Directory.GetFiles(fixture.Directory, "*.candidate"));
            Assert.Empty(Directory.GetFiles(fixture.Directory, "*.partial"));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task LocalOutputFailureDoesNotGetMisclassifiedAsProviderFailover()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new FakeTransport((call, path, options) =>
                throw new IOException("synthetic local recovery disk full"));
            await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync(transport));
            Assert.Equal(1, transport.Calls);
            Assert.False(File.Exists(fixture.Output));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task AllSourcesFailWithoutPublishingOutput()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync(transport));
            Assert.False(File.Exists(fixture.Output));
            Assert.Empty(Directory.GetFiles(fixture.Directory, "*.candidate"));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task TamperedCatalogStopsBeforeProviderCall()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            fixture.Catalog[^20] ^= 1;
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.RunAsync(transport));
            Assert.Equal(0, transport.Calls);
        }
        finally { fixture.Dispose(); }
    }

    [Theory]
    [InlineData("accessKey")]
    [InlineData("secretKey")]
    [InlineData("unexpected")]
    public async Task ProviderConfigRejectsInlineOrUnknownFields(string field)
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var changed = fixture.ProviderJson.Replace(
                "\"destinations\":[", $"\"{field}\":\"inline\",\"destinations\":[",
                StringComparison.Ordinal);
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new OfflineCatalogMaterializer(transport).MaterializeAsync(
                    fixture.Catalog, Key, System.Text.Encoding.UTF8.GetBytes(changed),
                    fixture.Output, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal(0, transport.Calls);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task WrongPrefixNeverCallsProvider()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var config = fixture.ProviderJson.Replace("\"prefix\":\"safe\"",
                "\"prefix\":\"wrong\"", StringComparison.Ordinal);
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            await Assert.ThrowsAsync<IOException>(() =>
                new OfflineCatalogMaterializer(transport).MaterializeAsync(
                    fixture.Catalog, Key, System.Text.Encoding.UTF8.GetBytes(config),
                    fixture.Output, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal(0, transport.Calls);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task ExistingOutputIsNotOverwritten()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await File.WriteAllBytesAsync(fixture.Output, [7, 7]);
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync(transport));
            Assert.Equal([7, 7], await File.ReadAllBytesAsync(fixture.Output));
            Assert.Equal(0, transport.Calls);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task WrongSigningKeyStopsBeforeProviderCall()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new OfflineCatalogMaterializer(transport).MaterializeAsync(
                    fixture.Catalog, "different-synthetic-backup-key-32-chars",
                    fixture.ProviderBytes, fixture.Output, TimeSpan.FromSeconds(5),
                    CancellationToken.None));
            Assert.Equal(0, transport.Calls);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task CancelledRequestPublishesNothing()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new OfflineCatalogMaterializer(transport).MaterializeAsync(
                    fixture.Catalog, Key, fixture.ProviderBytes, fixture.Output,
                    TimeSpan.FromSeconds(5), cancelled.Token));
            Assert.False(File.Exists(fixture.Output));
            Assert.Equal(0, transport.Calls);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task DuplicateProviderDestinationIsRejected()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            using var document = JsonDocument.Parse(fixture.ProviderJson);
            var destinations = document.RootElement.GetProperty("destinations").EnumerateArray().ToArray();
            var duplicate = $"{{\"version\":1,\"destinations\":[{destinations[0].GetRawText()},{destinations[0].GetRawText()}]}}";
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new OfflineCatalogMaterializer(transport).MaterializeAsync(
                    fixture.Catalog, Key, System.Text.Encoding.UTF8.GetBytes(duplicate),
                    fixture.Output, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal(0, transport.Calls);
        }
        finally { fixture.Dispose(); }
    }

    [Theory]
    [InlineData("unsupported-version")]
    [InlineData("no-destinations")]
    [InlineData("unknown-nested-field")]
    [InlineData("missing-endpoint")]
    [InlineData("unsafe-endpoint")]
    [InlineData("malformed-json")]
    public async Task InvalidProviderConfigStopsBeforeProviderCall(string caseName)
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var invalid = caseName switch
            {
                "unsupported-version" => fixture.ProviderJson.Replace("\"version\":1",
                    "\"version\":2", StringComparison.Ordinal),
                "no-destinations" => "{\"version\":1,\"destinations\":[]}",
                "unknown-nested-field" => fixture.ProviderJson.Replace("\"region\":",
                    "\"unexpected\":\"x\",\"region\":", StringComparison.Ordinal),
                "missing-endpoint" => fixture.ProviderJson.Replace(
                    "\"endpoint\":\"https://s3.example.invalid\",", "", StringComparison.Ordinal),
                "unsafe-endpoint" => fixture.ProviderJson.Replace("https://s3.example.invalid",
                    "http://s3.example.invalid", StringComparison.Ordinal),
                _ => "{"
            };
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new OfflineCatalogMaterializer(transport).MaterializeAsync(
                    fixture.Catalog, Key, System.Text.Encoding.UTF8.GetBytes(invalid),
                    fixture.Output, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal(0, transport.Calls);
            Assert.False(File.Exists(fixture.Output));
        }
        finally { fixture.Dispose(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1801)]
    public async Task DeadlineOutsideBoundsStopsBeforeProviderCall(int seconds)
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new FakeTransport((call, path, options) => throw Unavailable());
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                new OfflineCatalogMaterializer(transport).MaterializeAsync(
                    fixture.Catalog, Key, fixture.ProviderBytes,
                    fixture.Output, TimeSpan.FromSeconds(seconds), CancellationToken.None));
            Assert.Equal(0, transport.Calls);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public void ParserRejectsInlineSecrets()
    {
        Assert.Throws<ArgumentException>(() => OfflineCatalogMaterializeOptions.Parse(
            ["--access-key", "inline"]));
        Assert.Throws<ArgumentException>(() => OfflineCatalogMaterializeOptions.Parse(
            ["--endpoint", "https://example.invalid"]));
    }

    private static BackupDestinationOperationException Unavailable() => new(
        new BackupDestinationFailure(BackupDestinationErrorCode.Unavailable,
            BackupDestinationFailureDisposition.Retryable), "fixture unavailable");

    private static BackupObjectStorageMaterializationResult Result(string path, byte[] body) =>
        new(path, body.Length, Convert.ToHexStringLower(SHA256.HashData(body)), null, null, null);

    private sealed class FakeTransport(Func<int, string, BackupOptions, BackupObjectStorageMaterializationResult> behavior)
        : IBackupObjectStorageTransport
    {
        public int Calls { get; private set; }
        public Task<string> UploadAndVerifyAsync(string path, BackupOptions options, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<BackupObjectStorageMaterializationResult> MaterializeAndVerifyAsync(
            string objectKey, string finalPath, long expectedSize, string expectedSha256,
            BackupOptions options, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(behavior(Calls, finalPath, options));
        }
    }

    private sealed class MidBodyThenHealthyTransport(byte[] body) : IBackupObjectStorageTransport
    {
        public int Calls { get; private set; }

        public Task<string> UploadAndVerifyAsync(string path, BackupOptions options, CancellationToken token) =>
            throw new NotSupportedException();

        public async Task<BackupObjectStorageMaterializationResult> MaterializeAndVerifyAsync(
            string objectKey, string finalPath, long expectedSize, string expectedSha256,
            BackupOptions options, CancellationToken token)
        {
            Calls++;
            await using Stream source = Calls == 1
                ? new FailingReadStream(body)
                : new MemoryStream(body, writable: false);
            return await S3BackupObjectStorageTransport.CopyVerifyAndPublishAsync(
                source, finalPath + ".inner.partial", finalPath, expectedSize, expectedSha256, token);
        }
    }

    private sealed class FailingReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private int readCount;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref readCount) > 1)
                throw new IOException("synthetic provider stream lost");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public required string Directory { get; init; }
        public required string Output { get; init; }
        public required byte[] Catalog { get; init; }
        public required byte[] ProviderBytes { get; init; }
        public required string ProviderJson { get; init; }
        public required byte[] Body { get; init; }
        public required Guid SecondCopyId { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"offline-catalog-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var access = Path.Combine(directory, "access.secret");
            var secret = Path.Combine(directory, "secret.secret");
            await File.WriteAllTextAsync(access, "TESTACCESS");
            await File.WriteAllTextAsync(secret, "TESTSECRET123");
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var secondCopy = Guid.NewGuid();
            var body = new byte[] { 1, 2, 3, 4, 5 };
            var finished = DateTimeOffset.UtcNow.AddMinutes(-3);
            var snapshot = new BackupCatalogSnapshot(Guid.NewGuid(), "snapshot.phbackup",
                body.Length, Convert.ToHexStringLower(SHA256.HashData(body)), 1,
                finished, DateTimeOffset.UtcNow, "legacy",
                [new(Guid.NewGuid(), first, "s3", "safe/snapshot.phbackup", 1, finished.AddMinutes(1)),
                 new(secondCopy, second, "s3", "safe/snapshot.phbackup", 2, finished.AddMinutes(2))]);
            var providerJson = JsonSerializer.Serialize(new
            {
                version = 1,
                destinations = new[] { Provider(first, access, secret), Provider(second, access, secret) }
            });
            return new Fixture
            {
                Directory = directory,
                Output = Path.Combine(directory, "result.phbackup"),
                Catalog = BackupCatalogService.Seal(snapshot, Key),
                ProviderBytes = System.Text.Encoding.UTF8.GetBytes(providerJson),
                ProviderJson = providerJson,
                Body = body,
                SecondCopyId = secondCopy
            };
        }

        private static object Provider(Guid id, string access, string secret) => new
        {
            destinationId = id,
            endpoint = "https://s3.example.invalid",
            region = "test-region",
            bucket = "private-bucket",
            prefix = "safe",
            usePathStyle = true,
            accessKeyFile = access,
            secretKeyFile = secret
        };

        public Task<(string Path, Guid CopyId, string Sha256)> RunAsync(IBackupObjectStorageTransport transport) =>
            new OfflineCatalogMaterializer(transport).MaterializeAsync(
                Catalog, Key, ProviderBytes, Output, TimeSpan.FromSeconds(5), CancellationToken.None);

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
