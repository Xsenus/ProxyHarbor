using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyHarbor.Infrastructure;

/// <summary>Надёжно отправляет один backup-документ в Telegram с обработкой 429 и временных 5xx.</summary>
internal static class TelegramBackupSender
{
    private const int MaxAttempts = 3;
    private const int MaxResponseBytes = 64 * 1024;

    internal static async Task SendAsync(
        HttpClient client,
        string path,
        string caption,
        string botToken,
        string chatId,
        CancellationToken token)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                // Multipart и поток файла создаются заново: после неудачной отправки их нельзя
                // безопасно переиспользовать, особенно если сервер прочитал только часть тела.
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(chatId), "chat_id");
                form.Add(new StringContent(caption), "caption");
                await using var stream = File.OpenRead(path);
                using var file = new StreamContent(stream);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(file, "document", Path.GetFileName(path));
                using var response = await client.PostAsync($"https://api.telegram.org/bot{botToken}/sendDocument", form, token);
                var apiResponse = await ReadApiResponseAsync(response.Content, token);
                // Bot API определяет успех одновременно транспортным 2xx и JSON-полем ok=true.
                // Одного HTTP-кода недостаточно для подтверждения фактической доставки документа.
                if (response.IsSuccessStatusCode && apiResponse?.Ok == true) return;

                var retryable = IsRetryableResponse(response.StatusCode, apiResponse);
                if (!retryable || attempt == MaxAttempts)
                    throw DeliveryRejected(response.StatusCode, apiResponse);

                await DelayBeforeRetryAsync(
                    response.Headers.RetryAfter,
                    apiResponse?.Parameters?.RetryAfter,
                    attempt,
                    token);
            }
            catch (Exception exception) when (IsTransientTransportFailure(exception, token) && attempt < MaxAttempts)
            {
                await DelayBeforeRetryAsync(null, null, attempt, token);
            }
            catch (Exception exception) when (IsTransientTransportFailure(exception, token))
            {
                // Не вкладываем исходное исключение: некоторые handlers включают полный URI,
                // а URI Telegram содержит секретный bot token.
                throw new HttpRequestException("Telegram недоступен после нескольких попыток отправки backup.");
            }
        }
    }

    private static bool IsTransientTransportFailure(Exception exception, CancellationToken token) =>
        (exception is HttpRequestException { StatusCode: null } && exception is not TelegramDeliveryException) ||
        exception is IOException ||
        (exception is OperationCanceledException && !token.IsCancellationRequested);

    private static bool IsRetryableResponse(HttpStatusCode statusCode, TelegramApiResponse? apiResponse)
    {
        var httpCode = (int)statusCode;
        if (httpCode == 429 || httpCode >= 500) return true;
        if (apiResponse?.ErrorCode is 429 or >= 500) return true;
        // Невалидный/слишком большой JSON при HTTP 2xx оставляет доставку неоднозначной:
        // повторяем ограниченное число раз, но никогда не записываем ложный успех.
        return httpCode is >= 200 and <= 299 && apiResponse?.Ok is null;
    }

    private static TelegramDeliveryException DeliveryRejected(
        HttpStatusCode statusCode,
        TelegramApiResponse? apiResponse)
    {
        var telegramCode = apiResponse?.ErrorCode;
        HttpStatusCode? effectiveStatus = !((int)statusCode is >= 200 and <= 299)
            ? statusCode
            : telegramCode is >= 100 and <= 599 ? (HttpStatusCode)telegramCode.Value : (HttpStatusCode?)null;
        var detail = telegramCode.HasValue
            ? $"HTTP {(int)statusCode}, Telegram {telegramCode.Value}"
            : $"HTTP {(int)statusCode}, подтверждение ok=true отсутствует";
        // Description намеренно не включается: внешний ответ не должен попадать в audit/log без фильтрации.
        return new TelegramDeliveryException(
            $"Telegram не подтвердил доставку backup: {detail}.", effectiveStatus);
    }

    private static async Task<TelegramApiResponse?> ReadApiResponseAsync(HttpContent content, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (output.Length <= MaxResponseBytes)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        try
        {
            return JsonSerializer.Deserialize<TelegramApiResponse>(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task DelayBeforeRetryAsync(
        RetryConditionHeaderValue? retryAfter,
        int? bodyRetryAfterSeconds,
        int attempt,
        CancellationToken token)
    {
        var serverDelay = retryAfter?.Delta;
        if (serverDelay is null && retryAfter?.Date is { } retryDate)
            serverDelay = retryDate - DateTimeOffset.UtcNow;
        if (serverDelay is null && bodyRetryAfterSeconds is >= 0)
            serverDelay = TimeSpan.FromSeconds(bodyRetryAfterSeconds.Value);
        var delay = serverDelay ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 30_000));
        if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
    }

    private sealed record TelegramApiResponse(
        [property: JsonPropertyName("ok")] bool? Ok,
        [property: JsonPropertyName("error_code")] int? ErrorCode,
        [property: JsonPropertyName("parameters")] TelegramResponseParameters? Parameters);

    private sealed record TelegramResponseParameters(
        [property: JsonPropertyName("retry_after")] int? RetryAfter);

    /// <summary>Отличает валидный отрицательный Bot API результат от сетевой ошибки без HTTP-кода.</summary>
    private sealed class TelegramDeliveryException(string message, HttpStatusCode? statusCode)
        : HttpRequestException(message, null, statusCode);
}
