using System.Globalization;
using System.Text.Json;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Reads Telegram proxy links without treating their link-service host as the endpoint.</summary>
internal static class MtProtoLinkParser
{
    internal static bool TryReadJson(string content, Func<VpnCandidate, bool> accept)
    {
        var trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.Length == 0 || trimmed[0] is not ('[' or '{')) return false;
        try
        {
            using var document = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 32 });
            var entries = document.RootElement;
            if (entries.ValueKind == JsonValueKind.Object)
            {
                if (entries.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                    return true;
                if (!entries.TryGetProperty("proxies", out entries)) return true;
            }
            if (entries.ValueKind != JsonValueKind.Array) return true;
            foreach (var entry in entries.EnumerateArray())
            {
                string? link = null;
                if (entry.ValueKind == JsonValueKind.String) link = entry.GetString();
                else if (entry.ValueKind == JsonValueKind.Object)
                {
                    if (entry.TryGetProperty("link", out var published) && published.ValueKind == JsonValueKind.String)
                        link = published.GetString();
                    else if (entry.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.String &&
                        entry.TryGetProperty("secret", out var secret) && secret.ValueKind == JsonValueKind.String &&
                        entry.TryGetProperty("port", out var port) && port.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    {
                        var host = server.GetString()!;
                        var portText = port.ValueKind == JsonValueKind.String ? port.GetString()! : port.GetRawText();
                        var credential = secret.GetString()!;
                        if (host.Length is 0 or > 253 || portText.Length is 0 or > 5 || credential.Length is 0 or > 398)
                            continue;
                        link = "tg://proxy?server=" + Uri.EscapeDataString(host) +
                            "&port=" + Uri.EscapeDataString(portText) +
                            "&secret=" + Uri.EscapeDataString(credential);
                    }
                }
                if (link is null || link.Length > 16_384 || link.Any(char.IsControl)) continue;
                foreach (var candidate in VpnFeedParser.Parse(link, VpnProtocol.Vless, 1))
                    if (!accept(candidate)) return true;
            }
        }
        catch (JsonException) { }
        return true;
    }

    internal static bool TryParse(string value, out VpnCandidate candidate)
    {
        candidate = default;
        if (value.Length is 0 or > 16_384 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return false;
        var telegramUri = uri.Scheme.Equals("tg", StringComparison.OrdinalIgnoreCase) &&
            ((uri.Host.Equals("proxy", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath is "" or "/") ||
             (uri.Host.Length == 0 && uri.AbsolutePath == "proxy"));
        var webUri = uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host is "t.me" or "telegram.me" or "telegram.dog" && uri.AbsolutePath == "/proxy";
        if (!telegramUri && !webUri) return false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var equals = part.IndexOf('=');
            if (equals < 1) continue;
            var name = Uri.UnescapeDataString(part[..equals]);
            var content = Uri.UnescapeDataString(part[(equals + 1)..]);
            if (name.Any(char.IsControl) || content.Any(char.IsControl)) return false;
            if (name is "server" or "port" or "secret" && !fields.TryAdd(name, content)) return false;
        }
        if (!fields.TryGetValue("server", out var host) || host.Any(char.IsWhiteSpace) ||
            !fields.TryGetValue("port", out var portText) ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            !fields.TryGetValue("secret", out var secret) || !IsValidSecret(secret)) return false;
        candidate = new VpnCandidate(host, port, VpnProtocol.MtProto, "tcp", value);
        return VpnFeedParser.IsSafe(candidate);
    }

    private static bool IsValidSecret(string secret)
    {
        // TDLib ProxySecret::from_link tries hex, base64url and base64 in that order.
        // Its binary forms are 16 bytes, dd + 16 bytes, or ee + 16 bytes + domain
        // (at most 182 bytes). Do not truncate a damaged published credential.
        if (secret.Length is 0 or > 398 || secret.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('+' or '/' or '=' or '-' or '_')))
            return false;
        byte[] bytes;
        try
        {
            if (secret.Length % 2 == 0 && secret.All(char.IsAsciiHexDigit)) bytes = Convert.FromHexString(secret);
            else
            {
                var normalized = secret.Replace('-', '+').Replace('_', '/');
                bytes = Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
            }
        }
        catch (FormatException) { return false; }
        return bytes.Length == 16 || bytes.Length == 17 && bytes[0] == 0xdd ||
            bytes.Length is >= 18 and <= 199 && bytes[0] == 0xee;
    }
}
