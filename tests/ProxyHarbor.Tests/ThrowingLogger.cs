using Microsoft.Extensions.Logging;

namespace ProxyHarbor.Tests;

/// <summary>Детерминированный test double для проверки независимости pipeline от logging provider.</summary>
internal sealed class ThrowingLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        throw new InvalidOperationException("Deterministic logging provider failure.");
}
