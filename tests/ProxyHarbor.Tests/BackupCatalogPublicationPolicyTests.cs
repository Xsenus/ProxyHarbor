using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class BackupCatalogPublicationPolicyTests
{
    [Fact]
    public async Task DisabledPublisherNeverOpensDatabaseOrProvider()
    {
        var signing = Options.Create(new BackupCatalogSigningOptions { Enabled = false });
        var routing = Options.Create(new BackupRoutingOptions { Enabled = true });
        var processor = new BackupCatalogPublicationProcessor(null!, null!, signing, routing);
        Assert.Null(await processor.TryClaimAsync(CancellationToken.None));
        await processor.ProcessAsync(new BackupCatalogPublicationLease(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        using var worker = new BackupCatalogPublicationWorker(processor, signing, routing,
            NullLogger<BackupCatalogPublicationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void PublisherRequiresRoutingFeatureFlagAndStrongSigningConfiguration()
    {
        var routing = new BackupRoutingOptions { Enabled = false };
        var signing = new BackupCatalogSigningOptions
        {
            Enabled = true,
            SigningKey = "synthetic-dedicated-catalog-signing-key-32-characters",
            KeyReference = "catalog-v1"
        };
        Assert.False(BackupCatalogPublicationProcessor.IsEnabled(signing, routing));
        routing.Enabled = true;
        Assert.True(BackupCatalogPublicationProcessor.IsEnabled(signing, routing));
        signing.Enabled = false;
        Assert.False(BackupCatalogPublicationProcessor.IsEnabled(signing, routing));
        signing.Enabled = true;
        signing.SigningKey = null;
        Assert.False(BackupCatalogPublicationProcessor.IsEnabled(signing, routing));
        signing.SigningKey = "synthetic-dedicated-catalog-signing-key-32-characters";
        signing.KeyReference = "unsafe/reference";
        Assert.False(BackupCatalogPublicationProcessor.IsEnabled(signing, routing));
    }

    [Theory]
    [InlineData(null, "catalog-v1", false)]
    [InlineData("catalog-v1", "catalog-v1", false)]
    [InlineData("catalog-v1", "catalog-v2", true)]
    public void KeyRotationNeverSilentlyResignsAnExistingSidecar(
        string? pinnedReference, string currentReference, bool expected)
    {
        Assert.Equal(expected, BackupCatalogPublicationProcessor.NeedsKeyReview(
            pinnedReference, currentReference));
    }

    [Fact]
    public void OnlyCurrentVerifiedLeaseWithMatchingKeyCanPublish()
    {
        var now = DateTimeOffset.UtcNow;
        var poolId = Guid.NewGuid();
        var lease = new BackupCatalogPublicationLease(Guid.NewGuid(), Guid.NewGuid());
        var copy = new BackupCopy
        {
            State = "verified",
            CatalogState = "processing",
            CatalogLeaseId = lease.LeaseId,
            CatalogLeaseUntil = now.AddMinutes(1),
            CatalogKeyReference = "catalog-v1",
            BackupRun = new BackupRun { BackupPoolId = poolId }
        };

        Assert.Equal(poolId, BackupCatalogPublicationProcessor.EligiblePoolId(
            copy, lease, "catalog-v1", now));

        copy.State = "quarantined";
        Assert.Null(BackupCatalogPublicationProcessor.EligiblePoolId(copy, lease, "catalog-v1", now));
        copy.State = "verified";
        copy.CatalogState = "pending";
        Assert.Null(BackupCatalogPublicationProcessor.EligiblePoolId(copy, lease, "catalog-v1", now));
        copy.CatalogState = "processing";
        copy.CatalogLeaseId = Guid.NewGuid();
        Assert.Null(BackupCatalogPublicationProcessor.EligiblePoolId(copy, lease, "catalog-v1", now));
        copy.CatalogLeaseId = lease.LeaseId;
        copy.CatalogLeaseUntil = null;
        Assert.Null(BackupCatalogPublicationProcessor.EligiblePoolId(copy, lease, "catalog-v1", now));
        copy.CatalogLeaseUntil = now;
        Assert.Null(BackupCatalogPublicationProcessor.EligiblePoolId(copy, lease, "catalog-v1", now));
        copy.CatalogLeaseUntil = now.AddMinutes(1);
        Assert.Null(BackupCatalogPublicationProcessor.EligiblePoolId(copy, lease, "catalog-v2", now));
        copy.BackupRun.BackupPoolId = null;
        Assert.Null(BackupCatalogPublicationProcessor.EligiblePoolId(copy, lease, "catalog-v1", now));
    }

    [Theory]
    [InlineData(BackupDestinationErrorCode.Collision, true)]
    [InlineData(BackupDestinationErrorCode.IntegrityMismatch, true)]
    [InlineData(BackupDestinationErrorCode.UnsupportedOperation, true)]
    [InlineData(BackupDestinationErrorCode.ProviderRejected, true)]
    [InlineData(BackupDestinationErrorCode.UnknownOutcome, false)]
    [InlineData(BackupDestinationErrorCode.Timeout, false)]
    [InlineData(BackupDestinationErrorCode.RateLimited, false)]
    [InlineData(BackupDestinationErrorCode.InvalidConfiguration, false)]
    public void OnlyIrrecoverableProviderOutcomesRequireManualReview(
        BackupDestinationErrorCode code, bool expected)
    {
        Assert.Equal(expected, BackupCatalogPublicationProcessor.IsPermanent(code));
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(8, 1920)]
    [InlineData(9, 3600)]
    [InlineData(100, 3600)]
    public void RetryDelayIsBounded(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            BackupCatalogPublicationProcessor.RetryDelay(attempt));
    }
}
