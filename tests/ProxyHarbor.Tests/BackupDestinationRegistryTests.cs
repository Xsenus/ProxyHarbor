using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class BackupDestinationRegistryTests
{
    [Fact]
    public void ResolvesOnlyExplicitlyAllowedRouteAndOperation()
    {
        var registry = CreateRegistry();
        var (destination, route) = S3Route();

        var adapter = registry.Resolve(destination, route, BackupDestinationOperation.Put, 1_024);

        Assert.IsType<S3BackupDestinationAdapter>(adapter);
        Assert.True(adapter.Capabilities.Put.Supported);
        Assert.True(adapter.Capabilities.Verify.Supported);
        Assert.True(adapter.Capabilities.Materialize.Supported);
        Assert.False(adapter.Capabilities.SupportsConditionalCreate);
        Assert.False(adapter.Capabilities.ProvidesNativeVersion);
        Assert.False(adapter.Capabilities.ProvidesNativeChecksum);
        Assert.Same(adapter, registry.Resolve(
            destination, route, BackupDestinationOperation.Materialize, 1_024));
    }

    [Fact]
    public void RejectsMissingDuplicateAndUnknownAdapters()
    {
        AssertRejection(
            BackupDestinationRouteRejection.MissingAdapter,
            () => _ = new BackupDestinationRegistry([new S3BackupDestinationAdapter()]));
        AssertRejection(
            BackupDestinationRouteRejection.DuplicateAdapter,
            () => _ = new BackupDestinationRegistry([
                new S3BackupDestinationAdapter(),
                new S3BackupDestinationAdapter(),
                new TelegramBackupDestinationAdapter()
            ]));
        AssertRejection(
            BackupDestinationRouteRejection.UnknownKind,
            () => _ = new BackupDestinationRegistry([
                new S3BackupDestinationAdapter(),
                new TelegramBackupDestinationAdapter(),
                new FakeAdapter("filesystem")
            ]));
    }

    [Fact]
    public void RejectsDisabledDrainingAndOperationForbiddenRoutes()
    {
        var registry = CreateRegistry();
        var (destination, route) = S3Route();
        destination.Enabled = false;
        AssertRejection(
            BackupDestinationRouteRejection.DestinationDisabled,
            () => registry.Resolve(destination, route, BackupDestinationOperation.Put, 1));

        destination.Enabled = true;
        route.Draining = true;
        AssertRejection(
            BackupDestinationRouteRejection.RouteDraining,
            () => registry.Resolve(destination, route, BackupDestinationOperation.Put, 1));

        route.Draining = false;
        route.AllowedOperations = "read";
        AssertRejection(
            BackupDestinationRouteRejection.OperationForbidden,
            () => registry.Resolve(destination, route, BackupDestinationOperation.Put, 1));
    }

    [Fact]
    public void RejectsRouteForAnotherDestinationWithoutSearchingFallback()
    {
        var registry = CreateRegistry();
        var (destination, route) = S3Route();
        route.BackupDestinationId = Guid.NewGuid();
        route.Role = "fallback";

        AssertRejection(
            BackupDestinationRouteRejection.RouteForbidden,
            () => registry.Resolve(destination, route, BackupDestinationOperation.Put, 1));
    }

    [Fact]
    public void TelegramDeclaresBoundedPutAndNoReadOrIndependentVerify()
    {
        var registry = CreateRegistry();
        var destination = new BackupDestination
        {
            Id = Guid.NewGuid(),
            Name = "telegram",
            Kind = "telegram",
            Enabled = true,
            FailureDomain = "telegram"
        };
        var route = new BackupPoolDestination
        {
            BackupDestinationId = destination.Id,
            Enabled = true,
            AllowedOperations = "put,verify,read"
        };
        var adapter = registry.Resolve(destination, route, BackupDestinationOperation.Put, 1);
        Assert.Equal(20, adapter.Capabilities.Put.MaximumParts);
        Assert.Equal(20L * 49 * 1024 * 1024, adapter.Capabilities.Put.MaximumBytes);

        AssertRejection(
            BackupDestinationRouteRejection.UnsupportedOperation,
            () => registry.Resolve(destination, route, BackupDestinationOperation.Verify, 1));
        AssertRejection(
            BackupDestinationRouteRejection.UnsupportedOperation,
            () => registry.Resolve(destination, route, BackupDestinationOperation.Materialize, 1));
        AssertRejection(
            BackupDestinationRouteRejection.ObjectTooLarge,
            () => registry.Resolve(
                destination,
                route,
                BackupDestinationOperation.Put,
                adapter.Capabilities.Put.MaximumBytes!.Value + 1));
    }

    [Fact]
    public void ProductionDependencyInjectionContainsBothAllowlistedAdapters()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=registry-test;Username=test;Password=not-used"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProxyHarborInfrastructure(configuration);
        services.AddSingleton<IBackupDestinationAdapter, TelegramBackupDestinationAdapter>();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<BackupDestinationRegistry>();
        var (destination, route) = S3Route();

        Assert.IsType<S3BackupDestinationAdapter>(
            registry.Resolve(destination, route, BackupDestinationOperation.Put, 1));
    }

    [Fact]
    public void ProviderFailuresHaveStableCodeAndDisposition()
    {
        var failure = new BackupDestinationFailure(
            BackupDestinationErrorCode.UnknownOutcome,
            BackupDestinationFailureDisposition.UnknownOutcome);

        Assert.Equal(BackupDestinationErrorCode.UnknownOutcome, failure.Code);
        Assert.Equal(BackupDestinationFailureDisposition.UnknownOutcome, failure.Disposition);
    }

    private static BackupDestinationRegistry CreateRegistry() => new([
        new S3BackupDestinationAdapter(),
        new TelegramBackupDestinationAdapter()
    ]);

    private static (BackupDestination Destination, BackupPoolDestination Route) S3Route()
    {
        var destination = new BackupDestination
        {
            Id = Guid.NewGuid(),
            Name = "primary-s3",
            Kind = "s3",
            Enabled = true,
            FailureDomain = "independent-s3"
        };
        return (destination, new BackupPoolDestination
        {
            BackupDestinationId = destination.Id,
            Enabled = true,
            AllowedOperations = "put,verify,read"
        });
    }

    private static void AssertRejection(
        BackupDestinationRouteRejection expected,
        Action action)
    {
        var exception = Assert.Throws<BackupDestinationRouteException>(action);
        Assert.Equal(expected, exception.Rejection);
    }

    private sealed class FakeAdapter(string kind) : IBackupDestinationAdapter
    {
        public string Kind { get; } = kind;
        public BackupDestinationCapabilities Capabilities { get; } = new(
            new(false), new(false), new(false), false, false, false);
    }
}
