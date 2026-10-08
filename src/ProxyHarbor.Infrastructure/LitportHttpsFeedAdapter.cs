using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Preserves wire protocols in Litport's mixed HTTPS-capable export.</summary>
internal static class LitportHttpsFeedAdapter
{
    internal static bool Supports(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath is "/litportnet/free-proxy-list/live/proxies/https.json" or
            "/litportnet/free-proxy-list/main/proxies/https.json" or
            "/litportnet/free-proxy-list/refs/heads/live/proxies/https.json" or
            "/litportnet/free-proxy-list/refs/heads/main/proxies/https.json";

    internal static string Extract(string url, string content)
    {
        if (!Supports(url)) throw InvalidFormat();
        try
        {
            using var document = JsonDocument.Parse(content.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidFormat();
            using var buffer = new MemoryStream();
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartArray();
            var count = 0;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw InvalidFormat();
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in row.EnumerateObject())
                    if (!fields.Add(field.Name) || field.Name is "username" or "password" or "user" or "pass" or "auth" or "authorization")
                        throw InvalidFormat();
                if (!row.TryGetProperty("ip", out var host) || host.ValueKind != JsonValueKind.String ||
                    !IPAddress.TryParse(host.GetString(), out var address) || !NetworkSafety.IsPublicAddress(address) ||
                    !row.TryGetProperty("port", out var port) || port.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
                    !int.TryParse(port.ValueKind == JsonValueKind.String ? port.GetString() : port.GetRawText(),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var portNumber) || portNumber is < 1 or > 65535 ||
                    !row.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String ||
                    !row.TryGetProperty("https", out var https) || https.ValueKind != JsonValueKind.True ||
                    !row.TryGetProperty("url", out var endpoint) || endpoint.ValueKind != JsonValueKind.String)
                    throw InvalidFormat();
                var expected = protocol.GetString() switch
                {
                    "http" => ProxyProtocol.Http,
                    "socks4" => ProxyProtocol.Socks4,
                    "socks5" => ProxyProtocol.Socks5,
                    _ => throw InvalidFormat()
                };
                var rawEndpoint = endpoint.GetString()!;
                if (!rawEndpoint.StartsWith(protocol.GetString() + "://", StringComparison.Ordinal) ||
                    !ProxyParser.TryParseEndpoint(rawEndpoint, ProxyProtocol.Http, out var parsed) ||
                    parsed.ToEndpoint().Host != address.ToString() || parsed.Port != portNumber || parsed.Protocol != expected)
                    throw InvalidFormat();
                // The publisher verified HTTPS through each proxy. Only HTTP uses
                // CONNECT; the SOCKS wire protocol remains SOCKS even for TLS traffic.
                writer.WriteStringValue(expected == ProxyProtocol.Http ? "https://" + rawEndpoint[7..] : rawEndpoint);
                count++;
            }
            if (count == 0) throw InvalidFormat();
            writer.WriteEndArray();
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }
        catch (JsonException) { throw InvalidFormat(); }
    }

    private static InvalidDataException InvalidFormat() => new("Неизвестный формат HTTPS-выгрузки Litport.");
}
