using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Public v4 export. Offset traversal ends at an observed empty page, not a mutable total.</summary>
internal static class ProxyScrapePublicApi
{
    internal const string Url = "https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=json&skip=0&limit=2000";
    internal const string Origin = "https://api.proxyscrape.com";
    internal const int PageSize = 2000;
    internal static bool Supports(string url) => string.Equals(url, Url, StringComparison.Ordinal);
    internal static bool IsApiUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.TrimEnd('.').Equals("api.proxyscrape.com", StringComparison.OrdinalIgnoreCase) &&
        (Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').Equals("/v4/free-proxy-list/get", StringComparison.OrdinalIgnoreCase) ||
            Uri.UnescapeDataString(uri.AbsolutePath).StartsWith("/v4/free-proxy-list/get/", StringComparison.OrdinalIgnoreCase));

    internal static (long Total, JsonElement Rows) InspectPage(JsonElement root, int pageIndex)
    {
        if (pageIndex is < 1 or > FreeProxyDbPageCapture.MaximumPages || root.ValueKind != JsonValueKind.Object ||
            (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
            (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null &&
                (error.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(error.GetString()))) ||
            (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code == 0) ||
            !root.TryGetProperty("total_records", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var total) ||
            total is < 0 or > FreeProxyDbPageCapture.MaximumPages * PageSize ||
            !root.TryGetProperty("skip", out var skip) || skip.ValueKind != JsonValueKind.Number || !skip.TryGetInt32(out var offset) || offset != (pageIndex - 1) * PageSize ||
            !root.TryGetProperty("limit", out var limit) || limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var size) || size != PageSize ||
            !root.TryGetProperty("shown_records", out var shown) || shown.ValueKind != JsonValueKind.Number || !shown.TryGetInt32(out var length) || length is < 0 or > PageSize ||
            !root.TryGetProperty("nextpage", out var next) || next.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("proxies", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != length ||
            (length == 0 && (next.GetBoolean() || offset < total)))
            throw new InvalidDataException("Public ProxyScrape page metadata is inconsistent.");
        // Live totals and rows are not transactional: a captured tail had one more row than total-skip.
        // A nonempty nextpage=false response therefore still requires a later empty offset.
        return (total, rows);
    }

    internal static string RecordKey(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("ip", out var host) || host.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty("port", out var port) || port.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
            !row.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Public ProxyScrape record has no typed endpoint identity.");
        var address = host.GetString()!;
        var portText = port.ValueKind == JsonValueKind.String ? port.GetString()! : port.GetRawText();
        var type = protocol.GetString()!;
        if (address.Length is < 1 or > 512 || address.Any(char.IsControl) || portText.Length is < 1 or > 64 || portText.Any(char.IsControl) ||
            type is not ("http" or "socks4" or "socks5"))
            throw new InvalidDataException("Public ProxyScrape identity is inconsistent.");
        if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            portText = number.ToString(CultureInfo.InvariantCulture);
        return "s:" + JsonSerializer.Serialize(new[] { address, portText, type });
    }

    internal static string CompactPage(string content, int pageIndex)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > FreeProxyDbPageCapture.MaximumPageBytes) throw new InvalidDataException("Public ProxyScrape page exceeds the safe size limit.");
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
        var (total, rows) = InspectPage(document.RootElement, pageIndex);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("total_records", total);
            writer.WriteNumber("skip", (pageIndex - 1) * PageSize);
            writer.WriteNumber("limit", PageSize);
            writer.WriteNumber("shown_records", rows.GetArrayLength());
            writer.WriteBoolean("nextpage", document.RootElement.GetProperty("nextpage").GetBoolean());
            writer.WriteString("originalBodySha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            writer.WriteStartArray("proxies");
            foreach (var row in rows.EnumerateArray())
            {
                _ = RecordKey(row);
                writer.WriteStartObject();
                foreach (var field in new[] { "ip", "port", "protocol" })
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
