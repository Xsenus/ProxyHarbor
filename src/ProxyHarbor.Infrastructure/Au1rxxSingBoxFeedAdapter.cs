using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Reads explicit HTTP CONNECT/SOCKS outbounds without stripping profile settings.</summary>
internal static partial class Au1rxxSingBoxFeedAdapter
{
    [GeneratedRegex(@"^/Au1rxx/free-vpn-subscriptions/(?:main|refs/heads/main)/output/protocol/(http|https|socks4|socks5)/singbox-([0-9]{4})\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    internal static bool Supports(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        PathPattern().Match(uri.AbsolutePath) is { Success: true } match && match.Groups[2].Value != "0000";

    internal static Au1rxxSingBoxExtraction Extract(string url, string content)
    {
        if (!Supports(url)) throw InvalidFormat();
        var partition = PathPattern().Match(new Uri(url).AbsolutePath).Groups[1].Value;
        try
        {
            using var document = JsonDocument.Parse(content.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            CheckFields(root, "outbounds", "route");
            if (!root.TryGetProperty("outbounds", out var rows) || rows.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("route", out var route) || route.ValueKind != JsonValueKind.Object) throw InvalidFormat();
            var candidates = new List<string>();
            var held = new List<Au1rxxHeldProfile>();
            var tags = new HashSet<string>(StringComparer.Ordinal);
            var metadata = 0;
            foreach (var row in rows.EnumerateArray())
            {
                var type = RequiredString(row, "type");
                if (!tags.Add(RequiredString(row, "tag"))) throw InvalidFormat();
                if (type is "direct" or "selector" or "urltest")
                {
                    CheckFields(row, "type", "tag", "outbounds", "url", "interval");
                    if (row.TryGetProperty("outbounds", out var choices) &&
                        (choices.ValueKind != JsonValueKind.Array || choices.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))) throw InvalidFormat();
                    metadata++;
                    continue;
                }
                CheckFields(row, "type", "tag", "server", "server_port", "username", "password", "version", "tls");
                var host = RequiredString(row, "server");
                if (!row.TryGetProperty("server_port", out var portValue) || !portValue.TryGetInt32(out var port) || port is < 1 or > 65535)
                    throw InvalidFormat();
                var authenticated = OptionalString(row, "username").Length > 0 | OptionalString(row, "password").Length > 0;
                var isIp = IPAddress.TryParse(host, out var ip);
                if (isIp && !string.Equals(ip!.ToString(), host, StringComparison.OrdinalIgnoreCase) ||
                    !isIp && (Uri.CheckHostName(host) != UriHostNameType.Dns || host.Any(char.IsWhiteSpace))) throw InvalidFormat();
                var customTls = false;
                string scheme;
                if (partition is "http" or "https")
                {
                    if (type != "http" || row.TryGetProperty("version", out _)) throw InvalidFormat();
                    // sing-box's HTTP outbound is CONNECT, including when plaintext.
                    // No HTTP version restriction is accepted by this captured contract.
                    scheme = "https";
                    if (partition == "https")
                    {
                        if (!row.TryGetProperty("tls", out var tls)) throw InvalidFormat();
                        CheckFields(tls, "enabled", "insecure", "server_name");
                        if (!tls.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True ||
                            !tls.TryGetProperty("insecure", out var insecure) || insecure.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidFormat();
                        var serverName = OptionalString(tls, "server_name");
                        customTls = serverName.Length > 0 && !serverName.Equals(host, StringComparison.OrdinalIgnoreCase);
                        scheme = insecure.GetBoolean() ? "http+tls-unverified" : "http+tls";
                    }
                    else if (row.TryGetProperty("tls", out _)) throw InvalidFormat();
                }
                else
                {
                    if (type != "socks" || RequiredString(row, "version") != (partition == "socks4" ? "4" : "5") || row.TryGetProperty("tls", out _))
                        throw InvalidFormat();
                    scheme = partition;
                }
                var endpoint = $"{scheme}://{(ip?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + host + "]" : host)}:{port.ToString(CultureInfo.InvariantCulture)}";
                var unsafeAddress = isIp && !ProxyParser.TryParseEndpoint(endpoint, ProxyProtocol.Http, out _);
                if (authenticated || !isIp || customTls || unsafeAddress)
                {
                    held.Add(new Au1rxxHeldProfile(row.Clone(), authenticated, !isIp, customTls, unsafeAddress));
                    continue;
                }
                candidates.Add(endpoint);
            }
            if (candidates.Count == 0 && held.Count == 0) throw InvalidFormat();
            return new Au1rxxSingBoxExtraction(JsonSerializer.Serialize(candidates), held, metadata);
        }
        catch (JsonException) { throw InvalidFormat(); }
        catch (InvalidOperationException) { throw InvalidFormat(); }
    }

    private static void CheckFields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidFormat();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw InvalidFormat();
    }

    private static string RequiredString(JsonElement row, string name)
    {
        var value = OptionalString(row, name);
        return value.Length > 0 ? value : throw InvalidFormat();
    }

    private static string OptionalString(JsonElement row, string name) =>
        !row.TryGetProperty(name, out var value) ? "" : value.ValueKind == JsonValueKind.String ? value.GetString()! : throw InvalidFormat();

    private static InvalidDataException InvalidFormat() => new("Неподдерживаемый формат sing-box источника Au1rxx.");
}

internal sealed record Au1rxxSingBoxExtraction(string Content, IReadOnlyList<Au1rxxHeldProfile> HeldProfiles, int MetadataRows);

// Keep the full original profile available to lossless authenticated/DNS import.
// Never include these profiles in logs or flatten them into unauthenticated endpoints.
internal sealed record Au1rxxHeldProfile(JsonElement OriginalProfile, bool Authenticated, bool Dns, bool CustomTls, bool UnsafeAddress);
