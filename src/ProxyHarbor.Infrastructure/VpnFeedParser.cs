using System.Net;
using System.Text;
using System.Text.Json;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Извлекает endpoint и готовые публичные URI подключения из распространённых VPN форматов.</summary>
public static class VpnFeedParser
{
    private const int MaxDecodedLength = 8 * 1024 * 1024;
    private const int DefaultMaximumResults = 50_000;

    /// <summary>Разбирает bounded feed и возвращает дедуплицированные endpoint.</summary>
    public static IReadOnlyList<VpnCandidate> Parse(
        string content,
        VpnProtocol fallback,
        int maxResults = DefaultMaximumResults)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        var result = new Dictionary<string, VpnCandidate>(StringComparer.OrdinalIgnoreCase);
        ParseText(content, fallback, result, maxResults);
        if (result.Count == 0 && TryDecodeBase64(content.Trim(), out var decoded))
            ParseText(decoded, fallback, result, maxResults);
        return result.Values.ToArray();
    }

    private static void ParseText(
        string content,
        VpnProtocol fallback,
        Dictionary<string, VpnCandidate> result,
        int maxResults)
    {
        // OpenVPN-конфигурации и WireGuard INI могут занимать несколько строк.
        if (fallback == VpnProtocol.OpenVpn) ParseOpenVpn(content, result);
        if (fallback == VpnProtocol.WireGuard) ParseWireGuardConfig(content, result);

        // Не используем string.Split: крупный публичный feed создавал массив из сотен
        // тысяч строк и кратковременно удваивал расход памяти контейнера. Здесь в памяти
        // существует только текущий сегмент, а разбор останавливается после bounded-лимита.
        for (var start = 0; start <= content.Length && result.Count < maxResults;)
        {
            var end = start;
            while (end < content.Length && content[end] is not ('\r' or '\n' or ',' or ';')) end++;
            var lineSpan = content.AsSpan(start, end - start).Trim().Trim('"');
            start = end + 1;
            if (lineSpan.Length is 0 or > 16_384 || lineSpan[0] == '#') continue;
            var line = lineSpan.ToString();
            if (line.Length is 0 or > 16_384 || line[0] == '#') continue;
            if (line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) ParseVmess(line, result);
            else if (TryProtocolUri(line, fallback, out var candidate)) Add(candidate, result);
            else if (fallback == VpnProtocol.OpenVpn && TryDecodeBase64(line, out var ovpn)) ParseOpenVpn(ovpn, result);
        }
    }

    private static bool TryProtocolUri(string value, VpnProtocol fallback, out VpnCandidate candidate)
    {
        candidate = default;
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator < 1) return false;
        var scheme = value[..separator].ToLowerInvariant();
        var protocol = scheme switch
        {
            "vless" => VpnProtocol.Vless,
            "trojan" => VpnProtocol.Trojan,
            "ss" => VpnProtocol.Shadowsocks,
            "hysteria2" or "hy2" => VpnProtocol.Hysteria2,
            "tuic" => VpnProtocol.Tuic,
            "wireguard" or "wg" => VpnProtocol.WireGuard,
            _ => fallback
        };
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65_535)
            return false;
        var transport = protocol is VpnProtocol.WireGuard or VpnProtocol.Hysteria2 or VpnProtocol.Tuic ? "udp" : "tcp";
        candidate = new VpnCandidate(uri.Host, uri.Port, protocol, transport, value);
        return IsSafe(candidate);
    }

    private static void ParseVmess(string value, Dictionary<string, VpnCandidate> result)
    {
        if (!TryDecodeBase64(value[8..], out var json)) return;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!root.TryGetProperty("add", out var address) || !root.TryGetProperty("port", out var portElement)) return;
            var host = address.GetString();
            var portText = portElement.ValueKind == JsonValueKind.Number ? portElement.GetRawText() : portElement.GetString();
            if (host is null || !int.TryParse(portText, out var port)) return;
            Add(new VpnCandidate(host, port, VpnProtocol.Vmess, "tcp", value), result);
        }
        catch (JsonException) { }
    }

    private static void ParseOpenVpn(string content, Dictionary<string, VpnCandidate> result)
    {
        foreach (var raw in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !parts[0].Equals("remote", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[2], out var port)) continue;
            var transport = parts.Length > 3 && parts[3].StartsWith("udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
            Add(new VpnCandidate(parts[1], port, VpnProtocol.OpenVpn, transport), result);
        }
    }

    private static void ParseWireGuardConfig(string content, Dictionary<string, VpnCandidate> result)
    {
        foreach (var raw in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!raw.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase)) continue;
            var value = raw[(raw.IndexOf('=') + 1)..].Trim();
            if (TryHostPort(value, out var host, out var port)) Add(new(host, port, VpnProtocol.WireGuard, "udp"), result);
        }
    }

    private static bool TryHostPort(string value, out string host, out int port)
    {
        host = string.Empty; port = 0;
        if (Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out var uri) && uri.Port is >= 1 and <= 65_535)
        { host = uri.Host; port = uri.Port; return true; }
        return false;
    }

    private static void Add(VpnCandidate candidate, Dictionary<string, VpnCandidate> result)
    {
        if (!IsSafe(candidate)) return;
        var normalizedHost = candidate.Host.Trim().Trim('[', ']').ToLowerInvariant();
        var normalized = candidate with { Host = normalizedHost };
        result[$"{normalized.Protocol}:{normalized.Transport}:{normalized.Host}:{normalized.Port}"] = normalized;
    }

    private static bool IsSafe(VpnCandidate candidate)
    {
        // PostgreSQL text/json values cannot contain U+0000. Some public subscription
        // files contain binary padding inside an otherwise parseable URI; Uri.TryCreate
        // accepts a subset of those values, but one such row would abort the whole COPY.
        // Reject the damaged candidate here so healthy neighbours still reach the catalog.
        if (candidate.ConnectionUri?.Contains('\0') == true ||
            candidate.Port is < 1 or > 65_535 || candidate.Host.Length is 0 or > 253)
            return false;
        var host = candidate.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var address)) return NetworkSafety.IsPublicAddress(address);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        return NetworkSafety.IsCanonicalDnsName(host);
    }

    private static bool TryDecodeBase64(string value, out string decoded)
    {
        decoded = string.Empty;
        if (value.Length is 0 or > MaxDecodedLength * 2) return false;
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(normalized);
            if (bytes.Length > MaxDecodedLength) return false;
            decoded = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>Кандидат с публичной ссылкой подключения, если feed опубликовал URI-формат.</summary>
public readonly record struct VpnCandidate
{
    /// <summary>Создаёт кандидата.</summary>
    public VpnCandidate(string host, int port, VpnProtocol protocol, string transport, string? connectionUri = null) =>
        (Host, Port, Protocol, Transport, ConnectionUri) = (host, port, protocol, transport, connectionUri);
    /// <summary>Публичный IP либо DNS-имя.</summary>
    public string Host { get; init; }
    /// <summary>Сетевой порт.</summary>
    public int Port { get; init; }
    /// <summary>VPN-протокол.</summary>
    public VpnProtocol Protocol { get; init; }
    /// <summary>TCP либо UDP.</summary>
    public string Transport { get; init; }
    /// <summary>Исходная готовая URI-конфигурация для импорта клиентом.</summary>
    public string? ConnectionUri { get; init; }
}
