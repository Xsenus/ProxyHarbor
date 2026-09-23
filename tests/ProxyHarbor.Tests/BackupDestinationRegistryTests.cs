using Microsoft.AspNetCore.DataProtection;
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

        route.Enabled = false;
        AssertRejection(
            BackupDestinationRouteRejection.RouteForbidden,
            () => registry.Resolve(destination, route, BackupDestinationOperation.Put, 1));
        Assert.Same(registry.GetRequired("s3"), registry.Resolve(
            destination, route, BackupDestinationOperation.Verify, 1));
        Assert.Same(registry.GetRequired("s3"), registry.Resolve(
            destination, route, BackupDestinationOperation.Materialize, 1));

        route.Enabled = true;
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

    [Fact]
    public async Task S3AdapterUsesProtectedProjectionAndReturnsVerifiedEvidence()
    {
        var protection = new EphemeralDataProtectionProvider();
        var protectedSecrets = protection.CreateProtector("ProxyHarbor.BackupDestination.Secrets.v1")
            .Protect("{\"accessKey\":\"access\",\"secretKey\":\"secret\"}");
        var transport = new CapturingObjectStorageTransport();
        var adapter = new S3BackupDestinationAdapter(transport, protection);
        var destination = new BackupDestination
        {
            Kind = "s3",
            SettingsJson = "{\"endpoint\":\"https://storage.example.test\",\"region\":\"eu-1\",\"bucket\":\"backup\",\"prefix\":\"safe\",\"usePathStyle\":true}",
            ProtectedSecrets = protectedSecrets
        };

        var result = await adapter.PutAsync(
            destination,
            "snapshot.phbackup",
            new string('a', 64),
            123,
            CancellationToken.None);

        Assert.True(result.IndependentlyVerified);
        Assert.Equal("safe/snapshot.phbackup", result.NativeLocator);
        Assert.Equal("access", transport.Options!.ObjectStorageAccessKey);
        Assert.Equal("secret", transport.Options.ObjectStorageSecretKey);
        Assert.Equal("https://storage.example.test", transport.Options.ObjectStorageEndpoint);
    }

    [Theory]
    [InlineData(null, BackupDestinationProbeOutcome.Matching)]
    [InlineData(BackupDestinationErrorCode.NotFound, BackupDestinationProbeOutcome.Missing)]
    [InlineData(BackupDestinationErrorCode.IntegrityMismatch, BackupDestinationProbeOutcome.Mismatching)]
    [InlineData(BackupDestinationErrorCode.Unavailable, BackupDestinationProbeOutcome.Inconclusive)]
    [InlineData(BackupDestinationErrorCode.AuthenticationFailed, BackupDestinationProbeOutcome.Inconclusive)]
    [InlineData(BackupDestinationErrorCode.AuthorizationFailed, BackupDestinationProbeOutcome.Inconclusive)]
    [InlineData(BackupDestinationErrorCode.RateLimited, BackupDestinationProbeOutcome.Inconclusive)]
    [InlineData(BackupDestinationErrorCode.Timeout, BackupDestinationProbeOutcome.Inconclusive)]
    public async Task S3ProbeMapsHeadEvidenceWithoutUpload(
        BackupDestinationErrorCode? failure, BackupDestinationProbeOutcome expected)
    {
        var protection = new EphemeralDataProtectionProvider();
        var transport = new CapturingObjectStorageTransport { VerificationFailure = failure };
        var adapter = new S3BackupDestinationAdapter(transport, protection);
        var destination = new BackupDestination
        {
            Kind = "s3",
            SettingsJson = "{\"endpoint\":\"https://storage.example.test\",\"region\":\"eu-1\",\"bucket\":\"backup\",\"prefix\":\"safe\",\"usePathStyle\":true}",
            ProtectedSecrets = protection.CreateProtector("ProxyHarbor.BackupDestination.Secrets.v1")
                .Protect("{\"accessKey\":\"access\",\"secretKey\":\"secret\"}")
        };

        var result = await adapter.ProbeWriteOutcomeAsync(
            destination, "snapshot.phbackup", new string('a', 64), 123, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected == BackupDestinationProbeOutcome.Inconclusive ? failure : null,
            result.FailureCode);
        Assert.Equal("safe/snapshot.phbackup", result.NativeLocator);
        Assert.Equal(0, transport.UploadCalls);
    }

    [Fact]
    public async Task S3ProbeRejectsNonBasenameLocator()
    {
        var protection = new EphemeralDataProtectionProvider();
        var adapter = new S3BackupDestinationAdapter(
            new CapturingObjectStorageTransport(), protection);
        var exception = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            adapter.ProbeWriteOutcomeAsync(
                new BackupDestination { Kind = "s3" },
                "../foreign.phbackup", new string('a', 64), 123, CancellationToken.None));

        Assert.Equal(BackupDestinationErrorCode.InvalidConfiguration, exception.Failure.Code);
    }

    [Fact]
    public async Task S3MaterializationRequiresExactCopyLocatorAndVerifiedIdentity()
    {
        var protection = new EphemeralDataProtectionProvider();
        var transport = new CapturingObjectStorageTransport();
        var adapter = new S3BackupDestinationAdapter(transport, protection);
        var destination = new BackupDestination
        {
            Kind = "s3",
            SettingsJson = "{\"endpoint\":\"https://storage.example.test\",\"region\":\"eu-1\",\"bucket\":\"backup\",\"prefix\":\"safe\",\"usePathStyle\":true}",
            ProtectedSecrets = protection.CreateProtector("ProxyHarbor.BackupDestination.Secrets.v1")
                .Protect("{\"accessKey\":\"access\",\"secretKey\":\"secret\"}")
        };
        var path = Path.Combine(Path.GetTempPath(), "materialized.phbackup");

        var result = await adapter.MaterializeAsync(
            destination, "snapshot.phbackup", "safe/snapshot.phbackup", path,
            new string('a', 64), 123, CancellationToken.None);

        Assert.Equal(path, result.Path);
        Assert.Equal(new string('a', 64), result.Sha256);
        Assert.Equal("version-1", result.NativeVersion);
        Assert.Equal("safe/snapshot.phbackup", transport.MaterializedObjectKey);
        Assert.Equal(1, transport.MaterializeCalls);
        Assert.Equal(0, transport.UploadCalls);

        var exception = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            adapter.MaterializeAsync(
                destination, "snapshot.phbackup", "foreign/snapshot.phbackup", path,
                new string('a', 64), 123, CancellationToken.None));
        Assert.Equal(BackupDestinationErrorCode.InvalidConfiguration, exception.Failure.Code);
        Assert.Equal(1, transport.MaterializeCalls);
    }

    [Fact]
    public async Task S3MaterializationRejectsTraversalAndTransportMismatch()
    {
        var protection = new EphemeralDataProtectionProvider();
        var transport = new CapturingObjectStorageTransport { MaterializedSha256 = new string('b', 64) };
        var adapter = new S3BackupDestinationAdapter(transport, protection);
        var destination = new BackupDestination
        {
            Kind = "s3",
            SettingsJson = "{\"endpoint\":\"https://storage.example.test\",\"region\":\"eu-1\",\"bucket\":\"backup\",\"prefix\":\"safe\",\"usePathStyle\":true}",
            ProtectedSecrets = protection.CreateProtector("ProxyHarbor.BackupDestination.Secrets.v1")
                .Protect("{\"accessKey\":\"access\",\"secretKey\":\"secret\"}")
        };
        var path = Path.Combine(Path.GetTempPath(), "materialized.phbackup");

        var traversal = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            adapter.MaterializeAsync(
                destination, "../snapshot.phbackup", "safe/snapshot.phbackup", path,
                new string('a', 64), 123, CancellationToken.None));
        Assert.Equal(BackupDestinationErrorCode.InvalidConfiguration, traversal.Failure.Code);
        Assert.Equal(0, transport.MaterializeCalls);

        var mismatch = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
            adapter.MaterializeAsync(
                destination, "snapshot.phbackup", "safe/snapshot.phbackup", path,
                new string('a', 64), 123, CancellationToken.None));
        Assert.Equal(BackupDestinationErrorCode.IntegrityMismatch, mismatch.Failure.Code);
        Assert.Equal(1, transport.MaterializeCalls);
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

    private sealed class CapturingObjectStorageTransport : IBackupObjectStorageTransport
    {
        public BackupOptions? Options { get; private set; }
        public BackupDestinationErrorCode? VerificationFailure { get; init; }
        public string? MaterializedSha256 { get; init; }
        public string? MaterializedObjectKey { get; private set; }
        public int MaterializeCalls { get; private set; }
        public int UploadCalls { get; private set; }

        public Task<string> UploadAndVerifyAsync(
            string path,
            BackupOptions options,
            CancellationToken token) => throw new NotSupportedException();

        public Task<BackupObjectStorageWriteResult> UploadAndVerifyDetailedAsync(
            string path,
            BackupOptions options,
            CancellationToken token)
        {
            UploadCalls++;
            Options = options;
            return Task.FromResult(new BackupObjectStorageWriteResult(
                "safe/snapshot.phbackup",
                123,
                new string('a', 64),
                "version-1",
                "checksum",
                "etag"));
        }

        public Task<BackupObjectStorageVerificationResult> VerifyAsync(
            string objectKey, long expectedSize, string expectedSha256,
            BackupOptions options, CancellationToken token)
        {
            Options = options;
            if (VerificationFailure is { } code)
                throw new BackupDestinationOperationException(
                    new BackupDestinationFailure(code, BackupDestinationFailureDisposition.Permanent),
                    "synthetic HEAD failure");
            return Task.FromResult(new BackupObjectStorageVerificationResult(
                objectKey, expectedSize, expectedSha256, "version-1", "checksum", "etag"));
        }

        public Task<BackupObjectStorageMaterializationResult> MaterializeAndVerifyAsync(
            string objectKey, string finalPath, long expectedSize, string expectedSha256,
            BackupOptions options, CancellationToken token)
        {
            MaterializeCalls++;
            MaterializedObjectKey = objectKey;
            Options = options;
            return Task.FromResult(new BackupObjectStorageMaterializationResult(
                finalPath, expectedSize, MaterializedSha256 ?? expectedSha256,
                "version-1", "checksum", "etag"));
        }
    }
}
