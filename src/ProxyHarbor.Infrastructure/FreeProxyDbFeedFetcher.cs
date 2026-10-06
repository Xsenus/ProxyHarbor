using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProxyHarbor.Infrastructure;

/// <summary>Полный публичный HTTP/SOCKS API список, а не ограниченная subscription-выборка.</summary>
internal static class FreeProxyDbFeedFetcher
{
    internal const string Url = "https://freeproxydb.com/api/proxy/search?protocol=http,socks4,socks5&page_size=100&page_index=1&order_by=id&order_dir=desc";
    internal static bool Supports(string url) => string.Equals(url, Url, StringComparison.Ordinal);

    internal static async Task<SourceFetchResult> FetchAsync(
        int maximumBytes,
        Func<string, CancellationToken, Task<SourceFetchResult>> fetchPage,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteStartArray("data");
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        long received = 0;
        long bodyBytes = 0;
        // Storage and HTTP limits remain explicit. Exceeding a limit is a failure,
        // never a successful prefix. Periodic refresh can observe a changing list;
        // an API without a snapshot token cannot promise an atomic remote snapshot.
        for (var page = 1; page <= 10_000; page++)
        {
            token.ThrowIfCancellationRequested();
            var url = Url.Replace("page_index=1&", $"page_index={page}&", StringComparison.Ordinal);
            var response = await fetchPage(url, token);
            if (response.NotModified || response.Content is null)
                throw new InvalidDataException("API-страница вернула 304 без полного списка.");
            bodyBytes += Encoding.UTF8.GetByteCount(response.Content);
            if (bodyBytes > maximumBytes)
                throw new InvalidDataException("Полный API-список превышает лимит размера источника.");
            using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number ||
                !status.TryGetInt32(out var code) || code != 1 ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("total_count", out var count) || count.ValueKind != JsonValueKind.Number ||
                !count.TryGetInt64(out var total) || total < 0 ||
                !data.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array ||
                rows.GetArrayLength() > 100)
                throw new InvalidDataException("API вернул некорректную структуру страницы.");
            var rowCount = rows.GetArrayLength();
            if (rowCount == 0 && received < total)
                throw new InvalidDataException("API завершил страницы до заявленного конца списка.");
            var ids = new List<long>(rowCount);
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var value) || value <= 0)
                    throw new InvalidDataException("API-страница содержит некорректный идентификатор записи.");
                ids.Add(value);
            }
            ids.Sort();
            var pageHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ids)));
            if (rowCount > 0 && !seenPages.Add(pageHash))
                throw new InvalidDataException("API повторяет страницу вместо продолжения списка.");
            foreach (var row in rows.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (row.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("API-страница содержит некорректную запись.");
                row.WriteTo(writer);
                if (writer.BytesCommitted + writer.BytesPending > maximumBytes)
                    throw new InvalidDataException("Полный API-список превышает лимит размера источника.");
            }
            received += rowCount;
            if (received < total) continue;
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            if (output.Length > maximumBytes)
                throw new InvalidDataException("Полный API-список превышает лимит размера источника.");
            // Validators of one page cannot authenticate the assembled representation.
            return new SourceFetchResult(Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length)),
                NotModified: false, HttpETag: null, HttpLastModifiedAt: null);
        }
        throw new InvalidDataException("API превысил лимит страниц полного списка.");
    }
}

internal sealed class SourceRateLimitException(DateTimeOffset retryNotBefore, System.Net.HttpStatusCode statusCode)
    : HttpRequestException("Источник ограничил частоту запросов; следующая попытка отложена.", null, statusCode)
{
    internal DateTimeOffset RetryNotBefore { get; } = retryNotBefore;
}
