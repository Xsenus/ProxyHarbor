using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Reads the publisher's NDJSON and protocol/anonymity grouped exports.</summary>
internal static class ParserPpFeedAdapter
{
    internal static bool Supports(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath is "/parserpp/ip_ports/main/proxy.list" or
            "/parserpp/ip_ports/refs/heads/main/proxy.list" or
            "/parserpp/ip_ports/main/proxyinfo.json" or
            "/parserpp/ip_ports/refs/heads/main/proxyinfo.json";

    internal static string Extract(string url, string content)
    {
        if (!Supports(url)) throw new InvalidDataException("Неизвестный export Parserpp.");
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        var count = 0;
        try
        {
            if (new Uri(url).AbsolutePath.EndsWith("/proxy.list", StringComparison.Ordinal))
            {
                ReadNdjson();
            }
            else
            {
                JsonDocument? document;
                try { document = JsonDocument.Parse(content.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException) { document = null; }
                // Two publisher jobs write this same URL: one groups records,
                // the other emits NDJSON. Validate every row in either shape.
                if (document is null) ReadNdjson();
                else using (document)
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidFormat();
                    if (document.RootElement.TryGetProperty("host", out _)) WriteRow(document.RootElement, null);
                    else
                    {
                        var groups = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var group in document.RootElement.EnumerateObject())
                        {
                            var separator = group.Name.IndexOf('_');
                            if (separator <= 0 || !groups.Add(group.Name) || group.Value.ValueKind != JsonValueKind.Array ||
                                group.Name[(separator + 1)..] is not ("high_anonymous" or "anonymous" or "transparent"))
                                throw InvalidFormat();
                            var protocol = group.Name[..separator];
                            if (!TryProtocol(protocol, out _)) throw InvalidFormat();
                            foreach (var row in group.Value.EnumerateArray()) WriteRow(row, protocol);
                        }
                    }
                }
            }
        }
        catch (JsonException) { throw InvalidFormat(); }
        if (count == 0) throw new InvalidDataException("Parserpp export не содержит публичных прокси.");
        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));

        void ReadNdjson()
        {
            using var reader = new StringReader(content.TrimStart('\uFEFF'));
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var row = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                WriteRow(row.RootElement, null);
            }
        }

        void WriteRow(JsonElement row, string? groupProtocol)
        {
            if (row.ValueKind != JsonValueKind.Object) throw InvalidFormat();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in row.EnumerateObject())
            {
                if (!fields.Add(field.Name)) throw InvalidFormat();
                if (field.Name is "username" or "password" or "user" or "pass" &&
                    field.Value.ValueKind != JsonValueKind.Null &&
                    (field.Value.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(field.Value.GetString())))
                    throw InvalidFormat();
            }
            if (!row.TryGetProperty("host", out var hostValue) || hostValue.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("port", out var portValue) || portValue.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                throw InvalidFormat();
            var name = type.GetString()!;
            if (!TryProtocol(name, out var protocol) || (groupProtocol is not null && name != groupProtocol))
                throw InvalidFormat();
            var host = hostValue.GetString()!;
            var portText = portValue.ValueKind == JsonValueKind.String ? portValue.GetString() : portValue.GetRawText();
            if (!IPAddress.TryParse(host, out _) ||
                !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                return;
            var address = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
            if (!ProxyParser.TryParseEndpoint(address, protocol, out var key)) return;
            var endpoint = key.ToEndpoint();
            writer.WriteStartObject();
            writer.WriteString("host", endpoint.Host);
            writer.WriteNumber("port", endpoint.Port);
            // Publisher generators disagree on HTTPS wire semantics. Preserve
            // the explicit label and the operator's established TLS fallback.
            writer.WriteString("protocol", name);
            writer.WriteEndObject();
            count++;
        }
    }

    private static bool TryProtocol(string name, out ProxyProtocol protocol)
    {
        protocol = name switch
        {
            "http" => ProxyProtocol.Http,
            "https" => ProxyProtocol.Https,
            "socks4" => ProxyProtocol.Socks4,
            "socks5" => ProxyProtocol.Socks5,
            _ => (ProxyProtocol)(-1)
        };
        return Enum.IsDefined(protocol);
    }

    private static InvalidDataException InvalidFormat() => new("Неизвестный формат Parserpp export.");
}
