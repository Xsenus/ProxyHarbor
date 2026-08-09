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

    [Fact]
    public async Task RetriesTransientTransportFailureWithFreshFileStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyharbor-telegram-{Guid.NewGuid():N}.phbackup");
        var payload = new byte[] { 10, 20, 30, 40 };
        await File.WriteAllBytesAsync(path, payload);
        try
        {
            var handler = new TransientFailureHandler();
            using var client = new HttpClient(handler);

            await TelegramBackupSender.SendAsync(
                client, path, "backup", "secret-token", "123456", CancellationToken.None);

            Assert.Equal(2, handler.Attempts);
            Assert.True(handler.LastMultipartLength > payload.Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FinalTransportErrorDoesNotExposeBotToken()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyharbor-telegram-{Guid.NewGuid():N}.phbackup");
        await File.WriteAllBytesAsync(path, [1]);
        try
        {
            using var client = new HttpClient(new AlwaysFailingHandler());

            var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                TelegramBackupSender.SendAsync(
                    client, path, "backup", "super-secret-token", "123456", CancellationToken.None));

            Assert.DoesNotContain("super-secret-token", exception.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DoesNotRetryPermanentTelegramRejection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyharbor-telegram-{Guid.NewGuid():N}.phbackup");
        await File.WriteAllBytesAsync(path, [1]);
        try
        {
            var handler = new PermanentRejectionHandler();
            using var client = new HttpClient(handler);

            var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                TelegramBackupSender.SendAsync(
                    client, path, "backup", "secret-token", "123456", CancellationToken.None));

            Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
            Assert.Equal(1, handler.Attempts);
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

    private sealed class TransientFailureHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        public int LastMultipartLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
                throw new HttpRequestException($"Temporary failure for {request.RequestUri}");

            LastMultipartLength = (await request.Content!.ReadAsByteArrayAsync(cancellationToken)).Length;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class AlwaysFailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Sensitive request URI: {request.RequestUri}");
    }

    private sealed class PermanentRejectionHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
    }
}
