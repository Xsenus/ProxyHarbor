using System.Net;
using System.Net.Http.Headers;

namespace ProxyHarbor.Infrastructure;

/// <summary>Надёжно отправляет один backup-документ в Telegram с обработкой 429 и временных 5xx.</summary>
internal static class TelegramBackupSender
{
    private const int MaxAttempts = 3;

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
                if (response.IsSuccessStatusCode) return;

                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500;
                if (!retryable || attempt == MaxAttempts)
                    throw new HttpRequestException(
                        $"Telegram отклонил backup: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                        null,
                        response.StatusCode);

                await DelayBeforeRetryAsync(response.Headers.RetryAfter, attempt, token);
            }
            catch (Exception exception) when (IsTransientTransportFailure(exception, token) && attempt < MaxAttempts)
            {
                await DelayBeforeRetryAsync(null, attempt, token);
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
        exception is HttpRequestException { StatusCode: null } ||
        (exception is OperationCanceledException && !token.IsCancellationRequested);

    private static async Task DelayBeforeRetryAsync(
        RetryConditionHeaderValue? retryAfter,
        int attempt,
        CancellationToken token)
    {
        var serverDelay = retryAfter?.Delta;
        if (serverDelay is null && retryAfter?.Date is { } retryDate)
            serverDelay = retryDate - DateTimeOffset.UtcNow;
        var delay = serverDelay ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 30_000));
        if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
    }
}
