using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Canonical public endpoint and stable publisher identities for the paginated community list.</summary>
internal static class RoundProxiesPublicApi
{
    internal const string Url = "https://roundproxies.com/api/get-free-proxies/?limit=500&page=1&sort_by=lastChecked&sort_type=asc";
    internal const string Origin = "https://roundproxies.com";
    internal const int PageSize = 500;
    internal static bool Supports(string url) => string.Equals(url, Url, StringComparison.Ordinal);
    internal static bool IsApiUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.TrimEnd('.').Equals("roundproxies.com", StringComparison.OrdinalIgnoreCase) &&
        Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').Equals("/api/get-free-proxies", StringComparison.OrdinalIgnoreCase);

    internal static (long Total, JsonElement Rows) InspectPage(JsonElement root, int pageIndex)
    {
        if (!root.TryGetProperty("total", out var count) || count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt64(out var total) || total is < 0 or > FreeProxyDbPageCapture.MaximumPages * PageSize ||
            !root.TryGetProperty("page", out var page) || page.ValueKind != JsonValueKind.Number || !page.TryGetInt32(out var number) || number != pageIndex ||
            !root.TryGetProperty("limit", out var limit) || limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var size) || size != PageSize ||
            !root.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > PageSize)
            throw new InvalidDataException("Public API page metadata is inconsistent.");
        return (total, rows);
    }

    internal static string RecordKey(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("_id", out var id) || id.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty("ip", out var address) || address.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty("port", out var port) || port.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
            !row.TryGetProperty("protocols", out var protocols) || protocols.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Public API record has no stable endpoint identity.");
        var value = id.GetString()!;
        if (value.Length is < 1 or > 256 || value.Any(char.IsControl) || address.GetString()!.Length > 512 ||
            port.GetRawText().Length > 64 || protocols.GetArrayLength() > 16 ||
            protocols.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String || p.GetString()!.Length > 64))
            throw new InvalidDataException("Public API record identity exceeds safe limits.");
        // The entire opaque publisher ID survives restarts; endpoint safety remains with the common parser.
        return "r:" + value;
    }
}
