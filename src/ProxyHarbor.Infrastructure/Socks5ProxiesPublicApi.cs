using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Documented public offset pagination with bounded endpoint-only checkpoints.</summary>
internal static class Socks5ProxiesPublicApi
{
    internal const string Url = "https://api.socks5proxies.com/api/proxies?limit=100&offset=0&sort=country";
    internal const string Origin = "https://api.socks5proxies.com";
    internal const int PageSize = 100;
    internal static bool Supports(string url) => string.Equals(url, Url, StringComparison.Ordinal);
    internal static bool IsApiUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.TrimEnd('.').Equals("api.socks5proxies.com", StringComparison.OrdinalIgnoreCase) &&
        IsListPath(Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/'));

    private static bool IsListPath(string path) =>
        path.Equals("/api/proxies", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/proxies/", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/v1/proxies", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/v1/proxies/", StringComparison.OrdinalIgnoreCase);

    internal static DateTimeOffset? RateLimitDeadline(HttpResponseHeaders headers, DateTimeOffset now, DateTimeOffset? retryAfter)
    {
        if (!headers.TryGetValues("X-RateLimit-Reset", out var values)) return retryAfter;
        var raw = values.Take(2).ToArray();
        if (raw.Length != 1 || raw[0].Length > 12 ||
            !long.TryParse(raw[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return retryAfter;
        var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return reset > now && (retryAfter is null || reset > retryAfter) ? reset : retryAfter;
    }

    internal static (long Total, JsonElement Rows) InspectPage(JsonElement root, int pageIndex)
    {
        if (pageIndex is < 1 or > FreeProxyDbPageCapture.MaximumPages || root.ValueKind != JsonValueKind.Object ||
            (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
            (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var statusCode) && statusCode == 0) ||
            (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null &&
                (error.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(error.GetString()))) ||
            !root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("total", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var total) ||
            total is < 0 or > FreeProxyDbPageCapture.MaximumPages * PageSize ||
            !meta.TryGetProperty("limit", out var limit) || limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var size) || size != PageSize ||
            !meta.TryGetProperty("offset", out var offset) || offset.ValueKind != JsonValueKind.Number || !offset.TryGetInt32(out var position) || position != (pageIndex - 1) * PageSize ||
            (meta.TryGetProperty("last_sync", out var sync) && (sync.ValueKind != JsonValueKind.String || sync.GetString()!.Length > 64 || sync.GetString()!.Any(char.IsControl))) ||
            !root.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > PageSize)
            throw new InvalidDataException("Public offset API page metadata is inconsistent.");
        return (total, rows);
    }

    internal static string RecordKey(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("ip", out var ip) || ip.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty("port", out var port) || port.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
            !row.TryGetProperty("protocols", out var protocols) || protocols.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Public offset API record has no endpoint identity.");
        var host = ip.GetString()!;
        var portText = port.ValueKind == JsonValueKind.String ? port.GetString()! : port.GetRawText();
        if (host.Length is < 1 or > 512 || host.Any(char.IsControl) || portText.Length is < 1 or > 64 || portText.Any(char.IsControl) ||
            protocols.GetArrayLength() > 16 || protocols.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String || p.GetString()!.Length > 64 || p.GetString()!.Any(char.IsControl)))
            throw new InvalidDataException("Public offset API identity exceeds safe limits.");
        if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            portText = number.ToString(CultureInfo.InvariantCulture);
        return "s:" + JsonSerializer.Serialize(new[] { host, portText });
    }

    // Keep complete endpoint/protocol identities, including unsafe rows for count reconciliation.
    // Credential markers preserve the common parser's rejection without persisting credentials.
    internal static string CompactPage(string content, int pageIndex)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(content) > FreeProxyDbPageCapture.MaximumPageBytes)
            throw new InvalidDataException("Public offset API page exceeds the body limit.");
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var (_, rows) = InspectPage(root, pageIndex);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("meta");
            var meta = root.GetProperty("meta");
            writer.WriteStartObject();
            foreach (var name in new[] { "total", "limit", "offset", "last_sync" })
                if (meta.TryGetProperty(name, out var field)) { writer.WritePropertyName(name); field.WriteTo(writer); }
            writer.WriteEndObject();
            writer.WriteString("originalBodySha256", Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
            writer.WriteStartArray("data");
            foreach (var row in rows.EnumerateArray())
            {
                _ = RecordKey(row);
                writer.WriteStartObject();
                foreach (var name in new[] { "ip", "port", "protocols" })
                {
                    writer.WritePropertyName(name);
                    row.GetProperty(name).WriteTo(writer);
                }
                foreach (var name in new[] { "username", "password", "user", "pass" })
                    if (row.TryGetProperty(name, out var credential) && credential.ValueKind != JsonValueKind.Null &&
                        (credential.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(credential.GetString())))
                        writer.WriteBoolean(name, true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
}
