using System.Collections.Concurrent;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Потокобезопасно дедуплицирует кандидатов и удерживает не более заданного числа
/// уникальных endpoint'ов. Признак лимита выставляется только после фактического
/// отказа новому уникальному элементу, а не при точном заполнении ёмкости.
/// </summary>
internal sealed class BoundedProxyCandidateSet
{
    private readonly ConcurrentDictionary<ProxyCandidateKey, byte> _items;
    private readonly int _limit;
    private int _count;
    private int _limitReached;

    internal BoundedProxyCandidateSet(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _limit = limit;
        _items = new ConcurrentDictionary<ProxyCandidateKey, byte>(
            Environment.ProcessorCount,
            Math.Min(limit, 4_096));
    }

    /// <summary>
    /// Возвращает true только для нового элемента, оставшегося в bounded-наборе.
    /// Дубликаты и уникальные элементы сверх лимита возвращают false.
    /// </summary>
    internal bool TryAdd((string Host, int Port, ProxyProtocol Protocol) candidate)
        => TryAdd(ProxyCandidateKey.Parse(candidate.Host, candidate.Port, candidate.Protocol));

    /// <summary>Горячий collector-path не материализует каноническую IP-строку.</summary>
    internal bool TryAdd(ProxyCandidateKey candidate)
    {
        if (!_items.TryAdd(candidate, 0)) return false;

        var count = Interlocked.Increment(ref _count);
        if (count <= _limit) return true;

        // Удаляется только элемент, который успешно добавил текущий поток. Другой
        // поток не может владеть тем же ключом, пока эта запись присутствует.
        if (!_items.TryRemove(candidate, out _))
            throw new InvalidOperationException("Нарушена атомарность bounded-набора кандидатов.");
        Interlocked.Decrement(ref _count);
        Interlocked.Exchange(ref _limitReached, 1);
        return false;
    }

    /// <summary>Точное число сохранённых уникальных кандидатов после завершения producers.</summary>
    internal int Count => Volatile.Read(ref _count);

    /// <summary>Снимок endpoint'ов для последующего PostgreSQL binary COPY.</summary>
    internal IEnumerable<(string Host, int Port, ProxyProtocol Protocol)> Items =>
        _items.Keys.Select(static candidate => candidate.ToEndpoint());

    /// <summary>Истина только если хотя бы один новый уникальный endpoint был отброшен.</summary>
    internal bool LimitReached => Volatile.Read(ref _limitReached) != 0;
}
