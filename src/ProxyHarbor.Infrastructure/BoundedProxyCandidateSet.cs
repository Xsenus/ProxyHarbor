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
    private readonly object _admissionGate = new();
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
        => TryAdd(ProxyCandidateKey.Parse(candidate.Host, candidate.Port, candidate.Protocol), preferred: false);

    /// <summary>Горячий collector-path не материализует каноническую IP-строку.</summary>
    internal bool TryAdd(ProxyCandidateKey candidate, bool preferred = false)
        => Admit(candidate, preferred) == CandidateAdmission.Added;

    /// <summary>Дубликат уже гарантированно принят; только отказ из-за квоты запрещает продвижение курсора.</summary>
    internal bool TryAccept(ProxyCandidateKey candidate, bool preferred = false)
        => Admit(candidate, preferred) != CandidateAdmission.Full;

    private CandidateAdmission Admit(ProxyCandidateKey candidate, bool preferred)
    {
        var marker = preferred ? (byte)1 : (byte)0;
        // Короткая critical section не публикует временную запись сверх квоты:
        // другой producer не должен принять её за подтверждённый дубликат и
        // продвинуть свой курсор перед последующим удалением этой записи.
        lock (_admissionGate)
        {
            if (_items.TryGetValue(candidate, out var existing))
            {
                if (preferred && existing == 0) _items[candidate] = marker;
                return CandidateAdmission.Duplicate;
            }
            if (_count >= _limit)
            {
                Interlocked.Exchange(ref _limitReached, 1);
                return CandidateAdmission.Full;
            }
            if (!_items.TryAdd(candidate, marker))
                throw new InvalidOperationException("Нарушена атомарность bounded-набора кандидатов.");
            Interlocked.Increment(ref _count);
            return CandidateAdmission.Added;
        }
    }

    private enum CandidateAdmission { Added, Duplicate, Full }

    /// <summary>Точное число сохранённых уникальных кандидатов после завершения producers.</summary>
    internal int Count => Volatile.Read(ref _count);

    /// <summary>Снимок endpoint'ов для последующего PostgreSQL binary COPY.</summary>
    internal IEnumerable<(string Host, int Port, ProxyProtocol Protocol)> Items =>
        _items.Keys.Select(static candidate => candidate.ToEndpoint());

    /// <summary>Снимок endpoint'ов вместе с приоритетом платного provenance.</summary>
    internal IEnumerable<(string Host, int Port, ProxyProtocol Protocol, bool Preferred)> ImportItems =>
        _items.Select(static pair =>
        {
            var endpoint = pair.Key.ToEndpoint();
            return (endpoint.Host, endpoint.Port, endpoint.Protocol, pair.Value != 0);
        });

    /// <summary>Истина только если хотя бы один новый уникальный endpoint был отброшен.</summary>
    internal bool LimitReached => Volatile.Read(ref _limitReached) != 0;
}
