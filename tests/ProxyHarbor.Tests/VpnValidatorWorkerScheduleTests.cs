using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnValidatorWorkerScheduleTests
{
    [Theory]
    [InlineData(true, VpnEndpointStatus.Reachable, 10)]
    [InlineData(false, VpnEndpointStatus.Unreachable, 30)]
    [InlineData(null, VpnEndpointStatus.UnsupportedTransport, 360)]
    public void ProbeOutcomeMapsToStatusAndSchedule(
        bool? reachable,
        VpnEndpointStatus expectedStatus,
        int expectedDelayMinutes)
    {
        var checkedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();

        var update = VpnCatalogService.ToValidationUpdate(
            new VpnProbeResult(id, reachable, 42, "probe"),
            checkedAt,
            10,
            30,
            360);

        Assert.Equal(id, update.Id);
        Assert.Equal(expectedStatus, update.Status);
        Assert.Equal(42, update.LatencyMs);
        Assert.Equal("probe", update.Error);
        Assert.Equal(checkedAt, update.CheckedAt);
        Assert.Equal(checkedAt.AddMinutes(expectedDelayMinutes), update.NextCheckAt);
    }

    [Fact]
    public void NonPositiveValidationIntervalIsClamped()
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var update = VpnCatalogService.ToValidationUpdate(
            new VpnProbeResult(Guid.NewGuid(), true, null, null),
            checkedAt,
            0,
            0,
            0);

        Assert.Equal(checkedAt.AddMinutes(1), update.NextCheckAt);
    }

    [Fact]
    public void CompletePersistenceAcceptsExactRowCount()
    {
        VpnCatalogService.EnsureCompletePersistence(1600, 1600);
    }

    [Fact]
    public void IncompletePersistenceFailsClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            VpnCatalogService.EnsureCompletePersistence(1599, 1600));

        Assert.Contains("1599 из 1600", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1600)]
    public void NonEmptyBatchContinuesDrainingQueue(int checkedCount)
    {
        Assert.Equal(TimeSpan.FromSeconds(1), VpnValidatorWorker.NextDelay(checkedCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EmptyBatchUsesIdleBackoff(int checkedCount)
    {
        Assert.Equal(TimeSpan.FromSeconds(30), VpnValidatorWorker.NextDelay(checkedCount));
    }
}
