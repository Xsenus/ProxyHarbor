using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Читает опубликованные JSON-списки без извлечения адресов из diagnostic/error полей.</summary>
internal static class JsonProxyFeedParser
{
    internal static ProxyParseSummary? TryParseTo(
        string content, ProxyProtocol fallback, int maxResults, Action<ProxyCandidateKey> accept)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(accept);
        var trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        var start = trimmed.AsSpan();
        if (start.IsEmpty || (start[0] != '{' && start[0] != '[')) return null;
        // Текстовый IPv6 feed также начинается с '[' и остаётся обычным text feed.
        var close = start.IndexOf(']');
        if (start[0] == '[' && close > 0 && close + 1 < start.Length && start[close + 1] == ':' &&
            IPAddress.TryParse(start[1..close], out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            return null;

        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        using var document = ReadDocument(trimmed);
        var unique = new HashSet<ProxyCandidateKey>(Math.Min(maxResults, 4_096));
        foreach (var candidate in ReadCandidates(document.RootElement, fallback, maxResults))
        {
            if (unique.Contains(candidate)) continue;
            if (unique.Count == maxResults) return new ProxyParseSummary(unique.Count, true);
            unique.Add(candidate);
            accept(candidate);
        }
        return new ProxyParseSummary(unique.Count, false);
    }

    private static JsonDocument ReadDocument(string content)
    {
        try { return JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException)
        {
            throw new InvalidDataException("Источник вернул некорректный JSON списка прокси.");
        }
    }

    private static IEnumerable<ProxyCandidateKey> ReadCandidates(JsonElement value, ProxyProtocol fallback, int maxResults)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                foreach (var candidate in ReadCandidates(item, fallback, maxResults)) yield return candidate;
            yield break;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            using var lines = new StringReader(value.GetString()!);
            while (lines.ReadLine() is { } line)
                if (ProxyParser.TryParseEndpoint(line, fallback, out var endpoint)) yield return endpoint;
            yield break;
        }
        if (value.ValueKind != JsonValueKind.Object) yield break;
        if ((value.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
            (value.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number &&
                status.TryGetInt32(out var statusCode) && statusCode == 0)) yield break;
        if (HasCredential(value, "username") || HasCredential(value, "password") ||
            HasCredential(value, "user") || HasCredential(value, "pass")) yield break;
        if (value.TryGetProperty("ip_address", out var address) || value.TryGetProperty("ip", out address) ||
            value.TryGetProperty("host", out address))
        {
            if (address.ValueKind != JsonValueKind.String || !value.TryGetProperty("port", out var port)) yield break;
            var host = address.GetString()!;
            if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip) || !NetworkSafety.IsPublicAddress(ip)) yield break;
            if (port.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) yield break;
            var portText = port.ValueKind == JsonValueKind.String ? port.GetString() : port.GetRawText();
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var portNumber) ||
                portNumber is < 1 or > 65535) yield break;
            portText = portNumber.ToString(CultureInfo.InvariantCulture);
            var endpoint = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{portText}" : $"{host}:{portText}";
            foreach (var protocol in ReadProtocols(value, fallback))
                if (ProxyParser.TryParseEndpoint(endpoint, protocol, out var parsed)) yield return parsed;
            yield break;
        }
        // API envelopes and the existing Gifted Proxies wrapper. Arbitrary message,
        // metadata, next-URL and error fields are deliberately not proxy lists.
        foreach (var name in new[] { "results", "data", "proxies" })
            if (value.TryGetProperty(name, out var nested))
                foreach (var candidate in ReadCandidates(nested, fallback, maxResults)) yield return candidate;
    }

    private static bool HasCredential(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null &&
        (field.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(field.GetString()));

    private static IEnumerable<ProxyProtocol> ReadProtocols(JsonElement value, ProxyProtocol fallback)
    {
        if (value.TryGetProperty("protocols", out var protocols))
        {
            if (protocols.ValueKind == JsonValueKind.Array)
                foreach (var protocol in protocols.EnumerateArray())
                    if (TryProtocol(protocol, out var parsed)) yield return parsed;
            yield break;
        }
        if (value.TryGetProperty("protocol", out var single) || value.TryGetProperty("type", out single))
        {
            if (TryProtocol(single, out var parsed)) yield return parsed;
            yield break;
        }
        yield return fallback;
    }

    private static bool TryProtocol(JsonElement value, out ProxyProtocol protocol)
    {
        var name = value.ValueKind == JsonValueKind.String ? value.GetString()?.ToLowerInvariant() : null;
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
}
