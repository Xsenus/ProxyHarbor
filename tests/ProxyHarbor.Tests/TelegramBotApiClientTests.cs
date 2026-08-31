using System.Net;
using System.Text;
using System.Text.Json;
using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

public sealed class TelegramBotApiClientTests
{
    [Theory]
    [InlineData("123:TEST_ONLY_NOT_A_REAL_TOKEN", true)]
    [InlineData("123:test-token-long-enough", true)]
    [InlineData("123456789_AbCdEfGhIjKlMnOpQrStUvWxYz", false)]
    [InlineData("123:short", false)]
    [InlineData("123456789:token/with/slash-and-enough-length", false)]
    [InlineData("123456789:token with spaces and enough length", false)]
    public void TokenPolicyAcceptsOnlyBoundedPathSafeBotTokens(string value, bool expected) =>
        Assert.Equal(expected, TelegramTokenPolicy.IsValid(value));

    [Fact]
    public void TokenPolicyRejectsNullAndOversizedValues()
    {
        Assert.False(TelegramTokenPolicy.IsValid(null));
        Assert.False(TelegramTokenPolicy.IsValid($"1:{new string('a', 300)}"));
        Assert.False(TelegramTokenPolicy.IsValid("1:two:colons-and-enough-characters"));
    }

    [Theory]
    [InlineData(TelegramTransportModes.Auto, 3, true)]
    [InlineData(TelegramTransportModes.Proxy, 2, false)]
    [InlineData(TelegramTransportModes.Direct, 1, true)]
    public void TransportPolicyBuildsOrderedFailover(string mode, int expectedAttempts, bool endsWithDirect)
    {
        var first = new TelegramProxyOptions { Host = "first.example", Port = 1080 };
        var second = new TelegramProxyOptions { Host = "second.example", Port = 1080 };
        var attempts = TelegramBotApiClient.ConnectionAttempts(new TelegramBotOptions
        {
            TransportMode = mode,
            Proxies = [first, second]
        }).ToArray();

        Assert.Equal(expectedAttempts, attempts.Length);
        if (mode != TelegramTransportModes.Direct)
            Assert.Equal(new[] { first, second }, attempts.Take(2));
        Assert.Equal(endsWithDirect, attempts[^1] is null);
    }

    [Fact]
    public void TransportCircuitBreakerRecoversAfterCooldownAndSuccess()
    {
        var health = new TelegramTransportHealth();
        var proxy = new TelegramProxyOptions { Host = "proxy.example", Port = 1080 };
        var now = DateTimeOffset.UtcNow;

        Assert.True(health.IsAvailable(proxy, now));
        health.MarkFailed(proxy, now);
        Assert.False(health.IsAvailable(proxy, now.AddMinutes(2)));
        Assert.True(health.IsAvailable(proxy, now.AddMinutes(4)));
        health.MarkFailed(proxy, now);
        health.MarkSucceeded(proxy);
        Assert.True(health.IsAvailable(proxy, now));
    }

    [Fact]
    public async Task CandidateCacheCoalescesConcurrentRefreshAndExpiresAsOneSnapshot()
    {
        var now = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var cache = new TelegramProxyCandidateCache(TimeSpan.FromMinutes(1), () => now);
        var loaderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        async Task<TelegramProxyOptions[]> Load(CancellationToken _)
        {
            Interlocked.Increment(ref loads);
            loaderStarted.TrySetResult();
            await releaseLoader.Task;
            return [new TelegramProxyOptions { Host = "cached.example", Port = 1080 }];
        }

        var concurrent = Enumerable.Range(0, 12)
            .Select(_ => cache.GetOrLoadAsync(Load, CancellationToken.None)).ToArray();
        await loaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseLoader.SetResult();
        var snapshots = await Task.WhenAll(concurrent);

        Assert.Equal(1, loads);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        _ = await cache.GetOrLoadAsync(Load, CancellationToken.None);
        Assert.Equal(1, loads);

        now = now.AddMinutes(2);
        _ = await cache.GetOrLoadAsync(Load, CancellationToken.None);
        Assert.Equal(2, loads);
    }

