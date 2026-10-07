using System.Globalization;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Canonical public pagination and explicit multi-protocol labels published by Proxiware.</summary>
internal static class ProxiwarePublicApi
{
    internal const string Url = "https://papi.proxiware.com/proxies?page=1&country=&protocol=&anonymity=&speed=";
    internal const string Origin = "https://papi.proxiware.com";
    internal static bool Supports(string url) => string.Equals(url, Url, StringComparison.Ordinal);
    internal static bool IsOriginUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.TrimEnd('.').Equals("papi.proxiware.com", StringComparison.OrdinalIgnoreCase);

    internal static string RecordKey(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("addr", out var address) || address.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty("port", out var port) || port.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
            !row.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Public API record has no stable endpoint identity.");
        var host = address.GetString()!;
        var name = protocol.GetString()!;
        var portText = port.ValueKind == JsonValueKind.String ? port.GetString()! : port.GetRawText();
        if (host.Length > 512 || name.Length > 64 || portText.Length > 32 ||
            host.Any(char.IsControl) || name.Any(char.IsControl) || portText.Any(char.IsControl))
            throw new InvalidDataException("Public API record identity exceeds safe limits.");
        if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            portText = number.ToString(CultureInfo.InvariantCulture);
        // Preserve the complete tuple rather than truncating a hash to a numeric ID.
        // Endpoint safety and supported transports are still enforced by the feed parser.
        return "p:" + JsonSerializer.Serialize(new[] { host, portText, name.ToLowerInvariant() });
    }

    internal static void WriteRecord(Utf8JsonWriter writer, JsonElement row)
    {
        string[]? protocols = row.GetProperty("protocol").GetString()!.ToLowerInvariant() switch
        {
            "socks4socks5" => ["socks4", "socks5"],
            "httpsocks4" => ["http", "socks4"],
            "httpsocks5" => ["http", "socks5"],
            "httphttps" => ["http", "https"],
            "httpsocks4socks5" => ["http", "socks4", "socks5"],
            _ => null
        };
        if (protocols is null || row.TryGetProperty("protocols", out _))
        {
            row.WriteTo(writer);
            return;
        }
        writer.WriteStartObject();
        foreach (var property in row.EnumerateObject()) property.WriteTo(writer);
        writer.WriteStartArray("protocols");
        foreach (var protocol in protocols) writer.WriteStringValue(protocol);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
