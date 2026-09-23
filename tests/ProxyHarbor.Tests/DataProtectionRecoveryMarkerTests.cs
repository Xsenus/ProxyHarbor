using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace ProxyHarbor.Tests;

public sealed class DataProtectionRecoveryMarkerTests
{
    [Fact]
    public async Task MarkerSurvivesKeyRingCopyWithoutDatabaseOrNewKeys()
    {
        using var fixture = new Fixture();
        var source = fixture.CreateRing("source");
        var marker = Path.Combine(fixture.Root, "recovery.marker.json");
        var before = Directory.GetFiles(source, "key-*.xml").Length;

        await DataProtectionRecoveryMarkerApplication.CreateAsync(source, marker, CancellationToken.None);
        Assert.Equal(before, Directory.GetFiles(source, "key-*.xml").Length);
        Assert.True(File.Exists(marker));
        Assert.DoesNotContain("fixture plaintext", await File.ReadAllTextAsync(marker),
            StringComparison.Ordinal);

        var recovered = fixture.CopyRing(source, "recovered");
        await DataProtectionRecoveryMarkerApplication.VerifyAsync(
            recovered, marker, CancellationToken.None);
        Assert.Equal(before, Directory.GetFiles(recovered, "key-*.xml").Length);
    }

    [Fact]
    public async Task WrongOrTamperedRingCannotVerifyMarker()
    {
        using var fixture = new Fixture();
        var source = fixture.CreateRing("source");
        var other = fixture.CreateRing("other");
        var marker = Path.Combine(fixture.Root, "recovery.marker.json");
        await DataProtectionRecoveryMarkerApplication.CreateAsync(source, marker, CancellationToken.None);

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            DataProtectionRecoveryMarkerApplication.VerifyAsync(other, marker, CancellationToken.None));
        var original = await File.ReadAllTextAsync(marker);
        await File.WriteAllTextAsync(marker,
            original.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            DataProtectionRecoveryMarkerApplication.VerifyAsync(source, marker, CancellationToken.None));
    }

    [Fact]
    public async Task ExistingMarkerIsNeverOverwrittenAndEmptyRingIsRejected()
    {
        using var fixture = new Fixture();
        var source = fixture.CreateRing("source");
        var marker = Path.Combine(fixture.Root, "recovery.marker.json");
        await File.WriteAllTextAsync(marker, "sentinel");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            DataProtectionRecoveryMarkerApplication.CreateAsync(source, marker, CancellationToken.None));
        Assert.Equal("sentinel", await File.ReadAllTextAsync(marker));

        var empty = Path.Combine(fixture.Root, "empty-ring");
        Directory.CreateDirectory(empty);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            DataProtectionRecoveryMarkerApplication.VerifyAsync(empty, marker, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(empty));
    }

    [Fact]
    public async Task UnknownMarkerFieldFailsClosed()
    {
        using var fixture = new Fixture();
        var source = fixture.CreateRing("source");
        var marker = Path.Combine(fixture.Root, "recovery.marker.json");
        await DataProtectionRecoveryMarkerApplication.CreateAsync(source, marker, CancellationToken.None);
        var original = await File.ReadAllTextAsync(marker);
        await File.WriteAllTextAsync(marker,
            original.Replace("\"version\":1", "\"version\":1,\"unknown\":true",
                StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DataProtectionRecoveryMarkerApplication.VerifyAsync(source, marker, CancellationToken.None));
    }

    [Fact]
    public async Task CliRequiresExplicitIsolatedPathsAndCanVerifyExistingMarker()
    {
        using var fixture = new Fixture();
        var ring = fixture.CreateRing("source");
        var marker = Path.Combine(fixture.Root, "recovery.marker.json");
        Assert.Equal(0, await DataProtectionRecoveryMarkerApplication.RunAsync(
            ["create", "--keys-directory", ring, "--output", marker], CancellationToken.None));
        Assert.Equal(0, await DataProtectionRecoveryMarkerApplication.RunAsync(
            ["verify", "--keys-directory", ring, "--input", marker], CancellationToken.None));
        Assert.Equal(1, await DataProtectionRecoveryMarkerApplication.RunAsync(
            ["create", "--keys-directory", ring, "--output", marker], CancellationToken.None));
        Assert.Equal(1, await DataProtectionRecoveryMarkerApplication.RunAsync(
            ["create", "--keys-directory", ring, "--secret", "inline"], CancellationToken.None));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            $"proxyharbor-dp-marker-{Guid.NewGuid():N}");

        internal Fixture() => Directory.CreateDirectory(Root);

        internal string CreateRing(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            var provider = DataProtectionProvider.Create(new DirectoryInfo(path), builder =>
                builder.SetApplicationName("ProxyHarbor"));
            _ = provider.CreateProtector("fixture").Protect("fixture plaintext");
            Assert.NotEmpty(Directory.GetFiles(path, "key-*.xml"));
            return path;
        }

        internal string CopyRing(string source, string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            foreach (var file in Directory.GetFiles(source, "key-*.xml"))
                File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
