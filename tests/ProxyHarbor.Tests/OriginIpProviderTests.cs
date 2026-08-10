using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует fail-closed health-gate и кэширование control endpoint.</summary>
public sealed class OriginIpProviderTests
{
    [Theory]
    [InlineData("Collector:DeadRetryMaxHours", "DeadRetryMaxHours")]
    [InlineData("Collector:SourceFailureBackoffMaxHours", "SourceFailureBackoffMaxHours")]
    [InlineData("Collector:DeadRetentionDays", "DeadRetentionDays")]
    public void InfrastructureOptionsRejectExtremeDurationsWithValidationError(string key, string failureName)
    {
        using var provider = BuildOptionsProvider(key, int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<CollectorOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains(failureName, StringComparison.Ordinal));
    }

    [Fact]
    public void InfrastructureOptionsRejectPrivateControlIp()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=unused;Username=unused",
            ["Collector:ProbeHost"] = "127.0.0.1"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProxyHarborInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<CollectorOptions>>().Value);

        Assert.Contains("ProbeHost", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("probe host.example")]
    [InlineData("probe_host.example")]
    [InlineData("-probe.example")]
    [InlineData("probe-.example")]
    [InlineData("probe..example")]
    [InlineData("probe.example.")]
    [InlineData("8.8.8.008")]
    [InlineData("2001:4860:4860:0:0:0:0:8888")]
    [InlineData("tést.example")]
    [InlineData("probe.example%25")]
    public void InfrastructureOptionsRejectNonCanonicalControlHost(string host)
    {
        using var provider = BuildOptionsProvider("Collector:ProbeHost", host);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<CollectorOptions>>().Value);

        Assert.Contains("ProbeHost", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InfrastructureOptionsRejectOversizedControlHost()
    {
        using var provider = BuildOptionsProvider("Collector:ProbeHost", new string('a', 254));

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<CollectorOptions>>().Value);
    }

    [Theory]
    [InlineData("probe.example")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    public void InfrastructureOptionsAcceptCanonicalPublicControlHost(string host)
    {
        using var provider = BuildOptionsProvider("Collector:ProbeHost", host);

        Assert.Equal(host, provider.GetRequiredService<IOptions<CollectorOptions>>().Value.ProbeHost);
    }

    [Theory]
    [InlineData("")]
    [InlineData("//other.example/control")]
    [InlineData("/control path")]
    [InlineData("/контроль")]
    [InlineData("/control%zz")]
    [InlineData("/control#fragment")]
    [InlineData("control")]
    public void InfrastructureOptionsRejectNonCanonicalProbePath(string path)
    {
        using var provider = BuildOptionsProvider("Collector:ProbePath", path);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<CollectorOptions>>().Value);

        Assert.Contains("ProbePath", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InfrastructureOptionsRejectOversizedProbePath()
    {
        using var provider = BuildOptionsProvider("Collector:ProbePath", "/" + new string('a', 2048));

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<CollectorOptions>>().Value);
    }

    [Theory]
    [InlineData("/?format=json")]
    [InlineData("/control?escaped=%2F&value=one")]
    [InlineData("/control;v=1")]
    public void InfrastructureOptionsAcceptCanonicalProbePath(string path)
    {
        using var provider = BuildOptionsProvider("Collector:ProbePath", path);

        Assert.Equal(path, provider.GetRequiredService<IOptions<CollectorOptions>>().Value.ProbePath);
    }

    [Fact]
    public async Task UsesConfiguredHttpsEndpointAndCachesSuccessfulHealthCheck()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ip\":\"8.8.8.8\"}")
        });
        using var factory = new StubHttpClientFactory(handler);
        var health = new ProbeControlHealth();
        using var provider = new OriginIpProvider(
            factory,
            Options.Create(new CollectorOptions
            {
                ProbeHost = "probe.example",
                ProbePort = 8443,
                ProbePath = "/who?format=json&escaped=%2F"
            }),
            health);

        Assert.Equal("8.8.8.8", await provider.GetRequiredAsync(CancellationToken.None));
        Assert.Equal("8.8.8.8", await provider.GetRequiredAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("https://probe.example:8443/who?format=json&escaped=%2F", handler.LastRequestUri?.AbsoluteUri);
        Assert.Equal(1, health.Availability);
        Assert.True(health.CheckedAtUnixSeconds > 0);
    }

    [Fact]
    public async Task ConcurrentProbeReadersObserveOneCoherentCachedSnapshot()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ip\":\"8.8.8.8\"}")
        });
        using var factory = new StubHttpClientFactory(handler);
        using var provider = new OriginIpProvider(
            factory,
            Options.Create(new CollectorOptions()),
            new ProbeControlHealth());

        var readers = Enumerable.Range(0, 2_000)
            .Select(_ => Task.Run(() => provider.GetRequiredAsync(CancellationToken.None)))
            .ToArray();
        var values = await Task.WhenAll(readers);

        Assert.All(values, value => Assert.Equal("8.8.8.8", value));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UnavailableEndpointFailsClosedAndCachesShortFailure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var factory = new StubHttpClientFactory(handler);
        var health = new ProbeControlHealth();
        using var provider = new OriginIpProvider(
            factory,
            Options.Create(new CollectorOptions()),
            health);

        await Assert.ThrowsAsync<ProbeControlUnavailableException>(
            () => provider.GetRequiredAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ProbeControlUnavailableException>(
            () => provider.GetRequiredAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, health.Availability);
    }

    [Fact]
    public async Task OversizedDirectResponseFailsClosedAndIsBounded()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 20 * 1024))
        });
        using var factory = new StubHttpClientFactory(handler);
        var health = new ProbeControlHealth();
        using var provider = new OriginIpProvider(
            factory,
            Options.Create(new CollectorOptions()),
            health);

        await Assert.ThrowsAsync<ProbeControlUnavailableException>(
            () => provider.GetRequiredAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, health.Availability);
    }

    [Fact]
    public async Task InvalidUtf8DirectResponseFailsClosedInsteadOfUsingReplacementCharacters()
    {
        // JSON-структура корректна, но значение содержит недопустимую UTF-8
        // последовательность C3 28. Permissive decoder скрывал бы повреждение.
        byte[] malformedJson =
        [
            (byte)'{', (byte)'"', (byte)'i', (byte)'p', (byte)'"', (byte)':', (byte)'"',
            0xC3, 0x28,
            (byte)'"', (byte)'}'
        ];
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(malformedJson)
        });
        using var factory = new StubHttpClientFactory(handler);
        var health = new ProbeControlHealth();
        using var provider = new OriginIpProvider(
            factory,
            Options.Create(new CollectorOptions()),
            health);

        await Assert.ThrowsAsync<ProbeControlUnavailableException>(
            () => provider.GetRequiredAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, health.Availability);
    }

    [Fact]
    public async Task NonStringIpFieldFailsClosedWithoutEscapingAsServerError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ip\":123}")
        });
        using var factory = new StubHttpClientFactory(handler);
        var health = new ProbeControlHealth();
        using var provider = new OriginIpProvider(
            factory,
            Options.Create(new CollectorOptions()),
            health);

        await Assert.ThrowsAsync<ProbeControlUnavailableException>(
            () => provider.GetRequiredAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, health.Availability);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"8.8.8.8\"")]
    [InlineData("123")]
    [InlineData("null")]
    public async Task NonObjectControlJsonFailsClosedWithoutEscapingAsServerError(string json)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });
        using var factory = new StubHttpClientFactory(handler);
        var health = new ProbeControlHealth();
        using var provider = new OriginIpProvider(
            factory,
            Options.Create(new CollectorOptions()),
            health);

        await Assert.ThrowsAsync<ProbeControlUnavailableException>(
            () => provider.GetRequiredAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, health.Availability);
    }

    [Fact]
    public async Task UnavailableEndpointStopsValidatorBeforeDatabaseQueueClaim()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var factory = new StubHttpClientFactory(handler);
        var settings = Options.Create(new CollectorOptions());
        using var origin = new OriginIpProvider(factory, settings, new ProbeControlHealth());
        using var validator = new ProxyValidator(
            new ThrowingDbFactory(),
            new ProxyProbeService(settings, origin),
            settings,
            NullLogger<ProxyValidator>.Instance);

        await Assert.ThrowsAsync<ProbeControlUnavailableException>(
            () => validator.ValidateBatchAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CallerCancellationIsNotCachedAsEndpointFailure()
    {
        using var handler = new BlockingHandler();
        using var factory = new StubHttpClientFactory(handler);
        var health = new ProbeControlHealth();
        using var provider = new OriginIpProvider(
            factory,
            Options.Create(new CollectorOptions()),
            health);
        using var cancellation = new CancellationTokenSource();

        var request = provider.GetRequiredAsync(cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(-1, health.Availability);
        Assert.Equal(0, health.CheckedAtUnixSeconds);
    }

    private static ServiceProvider BuildOptionsProvider(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=unused;Username=unused",
            [key] = value
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProxyHarborInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingDbFactory : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => throw new InvalidOperationException("Очередь БД не должна запрашиваться.");
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Очередь БД не должна запрашиваться.");
    }
}
