using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Ограниченная гонка уже проверенных DNS-адресов. Первый зависший IP не блокирует
/// fallback; победивший stream передаётся HTTP handler, остальные закрываются.
/// </summary>
internal sealed class PublicAddressConnector : IDisposable
{
    private static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);
    private readonly TimeProvider clock;
    private readonly ConcurrencyLimiter connections;

    internal PublicAddressConnector(int concurrency = 128, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 32);
        this.clock = clock ?? TimeProvider.System;
        connections = new(new ConcurrencyLimiterOptions
        {
            PermitLimit = concurrency,
            QueueLimit = concurrency * 32,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    internal async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect, CancellationToken token)
    {
        addresses = Interleave(addresses);
        // Атомарная резервация fan-out исключает deadlock: зависшие первые IP
        // разных запросов не могут занять permits, необходимые их fallback.
        // Очередь входит в общий ConnectTimeout handler и отменяется его token.
        using var reservation = await connections.AcquireAsync(addresses.Length, token);
        if (!reservation.IsAcquired) throw new HttpRequestException("Исчерпана очередь публичных TCP-соединений.");
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new List<Task<AttemptResult>>(addresses.Length);
        var attempts = new List<Task<AttemptResult>>(addresses.Length);
        Stream? winner = null;
        Exception? last = null;
        Task delay = Task.CompletedTask;
        var next = 0;
        try
        {
            try
            {
                while (next < addresses.Length || pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var completed = pending.FirstOrDefault(task => task.IsCompleted);
                    if (completed is not null)
                    {
                        var result = await completed;
                        if (result.Stream is not null) { winner = result.Stream; break; }
                        last = result.Error;
                        if (last is not SocketException and not IOException)
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(last!).Throw();
                        pending.Remove(completed);
                        continue;
                    }
                    if (next < addresses.Length && (pending.Count == 0 || delay.IsCompleted))
                    {
                        var attempt = AttemptAsync(addresses[next++], port, connect, race.Token);
                        pending.Add(attempt);
                        attempts.Add(attempt);
                        delay = Task.Delay(AttemptDelay, clock, race.Token);
                        continue;
                    }
                    if (next < addresses.Length) await Task.WhenAny(pending.Cast<Task>().Append(delay));
                    else await Task.WhenAny(pending);
                }
                if (winner is null)
                    throw new HttpRequestException("Не удалось соединиться ни с одним публичным адресом источника.", last);
            }
            finally
            {
                await race.CancelAsync();
                // В том числе поздний успех одновременно с отменой: не оставляем
                // открытые sockets/задачи после возврата или освобождения permits.
                foreach (var result in await Task.WhenAll(attempts))
                    if (result.Stream is not null && !ReferenceEquals(result.Stream, winner)) result.Stream.Dispose();
            }
            token.ThrowIfCancellationRequested();
            return winner;
        }
        catch
        {
            winner?.Dispose();
            throw;
        }
    }

    private static async Task<AttemptResult> AttemptAsync(IPAddress address, int port,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect, CancellationToken token)
    {
        try { return new(await connect(address, port, token), null); }
        // Собираем также неожиданные ошибки, чтобы finally смог дождаться всех
        // попыток и закрыть их streams. Основной цикл пробросит такую ошибку.
        catch (Exception exception) { return new(null, exception); }
    }

    private static IPAddress[] Interleave(IPAddress[] addresses)
    {
        var unique = addresses.Select(ip => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).Distinct().ToArray();
        var preferred = new Queue<IPAddress>(unique.Where(ip => ip.AddressFamily == unique[0].AddressFamily));
        var alternate = new Queue<IPAddress>(unique.Where(ip => ip.AddressFamily != unique[0].AddressFamily));
        var ordered = new List<IPAddress>(unique.Length);
        while (preferred.Count > 0 || alternate.Count > 0)
        {
            if (preferred.TryDequeue(out var first)) ordered.Add(first);
            if (alternate.TryDequeue(out var second)) ordered.Add(second);
        }
        return ordered.ToArray();
    }

    private sealed record AttemptResult(Stream? Stream, Exception? Error);

    public void Dispose() => connections.Dispose();
}
