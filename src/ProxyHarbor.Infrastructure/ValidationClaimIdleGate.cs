using System.Collections.Concurrent;
using System.Diagnostics;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Объединяет близкие пустые lease-poll нескольких checker-узлов. Короткий
/// process-local cooldown не является источником истины и ограничивает задержку
/// появившейся работы двумя секундами; PostgreSQL по-прежнему владеет очередью.
/// </summary>
public sealed class ValidationClaimIdleGate
{
    private static readonly TimeSpan EmptyCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatPersistenceInterval = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, long> lastHeartbeatTimestamps = new();
    private readonly Func<long> getTimestamp;
    private readonly long cooldownTicks;
    private readonly long heartbeatTicks;
    private long emptyUntilTimestamp;
    private long coalescedClaims;
    private long serializedCoalescedClaims;

    /// <summary>Создаёт gate на монотонных системных часах.</summary>
    public ValidationClaimIdleGate()
        : this(Stopwatch.GetTimestamp, Stopwatch.Frequency, EmptyCooldown, HeartbeatPersistenceInterval) { }

    internal ValidationClaimIdleGate(
        Func<long> getTimestamp,
        long timestampFrequency,
        TimeSpan emptyCooldown,
        TimeSpan heartbeatPersistenceInterval)
    {
        ArgumentNullException.ThrowIfNull(getTimestamp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(emptyCooldown, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatPersistenceInterval, TimeSpan.Zero);
        this.getTimestamp = getTimestamp;
        cooldownTicks = DurationTicks(emptyCooldown, timestampFrequency);
        heartbeatTicks = DurationTicks(heartbeatPersistenceInterval, timestampFrequency);
    }

    /// <summary>Число запросов, обслуженных без полного обхода validation-очереди.</summary>
    public long CoalescedClaims => Interlocked.Read(ref coalescedClaims);

    /// <summary>Число лишних probes, остановленных после cluster-wide сериализации.</summary>
    public long SerializedCoalescedClaims => Interlocked.Read(ref serializedCoalescedClaims);

    /// <summary>Показывает, действует ли сейчас короткое подтверждённое idle-окно.</summary>
    public bool CooldownActive => Volatile.Read(ref emptyUntilTimestamp) > getTimestamp();

    internal IdleClaimDecision TryCoalesce(Guid nodeId)
    {
        var now = getTimestamp();
        if (Volatile.Read(ref emptyUntilTimestamp) <= now)
            return default;
        Interlocked.Increment(ref coalescedClaims);
        return new IdleClaimDecision(true, ReserveHeartbeat(nodeId, now));
    }

    /// <summary>
    /// Повторно проверяет cooldown после cluster-wide сериализации claim. Несколько
    /// узлов могут одновременно пройти быструю внешнюю проверку до того, как первый
    /// из них подтвердит пустую очередь; последующие запросы не должны повторять
    /// одинаковые PostgreSQL seek после ожидания advisory-lock.
    /// </summary>
    internal bool TryCoalesceSerializedProbe()
    {
        if (Volatile.Read(ref emptyUntilTimestamp) <= getTimestamp()) return false;
        Interlocked.Increment(ref coalescedClaims);
        Interlocked.Increment(ref serializedCoalescedClaims);
        return true;
    }

    internal void MarkEmpty() =>
        Interlocked.Exchange(ref emptyUntilTimestamp, checked(getTimestamp() + cooldownTicks));

    internal void MarkWorkAvailable() => Interlocked.Exchange(ref emptyUntilTimestamp, 0);

    internal void MarkHeartbeat(Guid nodeId) => lastHeartbeatTimestamps[nodeId] = getTimestamp();

    private bool ReserveHeartbeat(Guid nodeId, long now)
    {
        while (true)
        {
            if (!lastHeartbeatTimestamps.TryGetValue(nodeId, out var previous))
            {
                if (lastHeartbeatTimestamps.TryAdd(nodeId, now)) return true;
                continue;
            }
            if (now - previous < heartbeatTicks) return false;
            if (lastHeartbeatTimestamps.TryUpdate(nodeId, now, previous)) return true;
        }
    }

    private static long DurationTicks(TimeSpan duration, long frequency)
    {
        var ticks = duration.TotalSeconds * frequency;
        if (ticks >= long.MaxValue) throw new ArgumentOutOfRangeException(nameof(duration));
        return Math.Max(1, checked((long)Math.Ceiling(ticks)));
    }
}

internal readonly record struct IdleClaimDecision(bool Coalesced, bool PersistHeartbeat);