    [Fact]
    public void ProxyClientPoolReusesMatchingRouteAndRemainsBounded()
    {
        using var pool = new TelegramProxyHttpClientPool();
        using var firstLease = pool.Acquire(new TelegramProxyOptions
        {
            Host = "PROXY.example",
            Port = 1080,
            Username = "user",
            Password = "secret"
        });
        using var sameLease = pool.Acquire(new TelegramProxyOptions
        {
            Host = "proxy.example",
            Port = 1080,
            Username = "user",
            Password = "secret"
        });
        using var changedCredentialsLease = pool.Acquire(new TelegramProxyOptions
        {
            Host = "proxy.example",
            Port = 1080,
            Username = "user",
            Password = "changed"
        });
        using var longUploadLease = pool.Acquire(new TelegramProxyOptions
        {
            Host = "proxy.example",
            Port = 1080,
            Username = "user",
            Password = "secret"
        }, TimeSpan.FromMinutes(5));

        Assert.Same(firstLease.Client, sameLease.Client);
        Assert.NotSame(firstLease.Client, changedCredentialsLease.Client);
        Assert.NotSame(firstLease.Client, longUploadLease.Client);
        Assert.Equal(TimeSpan.FromMinutes(5), longUploadLease.Client.Timeout);
        for (var index = 0; index < 40; index++)
            pool.Acquire(new TelegramProxyOptions { Host = $"192.0.2.{index + 1}", Port = 1080 }).Dispose();
        pool.Acquire(new TelegramProxyOptions { Host = "2001:db8::1", Port = 1080 }).Dispose();
        Assert.Equal(32, pool.Count);
    }

    [Fact]
    public void ProvisioningWorkerDetectsOnlyUnappliedReadyConfiguration()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new TelegramBotOptions
        {
            Enabled = true,
            BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN",
            BotId = 42,
            BotUsername = "ProxyHarborBot",
            WebhookSecret = "test-webhook-secret",
            UpdatedAt = now
        };

