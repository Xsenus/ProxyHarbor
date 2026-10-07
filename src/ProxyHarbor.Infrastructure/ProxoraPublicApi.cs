using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Documented public Proxora list; row protocols remain authoritative.</summary>
internal static class ProxoraPublicApi
{
    internal const string Url = "https://api.proxora.io/api/tools/free-proxies?per_page=500&page=1&sort=fresh";
    internal const string Origin = "https://api.proxora.io";
    internal const int PageSize = 500;
    internal static bool Supports(string url) => string.Equals(url, Url, StringComparison.Ordinal);
    internal static bool IsApiUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.TrimEnd('.').Equals("api.proxora.io", StringComparison.OrdinalIgnoreCase) &&
        (Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').Equals("/api/tools/free-proxies", StringComparison.OrdinalIgnoreCase) ||
            Uri.UnescapeDataString(uri.AbsolutePath).StartsWith("/api/tools/free-proxies/", StringComparison.OrdinalIgnoreCase));

    internal static (long Total, JsonElement Rows) InspectPage(JsonElement root, int pageIndex)
    {
        if (pageIndex is < 1 or > FreeProxyDbPageCapture.MaximumPages || root.ValueKind != JsonValueKind.Object ||
            (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
            (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null &&
                (error.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(error.GetString()))) ||
            (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code == 0) ||
            !root.TryGetProperty("total", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var total) ||
            total is < 0 or > FreeProxyDbPageCapture.MaximumPages * PageSize ||
            !root.TryGetProperty("page", out var page) || page.ValueKind != JsonValueKind.Number || !page.TryGetInt32(out var number) || number != pageIndex ||
            !root.TryGetProperty("per_page", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out var limit) || limit != PageSize ||
            !root.TryGetProperty("items", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > PageSize)
            throw new InvalidDataException("Public Proxora page metadata is inconsistent.");
        return (total, rows);
    }

    internal static string RecordKey(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty("port", out var port) || port.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
            !row.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Public Proxora record has no typed endpoint identity.");
        var address = host.GetString()!;
        var portText = port.ValueKind == JsonValueKind.String ? port.GetString()! : port.GetRawText();
        var type = protocol.GetString()!;
        if (address.Length is < 1 or > 512 || address.Any(char.IsControl) || portText.Length is < 1 or > 64 || portText.Any(char.IsControl) ||
            type is not ("http" or "socks4" or "socks5"))
            throw new InvalidDataException("Public Proxora identity is inconsistent.");
        if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            portText = number.ToString(CultureInfo.InvariantCulture);
        // Publisher total counts typed rows; one endpoint may support multiple protocols.
        return "p:" + JsonSerializer.Serialize(new[] { address, portText, type });
    }

    internal static string CompactPage(string content, int pageIndex)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > FreeProxyDbPageCapture.MaximumPageBytes) throw new InvalidDataException("Public Proxora page exceeds the safe size limit.");
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
        var (total, rows) = InspectPage(document.RootElement, pageIndex);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("total", total); writer.WriteNumber("page", pageIndex); writer.WriteNumber("per_page", PageSize);
            writer.WriteString("originalBodySha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            writer.WriteStartArray("items");
            foreach (var row in rows.EnumerateArray())
            {
                _ = RecordKey(row);
                writer.WriteStartObject();
                foreach (var field in new[] { "host", "port", "protocol" })
                {
                    writer.WritePropertyName(field); row.GetProperty(field).WriteTo(writer);
                }
                foreach (var field in new[] { "username", "password", "user", "pass" })
                    if (row.TryGetProperty(field, out var credential) && credential.ValueKind != JsonValueKind.Null &&
                        (credential.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(credential.GetString())))
                        writer.WriteBoolean(field, true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
