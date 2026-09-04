using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace ProxyHarbor.Infrastructure;

/// <summary>Одна партия TCP-проверок: ограничивает sockets, а не только число DNS endpoint.</summary>
internal sealed class VpnTcpProbe : IDisposable
{
    private const int MaximumAddresses = 32;
    private static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);
    private readonly TimeProvider clock;
    private readonly ConcurrencyLimiter connections;

    internal VpnTcpProbe(int concurrency, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(concurrency, 1_000);
        this.clock = clock ?? TimeProvider.System;
        // Резервируем весь DNS fan-out атомарно ДО сетевого тайм-аута. Иначе 800
        // зависших первых IP займут все permits и заблокируют собственный fallback.
        connections = new(new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Max(MaximumAddresses, concurrency),
            QueueLimit = concurrency * MaximumAddresses,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    internal Task<int> ProbeAsync(string host, int port, int timeoutSeconds, CancellationToken token) =>
        ProbeCoreAsync(host, port, TimeSpan.FromSeconds(timeoutSeconds),
            static (name, cancellation) => Dns.GetHostAddressesAsync(name, cancellation),
            ConnectAsync, token);

    internal async Task<int> ProbeCoreAsync(
        string host, int port, TimeSpan timeout,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, Task> connect,
        CancellationToken token)
    {
        if (port is < 1 or > 65_535) throw new IOException("VPN endpoint содержит недопустимый порт");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        token.ThrowIfCancellationRequested();
        var startedAt = clock.GetTimestamp();
        IPAddress[] addresses;
        using (var dnsDeadline = new CancellationTokenSource(timeout, clock))
        using (var dnsCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, dnsDeadline.Token))
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal] : await resolve(host, dnsCancellation.Token);
            dnsCancellation.Token.ThrowIfCancellationRequested();
        }
        if (addresses.Length is 0 or > MaximumAddresses || addresses.Any(x => !NetworkSafety.IsPublicAddress(x)))
            throw new IOException("DNS VPN пуст, содержит локальный или служебный адрес либо превышает лимит 32");
        addresses = Interleave(addresses);
        var dnsElapsed = clock.GetElapsedTime(startedAt);
        var remaining = timeout - dnsElapsed;
        if (remaining <= TimeSpan.Zero) throw new OperationCanceledException("VPN DNS timeout");

        // Ожидание своей очереди — не задержка удалённого сервера и не его отказ.
        // Оно отменяется остановкой партии, но не расходует timeout и не входит в latency.
        using var reservation = await connections.AcquireAsync(addresses.Length, token);
        if (!reservation.IsAcquired)
            throw new InvalidOperationException("Исчерпана очередь TCP-проверок VPN");
        token.ThrowIfCancellationRequested();
        using var networkDeadline = new CancellationTokenSource(remaining, clock);
        using var networkCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, networkDeadline.Token);
        startedAt = clock.GetTimestamp();
        await RaceAsync(addresses, port, connect, networkCancellation.Token);
        networkCancellation.Token.ThrowIfCancellationRequested();
        return (int)Math.Min((dnsElapsed + clock.GetElapsedTime(startedAt)).TotalMilliseconds, int.MaxValue);
    }

    private async Task RaceAsync(IPAddress[] addresses, int port,
        Func<IPAddress, int, CancellationToken, Task> connect, CancellationToken token)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new List<Task<Exception?>>(addresses.Length);
        var attempts = new List<Task<Exception?>>(addresses.Length);
        Task delay = Task.CompletedTask;
        var next = 0;
        Exception? last = null;
        try
        {
            while (next < addresses.Length || pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                // Сначала обрабатываем готовый успех: не открываем лишний socket,
                // если connect завершился одновременно с таймером следующей попытки.
                var completed = pending.FirstOrDefault(task => task.IsCompleted);
                if (completed is not null)
                {
                    last = await completed;
                    if (last is null) return;
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
                if (pending.Count == 0) break;
                if (next < addresses.Length) await Task.WhenAny(pending.Cast<Task>().Append(delay));
                else await Task.WhenAny(pending);
            }
            throw new IOException("VPN endpoint недоступен по всем публичным адресам", last);
        }
        finally
        {
            await race.CancelAsync();
            // До возврата permits дожидаемся закрытия ВСЕХ sockets, включая гонку
            // двух успешных connect. Нет fire-and-forget проверок после результата.
            await Task.WhenAll(attempts);
        }
    }

    private static async Task<Exception?> AttemptAsync(IPAddress address, int port,
        Func<IPAddress, int, CancellationToken, Task> connect, CancellationToken token)
    {
        try { await connect(address, port, token); return null; }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
        { return exception; }
    }

    private static IPAddress[] Interleave(IPAddress[] addresses)
    {
        var unique = addresses.Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).Distinct().ToArray();
        var preferred = new Queue<IPAddress>(unique.Where(address => address.AddressFamily == unique[0].AddressFamily));
        var alternate = new Queue<IPAddress>(unique.Where(address => address.AddressFamily != unique[0].AddressFamily));
        var ordered = new List<IPAddress>(unique.Length);
        while (preferred.Count > 0 || alternate.Count > 0)
        {
            if (preferred.TryDequeue(out var first)) ordered.Add(first);
            if (alternate.TryDequeue(out var second)) ordered.Add(second);
        }
        return ordered.ToArray();
    }

    internal static async Task ConnectAsync(IPAddress address, int port, CancellationToken token)
    {
        using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        await client.ConnectAsync(address, port, token);
    }

    public void Dispose() => connections.Dispose();
}
