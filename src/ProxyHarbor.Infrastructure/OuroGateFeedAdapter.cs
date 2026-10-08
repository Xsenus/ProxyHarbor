using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Reads the publisher's country-keyed public proxy pool without inferring protocols.</summary>
internal static class OuroGateFeedAdapter
{
    internal static bool Supports(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath is "/NotoriusP/ourogate-proxies/main/proxies_public.json" or
            "/NotoriusP/ourogate-proxies/refs/heads/main/proxies_public.json";

    internal static string Extract(string url, string content)
        => ExtractWithReport(url, content).Content;

    internal static OuroGateExtraction ExtractWithReport(string url, string content)
    {
        if (!Supports(url)) throw InvalidFormat();
        try
        {
            using var document = JsonDocument.Parse(content.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidFormat();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!fields.Add(field.Name) || field.Name is not ("updatedAt" or "source" or "countries" or "proxies"))
                    throw InvalidFormat();
            if (fields.Count != 4 || root.GetProperty("source").ValueKind != JsonValueKind.String ||
                root.GetProperty("source").GetString() != "ourogate-proxy" ||
                root.GetProperty("updatedAt").ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(root.GetProperty("updatedAt").GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _) ||
                root.GetProperty("countries").ValueKind != JsonValueKind.Array ||
                root.GetProperty("proxies").ValueKind != JsonValueKind.Object)
                throw InvalidFormat();
            var countries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var country in root.GetProperty("countries").EnumerateArray())
                if (country.ValueKind != JsonValueKind.String || !IsCountry(country.GetString()!) || !countries.Add(country.GetString()!))
                    throw InvalidFormat();
            var groups = new HashSet<string>(StringComparer.Ordinal);
            using var buffer = new MemoryStream();
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartArray();
            var count = 0;
            var unsupported = 0;
            var authenticated = 0;
            var dns = 0;
            foreach (var group in root.GetProperty("proxies").EnumerateObject())
            {
                if (!countries.Contains(group.Name) || !groups.Add(group.Name) || group.Value.ValueKind != JsonValueKind.Array)
                    throw InvalidFormat();
                foreach (var row in group.Value.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.String) throw InvalidFormat();
                    var endpoint = row.GetString()!;
                    // HTTPS is not established by this publisher's captured contract.
                    // Reject new ambiguous schemes rather than choosing CONNECT or TLS.
                    if (!(endpoint.StartsWith("http://", StringComparison.Ordinal) ||
                        endpoint.StartsWith("socks4://", StringComparison.Ordinal) ||
                        endpoint.StartsWith("socks5://", StringComparison.Ordinal)))
                        throw InvalidFormat();
                    if (endpoint.Contains('@', StringComparison.Ordinal) ||
                        !ProxyParser.TryParseEndpoint(endpoint, ProxyProtocol.Http, out _))
                    {
                        var held = ReadUnsupportedProfile(endpoint);
                        unsupported++;
                        if (held.Authenticated) authenticated++;
                        if (held.Dns) dns++;
                        continue;
                    }
                    writer.WriteStringValue(endpoint);
                    count++;
                }
            }
            if (count == 0 || !groups.SetEquals(countries)) throw InvalidFormat();
            writer.WriteEndArray();
            writer.Flush();
            return new OuroGateExtraction(
                Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)),
                count, unsupported, authenticated, dns);
        }
        catch (JsonException) { throw InvalidFormat(); }
    }

    private static (bool Authenticated, bool Dns) ReadUnsupportedProfile(string endpoint)
    {
        var start = endpoint.IndexOf("://", StringComparison.Ordinal) + 3;
        var authority = endpoint[start..];
        if (authority.Length == 0 || authority.Any(char.IsWhiteSpace) ||
            authority.IndexOfAny(['/', '\\', '?', '#']) >= 0 ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535)
            throw InvalidFormat();
        var at = authority.IndexOf('@');
        var authenticated = at >= 0;
        if (authenticated)
        {
            if (at != authority.LastIndexOf('@')) throw InvalidFormat();
            var userInfo = authority[..at];
            var colon = userInfo.IndexOf(':');
            if (colon <= 0 || colon == userInfo.Length - 1) throw InvalidFormat();
            for (var index = 0; index < userInfo.Length; index++)
            {
                if (userInfo[index] != '%') continue;
                if (index + 2 >= userInfo.Length || !Uri.IsHexDigit(userInfo[index + 1]) || !Uri.IsHexDigit(userInfo[index + 2]))
                    throw InvalidFormat();
                index += 2;
            }
            authority = authority[(at + 1)..];
        }
        if (!authority.EndsWith(":" + uri.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw InvalidFormat();
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            // Validate the original authority, including canonical spelling and public IP
            // checks. Never turn user:password@IP into an unauthenticated candidate.
            if (!authenticated || !ProxyParser.TryParseEndpoint(endpoint[..start] + authority, ProxyProtocol.Http, out _))
                throw InvalidFormat();
            return (true, false);
        }
        if (uri.HostNameType != UriHostNameType.Dns || IPAddress.TryParse(uri.Host, out _) ||
            !uri.Host.Contains('.', StringComparison.Ordinal) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
            throw InvalidFormat();
        // DNS candidates require a separate protected profile and validation path.
        // Counting them performs no DNS lookup and does not publish a guessed IP.
        return (authenticated, true);
    }

    private static bool IsCountry(string value) => value.Length == 2 && value.All(c => c is >= 'A' and <= 'Z');
    private static InvalidDataException InvalidFormat() => new("Неизвестный формат публичной выгрузки OuroGate.");
}

internal sealed record OuroGateExtraction(
    string Content,
    int AcceptedRows,
    int UnsupportedProfiles,
    int AuthenticatedProfiles,
    int DnsProfiles);
