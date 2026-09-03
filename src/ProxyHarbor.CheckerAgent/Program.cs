using System.Net;
using ProxyHarbor.CheckerAgent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<CheckerAgentOptions>()
    .Bind(builder.Configuration.GetSection(CheckerAgentOptions.Section))
    .Validate(x => Uri.TryCreate(x.ControlPlaneBaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment),
        "ControlPlaneBaseUrl должен быть корневым HTTPS origin без пути, query и fragment")
    .Validate(x => x.NodeId != Guid.Empty, "NodeId обязателен")
    .Validate(x => Path.IsPathFullyQualified(x.TokenFile), "TokenFile должен быть абсолютным")
    .ValidateOnStart();
builder.Services.AddHttpClient("control-plane", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 16
    });
builder.Services.AddHttpClient("origin", client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 8
    });
builder.Services.AddSingleton<CheckerAgentProbeRuntime>();
builder.Services.AddSingleton<CheckerAgentWorker>();
builder.Services.AddHostedService(services => services.GetRequiredService<CheckerAgentWorker>());
await builder.Build().RunAsync();
