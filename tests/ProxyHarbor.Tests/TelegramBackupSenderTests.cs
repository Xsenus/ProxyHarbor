using System.Net;
using System.Net.Http.Headers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет multipart-контракт Telegram и повтор после rate limit без внешней сети.</summary>
public sealed class TelegramBackupSenderTests
{
    [Fact]
    public async Task RetriesRateLimitAndSendsExpectedMultipartDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyharbor-telegram-{Guid.NewGuid():N}.phbackup");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
        try
        {
            var handler = new RecordingHandler();
            using var client = new HttpClient(handler);

            await TelegramBackupSender.SendAsync(
                client, path, "backup caption", "secret-token", "123456", CancellationToken.None);

            Assert.Equal(2, handler.Attempts);
            Assert.Equal("https://api.telegram.org/botsecret-token/sendDocument", handler.RequestUri);
            Assert.Contains("name=chat_id", handler.MultipartBody);
            Assert.Contains("123456", handler.MultipartBody);
            Assert.Contains("name=caption", handler.MultipartBody);
            Assert.Contains("backup caption", handler.MultipartBody);
            Assert.Contains(Path.GetFileName(path), handler.MultipartBody);
        }
        finally { File.Delete(path); }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        public string? RequestUri { get; private set; }
        public string MultipartBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            RequestUri = request.RequestUri?.AbsoluteUri;
            MultipartBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Attempts == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
