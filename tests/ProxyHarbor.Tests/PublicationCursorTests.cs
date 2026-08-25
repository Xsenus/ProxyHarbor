using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует переносимость, каноничность и привязку cursor к фильтрам.</summary>
public sealed class PublicationCursorTests
{
    [Fact]
    public void CursorRoundTripsAsFixedLengthBase64Url()
    {
        var position = new PublicationPosition(
            123, 17, Guid.Parse("12345678-1234-5678-90ab-cdef12345678"));
        var fingerprint = PublicationCursor.FilterFingerprint(ProxyProtocol.Socks5, 900, 87.5m);

        var encoded = PublicationCursor.Encode(position, fingerprint);

        Assert.Equal(PublicationCursor.EncodedLength, encoded.Length);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.True(PublicationCursor.TryDecode(encoded, fingerprint, out var decoded));
        Assert.Equal(position, decoded);
    }

    [Fact]
    public void FingerprintNormalizesEquivalentDecimalFilters()
    {
        Assert.Equal(
            PublicationCursor.FilterFingerprint(ProxyProtocol.Http, 500, 80m),
            PublicationCursor.FilterFingerprint(ProxyProtocol.Http, 500, 80.000m));
    }

    [Fact]
    public void FingerprintBindsCursorToTheSelectedCountrySet()
    {
        var first = PublicationCursor.FilterFingerprint(ProxyProtocol.Http, 500, 80m, ["DE", "US"]);
        var reordered = PublicationCursor.FilterFingerprint(ProxyProtocol.Http, 500, 80m, ["US", "DE"]);
        var changed = PublicationCursor.FilterFingerprint(ProxyProtocol.Http, 500, 80m, ["FR"]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void CursorCannotContinueWithDifferentFilters()
    {
        var original = PublicationCursor.FilterFingerprint(ProxyProtocol.Http, 500, 80m);
        var changed = PublicationCursor.FilterFingerprint(ProxyProtocol.Http, 501, 80m);
        var encoded = PublicationCursor.Encode(
            new PublicationPosition(100, 3, Guid.NewGuid()), original);

        Assert.False(PublicationCursor.TryDecode(encoded, changed, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void MalformedOrUnsupportedCursorFailsClosed(string encoded)
    {
        Assert.False(PublicationCursor.TryDecode(encoded, 0, out _));
    }

    [Fact]
    public void InvalidPublicationPositionCannotBeEncoded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PublicationCursor.Encode(new PublicationPosition(-1, 0, Guid.NewGuid()), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PublicationCursor.Encode(new PublicationPosition(1, 0, Guid.Empty), 0));
    }

    [Fact]
    public void NpgsqlTranslatesSeekPredicateWithoutOffset()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=unused;Password=unused")
            .Options;
        using var db = new ProxyHarborDbContext(options);

        var sql = ProxiesController.ApplyAfter(
                db.Proxies.AsNoTracking(),
                new PublicationPosition(
                    123, 17, Guid.Parse("12345678-1234-5678-90ab-cdef12345678")))
            .OrderBy(x => x.LatencyMs)
            .ThenByDescending(x => x.SuccessfulChecks)
            .ThenBy(x => x.Id)
            .Take(101)
            .ToQueryString();

        Assert.Contains("LatencyMs", sql, StringComparison.Ordinal);
        Assert.Contains("SuccessfulChecks", sql, StringComparison.Ordinal);
        Assert.Contains("Id", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.OrdinalIgnoreCase);
    }
}
