namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Изолирует correctness operational pipelines от сторонних logging providers.
/// Логирование является наблюдаемостью: его отказ не может заменить primary exception,
/// превратить завершённую операцию в ошибку либо остановить background worker.
/// </summary>
internal static class OperationalLogBoundary
{
    internal static void Write(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        try { write(); }
        catch (Exception)
        {
            // У неисправного logging provider нет безопасного вторичного канала:
            // повторная запись могла бы рекурсивно вызвать тот же отказ.
        }
    }
}
