using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class ProxyCountryGeolocationTests
{
    [Fact]
    public void ResolverSafelyHandlesMissingDatabaseAndInvalidAddresses()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"proxyharbor-missing-{Guid.NewGuid():N}.mmdb");
        using var resolver = new ProxyCountryResolver(Options.Create(new GeoIpOptions { DatabasePath = missingPath }));

        Assert.False(resolver.Reload());
        Assert.Null(resolver.Resolve(null));
        Assert.Null(resolver.Resolve("not-an-ip"));
        Assert.Null(resolver.Resolve("8.8.8.8"));
    }

    [Fact]
    public async Task BoundedCopyCopiesAllowedContentAndRejectsOversizedContent()
    {
        await using var allowedInput = new MemoryStream([1, 2, 3]);
        await using var output = new MemoryStream();
        await ProxyCountryWorker.CopyBoundedAsync(allowedInput, output, 3, CancellationToken.None);
        Assert.Equal([1, 2, 3], output.ToArray());

        await using var oversizedInput = new MemoryStream([1, 2, 3, 4]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProxyCountryWorker.CopyBoundedAsync(oversizedInput, Stream.Null, 3, CancellationToken.None));
    }
}
