using System.Threading.Channels;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Передаёт validator'у bounded уведомление о новых кандидатах без очереди событий
/// на каждый source или proxy. Несколько pulse до чтения объединяются в один wake.
/// </summary>
public sealed class ValidationWakeSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    /// <summary>Будит ближайшее idle-ожидание; повторный pending-сигнал не накапливается.</summary>
    internal void Pulse() => _channel.Writer.TryWrite(0);

    /// <summary>Ожидает pulse либо штатный timeout, сохраняя немедленную отмену shutdown.</summary>
    internal async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero) return;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            _ = await _channel.Reader.ReadAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Обычное окончание idle-delay не является ошибкой worker'а.
        }
    }
}