        Assert.True(TelegramProvisioningWorker.NeedsProvisioning(options));
        options.ProvisionedAt = now.AddMinutes(-1);
        Assert.True(TelegramProvisioningWorker.NeedsProvisioning(options));
        options.ProvisionedAt = now.AddSeconds(-1);
        Assert.False(TelegramProvisioningWorker.NeedsProvisioning(options));
        options.Enabled = false;
        options.ProvisionedAt = null;
        Assert.False(TelegramProvisioningWorker.NeedsProvisioning(options));
    }

    [Fact]
    public async Task GetMeUsesOfficialEndpointAndReadsIdentity()
    {
        using var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true,"result":{"id":42,"username":"proxy_harbor_bot"}}""", Encoding.UTF8, "application/json")
        });
        var identity = await new TelegramBotApiClient(factory).GetMeAsync("123:TEST_ONLY_NOT_A_REAL_TOKEN", CancellationToken.None);

        Assert.Equal(42, identity.Id);
        Assert.Equal("proxy_harbor_bot", identity.Username);
        Assert.Equal("https://api.telegram.org/bot123:TEST_ONLY_NOT_A_REAL_TOKEN/getMe", factory.LastRequestUri);
    }

    [Fact]
    public async Task PollingDeadlineBoundsTheWholeTransportAttempt()
    {
        using var factory = new RecordingFactory(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new TelegramBotApiClient(factory);

        var error = await Assert.ThrowsAsync<TelegramBotApiException>(() =>
            client.GetUpdatesAsync(
                new TelegramBotOptions { BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN" },
                0,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.Equal(504, error.ErrorCode);
        Assert.True(error.Transient);
        Assert.Contains("failover", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollingPreservesHostCancellation()
    {
        using var factory = new RecordingFactory(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TelegramBotApiClient(factory).GetUpdatesAsync(
                new TelegramBotOptions { BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN" },
                0,
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }

    [Fact]
    public async Task ApiErrorPreservesRetryAfterForPersistentQueue()
    {
        using var factory = new RecordingFactory(_ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("""{"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":7}}""", Encoding.UTF8, "application/json")
        });

        var error = await Assert.ThrowsAsync<TelegramBotApiException>(() =>
            new TelegramBotApiClient(factory).GetMeAsync("123:TEST_ONLY_NOT_A_REAL_TOKEN", CancellationToken.None));
        Assert.Equal(429, error.ErrorCode);
        Assert.Equal(7, error.RetryAfterSeconds);
        Assert.True(error.Transient);
    }

    [Theory]
    [InlineData(403, "{\"ok\":false,\"error_code\":403,\"description\":\"Forbidden\"}", true, false)]
    [InlineData(500, "not-json", false, true)]
    [InlineData(200, "{\"ok\":false,\"description\":\"Rejected\"}", false, false)]
    public async Task ApiFailuresAreClassifiedWithoutLeakingBodies(int status, string body, bool forbidden, bool transient)
    {
        using var factory = new RecordingFactory(_ => new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<TelegramBotApiException>(() =>
            new TelegramBotApiClient(factory).GetMeAsync("123:TEST_ONLY_NOT_A_REAL_TOKEN", CancellationToken.None));
        Assert.Equal(forbidden, error.Forbidden);
        Assert.Equal(transient, error.Transient);
    }

    [Fact]
    public async Task GetMeRejectsSuccessfulResponseWithoutUsername()
    {
        using var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"ok":true,"result":{"id":42}}""", Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<TelegramBotApiException>(() =>
            new TelegramBotApiClient(factory).GetMeAsync("123:TEST_ONLY_NOT_A_REAL_TOKEN", CancellationToken.None));
        Assert.Equal(502, error.ErrorCode);
    }

    [Fact]
    public async Task ControlOnlyTelegramDescriptionIsReplacedWithSafeMessage()
    {
        using var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        { Content = new StringContent("""{"ok":false,"error_code":502,"description":"\u0001"}""", Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<TelegramBotApiException>(() =>
            new TelegramBotApiClient(factory).GetMeAsync("123:TEST_ONLY_NOT_A_REAL_TOKEN", CancellationToken.None));
        Assert.Equal("Telegram Bot API отклонил запрос.", error.Message);
    }

    [Fact]
    public async Task ProvisionPollingRemovesWebhookAndAppliesCompleteProfile()
    {
        using var factory = new RecordingFactory(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true,"result":true}""", Encoding.UTF8, "application/json")
        });
        await new TelegramBotApiClient(factory).ProvisionAsync(new TelegramBotOptions
        {
            BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN",
            UpdateMode = TelegramUpdateModes.Polling,
            Name = "ProxyHarbor",
            Description = "Полное описание тестового бота.",
            ShortDescription = "Короткое описание",
            BotUsername = "ProxyHarborBot",
            WebhookSecret = "secret",
            PublicBaseUrl = "https://proxy.example.test"
        }, CancellationToken.None);
        Assert.Equal("https://api.telegram.org/bot123:TEST_ONLY_NOT_A_REAL_TOKEN/deleteWebhook", factory.LastRequestUri);
    }

    [Fact]
    public async Task SendMessageOmitsNullReplyMarkupAndPreservesKeyboardObjects()
    {
        using var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true,"result":{"message_id":17}}""", Encoding.UTF8, "application/json")
        });
        var client = new TelegramBotApiClient(factory);
        var options = new TelegramBotOptions { BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN" };

        Assert.Equal(17, await client.SendMessageAsync(options, 42, "Без кнопок", null, CancellationToken.None));
        using (var body = JsonDocument.Parse(factory.LastRequestBody!))
            Assert.False(body.RootElement.TryGetProperty("reply_markup", out _));

        var keyboard = new { inline_keyboard = new[] { new[] { new { text = "Открыть", callback_data = "open" } } } };
        Assert.Equal(17, await client.SendMessageAsync(options, 42, "С кнопкой", keyboard, CancellationToken.None));
        using (var body = JsonDocument.Parse(factory.LastRequestBody!))
            Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("reply_markup").ValueKind);
    }

    [Fact]
    public async Task StarHistoryUsesOfficialPagingAndPreservesInvoiceIdentity()
    {
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(1788163200);
        using var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$$"""
                {"ok":true,"result":{"transactions":[
                  {"id":"star-charge-1","amount":100,"date":{{{createdAt.ToUnixTimeSeconds()}}},
                   "source":{"type":"user","user":{"id":9001},"invoice_payload":"0123456789abcdef0123456789abcdef"}},
                  {"id":"withdrawal-1","amount":-50,"date":{{{createdAt.AddMinutes(-1).ToUnixTimeSeconds()}}},
                   "receiver":{"type":"fragment"}}
                ]}}
                """, Encoding.UTF8, "application/json")
        });
        var options = new TelegramBotOptions { BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN" };

        var transactions = await new TelegramBotApiClient(factory)
            .GetStarTransactionsAsync(options, 100, 25, CancellationToken.None);

        Assert.Equal("https://api.telegram.org/bot123:TEST_ONLY_NOT_A_REAL_TOKEN/getStarTransactions", factory.LastRequestUri);
        using (var request = JsonDocument.Parse(factory.LastRequestBody!))
        {
            Assert.Equal(100, request.RootElement.GetProperty("offset").GetInt32());
            Assert.Equal(25, request.RootElement.GetProperty("limit").GetInt32());
        }
        var payment = Assert.Single(transactions, x => x.InvoicePayload is not null);
        Assert.Equal("star-charge-1", payment.Id);
        Assert.Equal(100, payment.Stars);
        Assert.Equal(9001, payment.UserId);
        Assert.Equal(createdAt, payment.CreatedAt);
        Assert.Equal("0123456789abcdef0123456789abcdef", payment.InvoicePayload);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task StarHistoryRejectsInvalidPagingBeforeNetworkCall(int offset, int limit)
    {
        using var factory = new RecordingFactory(_ => throw new InvalidOperationException("network must not run"));
        var options = new TelegramBotOptions { BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN" };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new TelegramBotApiClient(factory).GetStarTransactionsAsync(options, offset, limit, CancellationToken.None));
        Assert.Null(factory.LastRequestUri);
    }

    [Fact]
    public async Task StarHistoryRejectsIncompletePageInsteadOfAssumingCoverage()
    {
        using var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"ok":true,"result":{"transactions":[{"id":"missing-date","amount":100}]}}""",
                Encoding.UTF8, "application/json")
        });

        var error = await Assert.ThrowsAsync<TelegramBotApiException>(() =>
            new TelegramBotApiClient(factory).GetStarTransactionsAsync(
                new TelegramBotOptions { BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN" },
                0, 100, CancellationToken.None));

        Assert.Equal(502, error.ErrorCode);
    }

    private sealed class RecordingFactory : IHttpClientFactory, IDisposable
    {
        private readonly RecordingHandler _handler;

        public RecordingFactory(Func<HttpRequestMessage, HttpResponseMessage> response)
            : this((request, _) => Task.FromResult(response(request))) { }

        public RecordingFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _handler = new(response);

        public string? LastRequestUri => _handler.LastRequestUri;
        public string? LastRequestBody => _handler.LastRequestBody;
        public HttpClient CreateClient(string name) => new(_handler, false);
        public void Dispose() => _handler.Dispose();
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.AbsoluteUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await response(request, cancellationToken);
        }
    }
}
