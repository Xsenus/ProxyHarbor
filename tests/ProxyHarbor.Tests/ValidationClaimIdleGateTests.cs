using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует bounded idle-coalescing и независимую редкую запись heartbeat каждого узла.</summary>
public sealed class ValidationClaimIdleGateTests
{
    [Fact]
    public void SerializedProbeCoalescesOnlyInsideConfirmedCooldown()
    {
        long timestamp = 1_000;
        var gate = CreateGate(() => timestamp);

        Assert.False(gate.TryCoalesceSerializedProbe());
        gate.MarkEmpty();
        Assert.True(gate.TryCoalesceSerializedProbe());
        Assert.Equal(1, gate.CoalescedClaims);
        Assert.Equal(1, gate.SerializedCoalescedClaims);

        timestamp += 2_000;
        Assert.False(gate.TryCoalesceSerializedProbe());
        Assert.Equal(1, gate.SerializedCoalescedClaims);
    }

    [Fact]
    public void EmptyCooldownCoalescesPollsAndExpiresOnMonotonicClock()
    {
        long timestamp = 1_000;
        var gate = CreateGate(() => timestamp);
        var node = Guid.NewGuid();

        Assert.False(gate.TryCoalesce(node).Coalesced);
        gate.MarkEmpty();

        var first = gate.TryCoalesce(node);
        Assert.True(first.Coalesced);
        Assert.True(first.PersistHeartbeat);
        Assert.True(gate.CooldownActive);
        Assert.False(gate.TryCoalesce(node).PersistHeartbeat);
        Assert.Equal(2, gate.CoalescedClaims);

        timestamp += 1_999;
        Assert.True(gate.TryCoalesce(node).Coalesced);
        timestamp++;
        Assert.False(gate.TryCoalesce(node).Coalesced);
        Assert.False(gate.CooldownActive);
    }

    [Fact]
    public void HeartbeatsAreThrottledPerNodeAndWorkClearsCooldown()
    {
        long timestamp = 5_000;
        var gate = CreateGate(() => timestamp);
        var firstNode = Guid.NewGuid();
        var secondNode = Guid.NewGuid();
        gate.MarkEmpty();

        Assert.True(gate.TryCoalesce(firstNode).PersistHeartbeat);
        Assert.True(gate.TryCoalesce(secondNode).PersistHeartbeat);
        timestamp += 29_999;
        gate.MarkEmpty();
        Assert.False(gate.TryCoalesce(firstNode).PersistHeartbeat);
        timestamp++;
        gate.MarkEmpty();
        Assert.True(gate.TryCoalesce(firstNode).PersistHeartbeat);

        gate.MarkWorkAvailable();
        Assert.False(gate.TryCoalesce(firstNode).Coalesced);
        Assert.False(gate.CooldownActive);
    }

    [Fact]
    public void ExplicitHeartbeatResetsPersistenceInterval()
    {
        long timestamp = 9_000;
        var gate = CreateGate(() => timestamp);
        var node = Guid.NewGuid();
        gate.MarkHeartbeat(node);
        gate.MarkEmpty();

        Assert.False(gate.TryCoalesce(node).PersistHeartbeat);
        timestamp += 30_000;
        gate.MarkEmpty();
        Assert.True(gate.TryCoalesce(node).PersistHeartbeat);
    }

    [Fact]
    public void UnderfilledClaimStartsCooldownWhileFullClaimKeepsDraining()
    {
        long timestamp = 12_000;
        var gate = CreateGate(() => timestamp);
        var node = Guid.NewGuid();

        gate.MarkClaimResult(claimedCount: 35, requestedCount: 160);
        Assert.True(gate.CooldownActive);
        Assert.True(gate.TryCoalesce(node).Coalesced);

        gate.MarkClaimResult(claimedCount: 160, requestedCount: 160);
        Assert.False(gate.CooldownActive);
        Assert.False(gate.TryCoalesce(node).Coalesced);

        Assert.Throws<ArgumentOutOfRangeException>(() => gate.MarkClaimResult(-1, 160));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.MarkClaimResult(161, 160));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.MarkClaimResult(0, 0));
    }

    private static ValidationClaimIdleGate CreateGate(Func<long> timestamp) =>
        new(timestamp, 1_000, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
}
