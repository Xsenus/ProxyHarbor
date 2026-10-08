using System.Globalization;
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
                    if (endpoint.Contains('@', StringComparison.Ordinal) || !ProxyParser.TryParseEndpoint(endpoint, ProxyProtocol.Http, out _))
                        throw InvalidFormat();
                    writer.WriteStringValue(endpoint);
                    count++;
                }
            }
            if (count == 0 || !groups.SetEquals(countries)) throw InvalidFormat();
            writer.WriteEndArray();
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }
        catch (JsonException) { throw InvalidFormat(); }
    }

    private static bool IsCountry(string value) => value.Length == 2 && value.All(c => c is >= 'A' and <= 'Z');
    private static InvalidDataException InvalidFormat() => new("Неизвестный формат публичной выгрузки OuroGate.");
}
