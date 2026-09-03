using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class OperationalMaintenanceWorkerTests
{
    [Fact]
    public void SuccessfulRunKeepsHourlyCadence() =>
        Assert.Equal(TimeSpan.FromHours(1), OperationalMaintenanceWorker.NextDelay(false));

    [Fact]
    public void FailedRunRetriesWithoutWaitingAnHour() =>
        Assert.Equal(TimeSpan.FromMinutes(5), OperationalMaintenanceWorker.NextDelay(true));
}
