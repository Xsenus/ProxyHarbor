using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет, что backup использует runtime-маршруты commerce-бота.</summary>
public sealed class TelegramBackupTransportTests
{
    [Fact]
    public async Task AutoModeUsesConfiguredAndCatalogProxiesBeforeDirectFallback()
    {
        var configured = new TelegramProxyOptions { Host = "198.51.100.10", Port = 1080 };
        var catalog = new TelegramProxyOptions { Host = "203.0.113.20", Port = 2080 };
        using var services = Services(new TelegramBotOptions
        {
            TransportMode = TelegramTransportModes.Auto,
            Proxies = [configured]
        }, [catalog]);
        using var pool = new TelegramProxyHttpClientPool();
        var transport = new TelegramBackupTransport(
            services.GetRequiredService<IServiceScopeFactory>(),
            new StubHttpClientFactory(), new TelegramTransportHealth(), pool);

        var attempts = await transport.ResolveAttemptsAsync(CancellationToken.None);

        Assert.Equal(3, attempts.Length);
        Assert.Same(configured, attempts[0]);
        Assert.Same(catalog, attempts[1]);
        Assert.Null(attempts[2]);
    }

    [Fact]
    public async Task DirectModeStreamsBackupThroughNamedTelegramClient()
    {
        using var services = Services(new TelegramBotOptions
        {
            TransportMode = TelegramTransportModes.Direct
        }, []);
        var factory = new StubHttpClientFactory();
        using var pool = new TelegramProxyHttpClientPool();
        var transport = new TelegramBackupTransport(
            services.GetRequiredService<IServiceScopeFactory>(),
            factory, new TelegramTransportHealth(), pool);
        var path = Path.Combine(Path.GetTempPath(), $"proxyharbor-route-{Guid.NewGuid():N}.phbackup");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            await transport.SendAsync(
                path, "backup", "123456789:valid_test_token_value", "123456",
                CancellationToken.None);

            Assert.Equal("telegram", factory.LastClientName);
            Assert.Equal(1, factory.Handler.Attempts);
            Assert.Contains(Path.GetFileName(path), factory.Handler.Body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ServiceProvider Services(
        TelegramBotOptions options,
        IReadOnlyList<TelegramProxyOptions> candidates) =>
        new ServiceCollection()
            .AddSingleton<ITelegramBotConfigurationStore>(new StaticConfigurationStore(options))
            .AddScoped<ITelegramProxyCandidateProvider>(_ => new StaticCandidateProvider(candidates))
            .BuildServiceProvider();

    private sealed class StaticConfigurationStore(TelegramBotOptions options)
        : ITelegramBotConfigurationStore
    {
        public Task<TelegramBotOptions> GetAsync(CancellationToken token = default) =>
            Task.FromResult(options);

        public Task SaveAsync(TelegramBotOptions value, CancellationToken token = default) =>
            throw new InvalidOperationException("Сохранение не ожидается.");
    }

    private sealed class StaticCandidateProvider(IReadOnlyList<TelegramProxyOptions> candidates)
        : ITelegramProxyCandidateProvider
    {
        public Task<IReadOnlyList<TelegramProxyOptions>> GetCandidatesAsync(CancellationToken token) =>
            Task.FromResult(candidates);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory, IDisposable
    {
        internal AcceptingHandler Handler { get; } = new();
        private readonly HttpClient client;
        internal string? LastClientName { get; private set; }

        internal StubHttpClientFactory() => client = new HttpClient(Handler);

        public HttpClient CreateClient(string name)
        {
            LastClientName = name;
            return client;
        }

        public void Dispose() => client.Dispose();
    }

    private sealed class AcceptingHandler : HttpMessageHandler
    {
        internal int Attempts { get; private set; }
        internal string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{}}", Encoding.UTF8, "application/json")
            };
        }
    }
}
