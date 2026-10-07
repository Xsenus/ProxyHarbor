using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Validated API pages, including their actual observation times. A pause never
/// publishes a partial representation; reconciliation recovers insertions at the head.
/// Persistence owns committing each returned checkpoint before another HTTP request.
/// </summary>
internal sealed class FreeProxyDbPageCapture
{
    internal const string VpnUrl = "https://freeproxydb.com/api/proxy/search?protocol=vless,vmess,trojan,ss,hysteria2&page_size=100&page_index=1&order_by=id&order_dir=desc";
    internal const string MtProtoUrl = "https://freeproxydb.com/api/proxy/search?protocol=mtproto&page_size=100&page_index=1&order_by=id&order_dir=desc";
    internal const int MaximumPages = 10_000;
    internal const int MaximumPageBytes = 2_000_000;
    internal const int MaximumCaptureBytes = 32 * 1024 * 1024;
    private readonly FreeProxyDbCapturedPage[] _pages;

    internal FreeProxyDbPageCapture() : this([]) { }
    private FreeProxyDbPageCapture(FreeProxyDbCapturedPage[] pages) => _pages = pages;
    internal IReadOnlyList<FreeProxyDbCapturedPage> Pages => Array.AsReadOnly(_pages);

    internal static bool Supports(string url) =>
        SupportsHttp(url) || SupportsVpn(url);
    internal static bool SupportsHttp(string url) => FreeProxyDbFeedFetcher.Supports(url) || ProxiwarePublicApi.Supports(url) || RoundProxiesPublicApi.Supports(url) || Socks5ProxiesPublicApi.Supports(url) || ProxoraPublicApi.Supports(url) || ProxyScrapePublicApi.Supports(url);
    internal static bool SupportsVpn(string url) => string.Equals(url, VpnUrl, StringComparison.Ordinal) ||
        string.Equals(url, MtProtoUrl, StringComparison.Ordinal);
    internal static bool IsSearchUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("freeproxydb.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.TrimEnd('/').Equals("/api/proxy/search", StringComparison.Ordinal);
    internal static bool IsApiOriginUrl(string url) => ProxyScrapePublicApi.IsApiUrl(url) || ProxoraPublicApi.IsApiUrl(url) || Socks5ProxiesPublicApi.IsApiUrl(url) || RoundProxiesPublicApi.IsApiUrl(url) || ProxiwarePublicApi.IsOriginUrl(url) || (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.TrimEnd('.').Equals("freeproxydb.com", StringComparison.OrdinalIgnoreCase));

    internal FreeProxyDbCaptureStatus Inspect(int maximumBytes, string? sourceUrl = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, MaximumCaptureBytes);
        if (sourceUrl is not null && !Supports(sourceUrl)) throw InvalidCapture();
        int? provider = sourceUrl is null ? null : ProxiwarePublicApi.Supports(sourceUrl) ? 1 : RoundProxiesPublicApi.Supports(sourceUrl) ? 2 : Socks5ProxiesPublicApi.Supports(sourceUrl) ? 3 : ProxoraPublicApi.Supports(sourceUrl) ? 4 : ProxyScrapePublicApi.Supports(sourceUrl) ? 5 : 0;
        var rows = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var traversalIds = new HashSet<string>(StringComparer.Ordinal);
        var pageHashes = new HashSet<string>(StringComparer.Ordinal);
        var headHashes = new HashSet<string>(StringComparer.Ordinal);
        var nextPage = 1;
        var head = false;
        var complete = false;
        long received = 0;
        long bodyBytes = 0;
        long total = 0;
        DateTimeOffset? observedAt = null;
        foreach (var page in _pages)
        {
            if (complete || page.PageIndex != nextPage || page.Reconciliation != head ||
                page.CapturedAt.Offset != TimeSpan.Zero || page.CapturedAt < DateTimeOffset.UnixEpoch ||
                page.CapturedAt > DateTimeOffset.UtcNow.AddDays(1) ||
                page.PageIndex is < 1 or > MaximumPages)
                throw InvalidCapture();
            var bytes = Encoding.UTF8.GetByteCount(page.Content);
            bodyBytes += bytes;
            if (bytes > MaximumPageBytes || bodyBytes > maximumBytes) throw InvalidCapture();
            using var document = JsonDocument.Parse(page.Content, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidCapture();
            var currentProvider = root.TryGetProperty("total_records", out _) && root.TryGetProperty("proxies", out var proxyScrapeRows) && proxyScrapeRows.ValueKind == JsonValueKind.Array ? 5 :
                root.TryGetProperty("items", out var proxoraRows) && proxoraRows.ValueKind == JsonValueKind.Array && root.TryGetProperty("per_page", out _) ? 4 :
                root.TryGetProperty("total_proxies", out _) ? 1 :
                root.TryGetProperty("total", out _) && root.TryGetProperty("data", out var providerData) && providerData.ValueKind == JsonValueKind.Array ? 2 :
                root.TryGetProperty("meta", out var providerMeta) && providerMeta.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var offsetRows) && offsetRows.ValueKind == JsonValueKind.Array ? 3 : 0;
            if (provider is not null && currentProvider != provider) throw InvalidCapture();
            provider = currentProvider;
            JsonElement pageRows;
            if (currentProvider is 1 or 2 or 3 or 4 or 5)
            {
                if ((root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
                    (root.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.Number &&
                        state.TryGetInt32(out var stateCode) && stateCode == 0) ||
                    (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null &&
                        (error.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(error.GetString()))))
                    throw InvalidCapture();
                if (currentProvider == 5) (total, pageRows) = ProxyScrapePublicApi.InspectPage(root, page.PageIndex);
                else if (currentProvider == 4) (total, pageRows) = ProxoraPublicApi.InspectPage(root, page.PageIndex);
                else if (currentProvider == 3) (total, pageRows) = Socks5ProxiesPublicApi.InspectPage(root, page.PageIndex);
                else if (currentProvider == 2) (total, pageRows) = RoundProxiesPublicApi.InspectPage(root, page.PageIndex);
                else if (root.GetProperty("total_proxies").ValueKind != JsonValueKind.Number ||
                    !root.GetProperty("total_proxies").TryGetInt64(out total) ||
                    total is < 0 or > MaximumPages * 100 ||
                    !root.TryGetProperty("proxies", out pageRows) || pageRows.ValueKind != JsonValueKind.Array ||
                    pageRows.GetArrayLength() > 100) throw InvalidCapture();
            }
            else
            {
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number ||
                    !status.TryGetInt32(out var code) || code != 1 ||
                    !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("total_count", out var count) || count.ValueKind != JsonValueKind.Number ||
                    !count.TryGetInt64(out total) ||
                    total is < 0 or > MaximumPages * 100 ||
                    !data.TryGetProperty("data", out pageRows) || pageRows.ValueKind != JsonValueKind.Array ||
                    pageRows.GetArrayLength() > 100)
                    throw InvalidCapture();
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in pageRows.EnumerateArray())
            {
                if (!ids.Add(RecordKey(row, currentProvider))) throw InvalidCapture();
            }
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ids.Order(StringComparer.Ordinal))));
            if (ids.Count > 0 && !(head ? headHashes : pageHashes).Add(hash)) throw InvalidCapture();
            if (currentProvider != 5 && ids.Count == 0 && (head ? rows.Count < total : received < total)) throw InvalidCapture();
            var overlapsTraversal = ids.Overlaps(traversalIds);
            foreach (var row in pageRows.EnumerateArray())
            {
                var id = RecordKey(row, currentProvider);
                // A reconciled head was observed later, so it supersedes captured rows.
                rows[id] = row.Clone();
                if (!head) traversalIds.Add(id);
            }
            observedAt = observedAt is null || page.CapturedAt < observedAt ? page.CapturedAt : observedAt;
            if (head)
            {
                complete = currentProvider == 5 ? overlapsTraversal || ids.Count == 0 : (overlapsTraversal || total == 0) && rows.Count >= total;
                nextPage++;
            }
            else
            {
                received += ids.Count;
                nextPage++;
                if (currentProvider == 5 ? ids.Count == 0 : received >= total)
                {
                    head = true;
                    nextPage = 1;
                }
            }
        }
        if (!complete && nextPage > MaximumPages) throw InvalidCapture();
        return new FreeProxyDbCaptureStatus(nextPage, head, complete, observedAt, rows);
    }

    internal FreeProxyDbPageCapture Append(FreeProxyDbCapturedPage page, int maximumBytes)
    {
        if (_pages.Length >= MaximumPages * 2) throw InvalidCapture();
        var next = new FreeProxyDbPageCapture([.. _pages, page]);
        _ = next.Inspect(maximumBytes);
        return next;
    }

    internal static FreeProxyDbPageCapture Restore(FreeProxyDbCapturedPage[] pages, int maximumBytes)
    {
        if (pages.Length > MaximumPages * 2) throw InvalidCapture();
        var capture = new FreeProxyDbPageCapture((FreeProxyDbCapturedPage[])pages.Clone());
        _ = capture.Inspect(maximumBytes);
        return capture;
    }

    /// <summary>Successful checkpoint callback must finish before the next request.</summary>
    internal static async Task<FreeProxyDbCaptureAdvance> AdvanceAsync(
        FreeProxyDbPageCapture capture, string url, int maximumBytes, int maximumNetworkPages,
        Func<string, CancellationToken, Task<SourceFetchResult>> fetchPage,
        Func<FreeProxyDbPageCapture, CancellationToken, Task> commitCheckpoint,
        CancellationToken token)
    {
        if (!Supports(url)) throw new ArgumentException("Unsupported public API URL.", nameof(url));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNetworkPages);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumNetworkPages, 100);
        var status = capture.Inspect(maximumBytes, url);
        for (var request = 0; request < maximumNetworkPages && !status.Complete; request++)
        {
            token.ThrowIfCancellationRequested();
            var pageUrl = ProxyScrapePublicApi.Supports(url)
                ? url.Replace("skip=0&", $"skip={(status.NextPage - 1) * ProxyScrapePublicApi.PageSize}&", StringComparison.Ordinal)
                : Socks5ProxiesPublicApi.Supports(url)
                ? url.Replace("offset=0&", $"offset={(status.NextPage - 1) * Socks5ProxiesPublicApi.PageSize}&", StringComparison.Ordinal)
                : ProxiwarePublicApi.Supports(url) || RoundProxiesPublicApi.Supports(url) || ProxoraPublicApi.Supports(url)
                ? url.Replace("page=1&", $"page={status.NextPage}&", StringComparison.Ordinal)
                : url.Replace("page_index=1&", $"page_index={status.NextPage}&", StringComparison.Ordinal);
            var response = await fetchPage(pageUrl, token);
            if (response.NotModified || response.Content is null) throw InvalidCapture();
            var content = ProxyScrapePublicApi.Supports(url)
                ? ProxyScrapePublicApi.CompactPage(response.Content, status.NextPage) : Socks5ProxiesPublicApi.Supports(url)
                ? Socks5ProxiesPublicApi.CompactPage(response.Content, status.NextPage) : ProxoraPublicApi.Supports(url)
                ? ProxoraPublicApi.CompactPage(response.Content, status.NextPage) : response.Content;
            var next = capture.Append(new FreeProxyDbCapturedPage(status.NextPage,
                status.Reconciliation, DateTimeOffset.UtcNow, content), maximumBytes);
            _ = next.Inspect(maximumBytes, url);
            await commitCheckpoint(next, token);
            capture = next;
            status = capture.Inspect(maximumBytes, url);
        }
        if (!status.Complete) return new(capture, null, null);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("data");
            foreach (var row in status.Rows.OrderByDescending(item => item.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (ProxiwarePublicApi.Supports(url)) ProxiwarePublicApi.WriteRecord(writer, row.Value);
                else row.Value.WriteTo(writer);
                if (writer.BytesCommitted + writer.BytesPending > maximumBytes) throw InvalidCapture();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (output.Length > maximumBytes) throw InvalidCapture();
        return new(capture, Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length)), status.ObservedAt);
    }

    private static InvalidDataException InvalidCapture() =>
        new("Сохранённые API-страницы некорректны, повторяются или превышают безопасный лимит.");

    private static string RecordKey(JsonElement row, int provider)
    {
        if (provider == 1) return ProxiwarePublicApi.RecordKey(row);
        if (provider == 2) return RoundProxiesPublicApi.RecordKey(row);
        if (provider == 3) return Socks5ProxiesPublicApi.RecordKey(row);
        if (provider == 4) return ProxoraPublicApi.RecordKey(row);
        if (provider == 5) return ProxyScrapePublicApi.RecordKey(row);
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var value) || value <= 0)
            throw InvalidCapture();
        return "f:" + value.ToString("D19", System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed record FreeProxyDbCapturedPage(int PageIndex, bool Reconciliation, DateTimeOffset CapturedAt, string Content);
internal sealed record FreeProxyDbCaptureStatus(int NextPage, bool Reconciliation, bool Complete,
    DateTimeOffset? ObservedAt, IReadOnlyDictionary<string, JsonElement> Rows);
internal sealed record FreeProxyDbCaptureAdvance(FreeProxyDbPageCapture Capture, string? Content, DateTimeOffset? ObservedAt);
