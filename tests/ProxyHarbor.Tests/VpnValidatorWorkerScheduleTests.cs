using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnValidatorWorkerScheduleTests
{
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
