using System.Net;
using System.Text;
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
            BotToken = "123:TEST_ONLY_NOT_A_REAL_TOKEN", UpdateMode = TelegramUpdateModes.Polling,
            Name = "ProxyHarbor", Description = "Полное описание тестового бота.",
            ShortDescription = "Короткое описание", BotUsername = "ProxyHarborBot",
            WebhookSecret = "secret", PublicBaseUrl = "https://proxy.example.test"
        }, CancellationToken.None);
        Assert.Equal("https://api.telegram.org/bot123:TEST_ONLY_NOT_A_REAL_TOKEN/deleteWebhook", factory.LastRequestUri);
    }

    private sealed class RecordingFactory(Func<HttpRequestMessage,HttpResponseMessage> response) : IHttpClientFactory, IDisposable
    {
        private readonly RecordingHandler _handler = new(response);
        public string? LastRequestUri => _handler.LastRequestUri;
        public HttpClient CreateClient(string name) => new(_handler, false);
        public void Dispose() => _handler.Dispose();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(response(request));
        }
    }
}
