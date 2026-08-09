using System.Net;
using System.Text;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет multipart-контракт Telegram и повтор после rate limit без внешней сети.</summary>
public sealed class TelegramBackupSenderTests
{
    [Fact]
    public async Task RetriesBodyRateLimitAndSendsExpectedMultipartDocument()
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
    public async Task ReleasesResponseAndBackupFileBeforeRetryDelay()
    {
        var path = await TemporaryBackupAsync();
        try
        {
            var firstContent = new DisposeTrackingContent(
                """{"ok":false,"error_code":429,"parameters":{"retry_after":30}}""");
            var handler = new ResourceTrackingHandler(firstContent);
            using var client = new HttpClient(handler);
            var responseDisposed = false;
            var backupFileReleased = false;

            await TelegramBackupSender.SendAsync(
                client,
                path,
                "backup",
                "secret-token",
                "123456",
                CancellationToken.None,
                (delay, _) =>
                {
                    Assert.Equal(TimeSpan.FromSeconds(30), delay);
                    responseDisposed = firstContent.Disposed;
                    using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    backupFileReleased = true;
                    return Task.CompletedTask;
                });

            Assert.True(responseDisposed);
            Assert.True(backupFileReleased);
            Assert.Equal(2, handler.Attempts);
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

            var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                TelegramBackupSender.SendAsync(
                    client, path, "backup", "secret-token", "123456", CancellationToken.None));

            Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
            Assert.Equal(1, handler.Attempts);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task HttpSuccessWithoutOkConfirmationIsRetriedButNeverAccepted()
    {
        var path = await TemporaryBackupAsync();
        try
        {
            var handler = new MissingConfirmationHandler();
            using var client = new HttpClient(handler);

            var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                TelegramBackupSender.SendAsync(
                    client, path, "backup", "super-secret-token", "123456", CancellationToken.None));

            Assert.Equal(3, handler.Attempts);
            Assert.Null(exception.StatusCode);
            Assert.Contains("ok=true", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-token", exception.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BodyErrorCodeOnHttpSuccessIsTreatedAsPermanentRejection()
    {
        var path = await TemporaryBackupAsync();
        try
        {
            var handler = new BodyRejectionHandler();
            using var client = new HttpClient(handler);

            var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                TelegramBackupSender.SendAsync(
                    client, path, "backup", "secret-token", "123456", CancellationToken.None));

            Assert.Equal(1, handler.Attempts);
            Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CallerCancellationIsNotRetriedOrConvertedToDeliveryFailure()
    {
        var path = await TemporaryBackupAsync();
        try
        {
            var handler = new BlockingHandler();
            using var client = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();

            var request = TelegramBackupSender.SendAsync(
                client, path, "backup", "secret-token", "123456", cancellation.Token);
            await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(1, handler.Attempts);
        }
        finally { File.Delete(path); }
    }

    private static async Task<string> TemporaryBackupAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyharbor-telegram-{Guid.NewGuid():N}.phbackup");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        return path;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

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
                return JsonResponse(HttpStatusCode.OK,
                    """{"ok":false,"error_code":429,"parameters":{"retry_after":0}}""");
            }
            return JsonResponse(HttpStatusCode.OK, """{"ok":true,"result":{}}""");
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
            return JsonResponse(HttpStatusCode.OK, """{"ok":true,"result":{}}""");
        }
    }

    private sealed class ResourceTrackingHandler(DisposeTrackingContent firstContent) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(Attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = firstContent }
                : JsonResponse(HttpStatusCode.OK, """{"ok":true,"result":{}}"""));
        }
    }

    private sealed class DisposeTrackingContent(string json)
        : ByteArrayContent(Encoding.UTF8.GetBytes(json))
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
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
            return Task.FromResult(JsonResponse(
                HttpStatusCode.BadRequest, """{"ok":false,"error_code":400,"description":"bad request"}"""));
        }
    }

    private sealed class MissingConfirmationHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK, """{"parameters":{"retry_after":0}}"""));
        }
    }

    private sealed class BodyRejectionHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK, """{"ok":false,"error_code":400,"description":"rejected"}"""));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        public TaskCompletionSource<bool> RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            RequestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(HttpStatusCode.OK, """{"ok":true,"result":{}}""");
        }
    }
}
