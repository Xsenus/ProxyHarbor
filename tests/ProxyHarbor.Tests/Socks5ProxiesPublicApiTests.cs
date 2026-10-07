using System.Text.Json;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class Socks5ProxiesPublicApiTests
{
    private static readonly string[] HttpProtocols = ["http"];
    private static readonly string[] MixedProtocols = ["socks4", "socks5"];

    [Theory]
    [InlineData("{\"data\":[],\"meta\":{\"total\":0,\"limit\":25,\"offset\":0}}")]
    [InlineData("{\"data\":[],\"meta\":{\"total\":0,\"limit\":100,\"offset\":100}}")]
    [InlineData("{\"data\":[],\"meta\":{\"total\":-1,\"limit\":100,\"offset\":0}}")]
    [InlineData("{\"data\":[],\"meta\":{\"total\":\"0\",\"limit\":100,\"offset\":0}}")]
    [InlineData("{\"data\":[],\"meta\":{\"total\":0,\"limit\":100,\"offset\":\"0\"}}")]
    [InlineData("{\"data\":[],\"meta\":{\"total\":0,\"limit\":100,\"offset\":0},\"success\":false}")]
    [InlineData("{\"data\":[],\"meta\":{\"total\":0,\"limit\":100,\"offset\":0},\"error\":\"quota\"}")]
    [InlineData("{\"data\":[],\"meta\":{\"total\":0,\"limit\":100,\"offset\":0},\"status\":0}")]
    public void IncorrectPaginationAndErrorEnvelopesCannotBecomeCompactCheckpoints(string body) =>
        Assert.Throws<InvalidDataException>(() => Socks5ProxiesPublicApi.CompactPage(body, 1));

    [Fact]
    public void CompactionPreservesMixedProtocolsAndUnsafeRowRejectionWithoutKeepingCredentials()
    {
        var body = JsonSerializer.Serialize(new
        {
            meta = new { total = 4, limit = 100, offset = 0, last_sync = "2026-10-07T17:47:29Z" },
            data = new object[] {
                new { ip = "8.8.8.8", port = 1080, protocols = MixedProtocols, country_name = new string('x', 10_000) },
                new { ip = "127.0.0.1", port = 80, protocols = HttpProtocols },
                new { ip = "9.9.9.9", port = 80, protocols = HttpProtocols, password = "credential-value-never-persisted" },
                new { ip = "1.1.1.1", port = 0, protocols = HttpProtocols }
            }
        });
        var compact = Socks5ProxiesPublicApi.CompactPage(body, 1);
        Assert.DoesNotContain("credential-value-never-persisted", compact);
        Assert.DoesNotContain("country_name", compact);
        Assert.True(compact.Length < body.Length / 10);
        using var document = JsonDocument.Parse(compact);
        var (total, rows) = Socks5ProxiesPublicApi.InspectPage(document.RootElement, 1);
        Assert.Equal(4, total);
        Assert.Equal(4, rows.GetArrayLength());
        Assert.Equal(64, document.RootElement.GetProperty("originalBodySha256").GetString()!.Length);
        Assert.Equal([ProxyProtocol.Socks4, ProxyProtocol.Socks5],
            SourceFeedParser.ParseRequired(compact, ProxyProtocol.Http).Select(p => p.Protocol).ToArray());
    }

    [Fact]
    public void EndpointIdentitySurvivesProtocolChangesAndNumericPortRepresentations()
    {
        using var first = JsonDocument.Parse("""{"ip":"8.8.8.8","port":"01080","protocols":["http"]}""");
        using var later = JsonDocument.Parse("""{"ip":"8.8.8.8","port":1080,"protocols":["socks5"]}""");
        Assert.Equal(Socks5ProxiesPublicApi.RecordKey(first.RootElement), Socks5ProxiesPublicApi.RecordKey(later.RootElement));
    }
}
