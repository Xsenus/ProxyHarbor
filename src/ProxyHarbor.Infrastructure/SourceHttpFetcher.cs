using System.Net.Http.Headers;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Единый защищённый HTTP transport для публичных proxy/VPN feed: SSRF-проверка
/// каждого redirect, bounded body, conditional validators и ограниченные retry.
/// </summary>
internal static class SourceHttpFetcher
{
    internal static async Task<SourceFetchResult> FetchAsync(
        HttpClient client,
        string url,
        string? httpETag,
        DateTimeOffset? httpLastModifiedAt,
        int maximumBytes,
        int timeoutSeconds,
        int retryCount,
        CancellationToken token,
        Action<string?>? ensureSupportedMediaType = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        delayAsync ??= static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);
        var retries = Math.Clamp(retryCount, 0, 5);
        // Старые версии могли сохранить PostgreSQL infinity, а недоверенный feed —
        // прислать далёкое будущее. Такое значение не имеет права авторизовать 304.
        var requestLastModifiedAt = NormalizeLastModified(httpLastModifiedAt, DateTimeOffset.UtcNow);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, timeoutSeconds)));
                using var response = await GetWithSafeRedirectsAsync(
                    client, url, httpETag, requestLastModifiedAt, timeout.Token);
                if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt < retries)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ??
                        response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ??
                        TimeSpan.FromMilliseconds(400 * (attempt + 1));
                    var delayMilliseconds = Math.Clamp(retryAfter.TotalMilliseconds, 0, 4_750) +
                        Random.Shared.Next(50, 250);
                    // ResponseHeadersRead не потребляет body. Освобождаем response до backoff,
                    // иначе один transient feed удерживает connection-pool slot всё время паузы.
                    response.Dispose();
                    await delayAsync(TimeSpan.FromMilliseconds(delayMilliseconds), token);
                    continue;
                }

                var responseETag = GetBoundedETag(response);
                var responseLastModifiedAt = NormalizeLastModified(
                    response.Content.Headers.LastModified, DateTimeOffset.UtcNow);
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    if (httpETag is null && requestLastModifiedAt is null)
                        throw new InvalidDataException(
                            "Источник вернул 304 без отправленного conditional validator.");
                    return new SourceFetchResult(
                        Content: null,
                        NotModified: true,
                        responseETag ?? httpETag,
                        responseLastModifiedAt ?? requestLastModifiedAt);
                }

                response.EnsureSuccessStatusCode();
                ensureSupportedMediaType?.Invoke(response.Content.Headers.ContentType?.MediaType);
                if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
                    throw new InvalidOperationException(
                        $"Источник превышает лимит {FormatByteLimit(maximumBytes)}.");
                return new SourceFetchResult(
                    await ReadLimitedAsync(response.Content, maximumBytes, timeout.Token),
                    NotModified: false,
                    responseETag,
                    responseLastModifiedAt);
            }
            catch (Exception exception) when (
                attempt < retries && SourceHttpRetry.IsRetryable(exception, token))
            {
                await delayAsync(
                    TimeSpan.FromMilliseconds(400 * (attempt + 1) + Random.Shared.Next(50, 250)), token);
            }
        }
    }

    private static async Task<HttpResponseMessage> GetWithSafeRedirectsAsync(
        HttpClient client,
        string url,
        string? httpETag,
        DateTimeOffset? httpLastModifiedAt,
        CancellationToken token)
    {
        EntityTagHeaderValue? parsedETag = null;
        if (httpETag is not null && !EntityTagHeaderValue.TryParse(httpETag, out parsedETag))
            throw new InvalidDataException("Сохранённый ETag источника имеет некорректный формат.");
        var current = new Uri(url, UriKind.Absolute);
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            if (!await NetworkSafety.IsSafePublicHttpsUrlAsync(current.AbsoluteUri, token))
                throw new HttpRequestException(
                    "Источник или его перенаправление ведёт в запрещённую сеть.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            // Conditional validators принадлежат representation исходного URI. Их
            // перенос на redirect-target способен раскрыть cross-origin ETag и дать
            // ложный 304, если владелец позже сменит Location на другой feed.
            if (redirect == 0)
            {
                if (parsedETag is not null) request.Headers.IfNoneMatch.Add(parsedETag);
                if (httpLastModifiedAt is not null) request.Headers.IfModifiedSince = httpLastModifiedAt;
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                if (redirect == 0) return response;
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    response.Dispose();
                    throw new InvalidDataException(
                        "Redirect-target вернул 304 без принадлежащего ему conditional validator.");
                }

                // Модель хранит validators по исходному Source.Url и не хранит effective
                // redirect URI. Не сохраняем ETag/Last-Modified другой representation.
                response.Headers.ETag = null;
                response.Content.Headers.LastModified = null;
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new HttpRequestException("Перенаправление источника не содержит Location.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new HttpRequestException("Источник превысил лимит в три перенаправления.");
    }

    private static string? GetBoundedETag(HttpResponseMessage response)
    {
        var value = response.Headers.ETag?.ToString();
        if (value is not null && (value.Length > 512 || value.Any(char.IsControl)))
            throw new InvalidDataException(
                "ETag источника превышает лимит или содержит управляющие символы.");
        return value;
    }

    /// <summary>
    /// HTTP cache date хранится только в UTC и не может быть PostgreSQL infinity,
    /// доэпохальным либо более чем на сутки опережать часы collector'а.
    /// </summary>
    private static DateTimeOffset? NormalizeLastModified(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is null) return null;
        var utc = value.Value.ToUniversalTime();
        var latest = now.ToUniversalTime().AddDays(1);
        return utc >= DateTimeOffset.UnixEpoch && utc <= latest ? utc : null;
    }

    private static async Task<string> ReadLimitedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0)
                return System.Text.Encoding.UTF8.GetString(
                    output.GetBuffer(), 0, checked((int)output.Length));
            if (output.Length + read > maximumBytes)
                throw new InvalidOperationException(
                    $"Источник превышает лимит {FormatByteLimit(maximumBytes)}.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static string FormatByteLimit(int bytes) => bytes % (1024 * 1024) == 0
        ? $"{bytes / (1024 * 1024)} MiB"
        : $"{bytes:N0} байт";
}
