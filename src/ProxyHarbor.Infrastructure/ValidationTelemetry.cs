using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Точный rolling-срез persistent validation-аудита для UI и Prometheus.</summary>
public sealed record ValidationTelemetrySnapshot(
    int Attempts,
    int Checked,
    int Alive,
    int Deferred,
    int FailedRuns,
    int ActiveRuns,
    double ChecksPerSecond,
    long? EstimatedDrainSeconds);

/// <summary>Выполняет общий расчёт без зависимости от HTTP или конкретного EF provider.</summary>
public static class ValidationTelemetry
{
    /// <summary>Агрегирует завершённые run'ы окна и рассчитывает throughput/ETA due-очереди.</summary>
    public static ValidationTelemetrySnapshot Calculate(
        IEnumerable<ValidationRun> runs,
        DateTimeOffset windowStart,
        int due)
    {
        var materialized = runs.ToArray();
        var completed = materialized.Where(run => run.Status == "completed" &&
            run.FinishedAt >= windowStart).ToArray();
        var checkedCount = completed.Sum(run => run.Checked);
        var deferred = completed.Sum(run => run.Deferred);
        var attempts = checkedCount + deferred;
        // Wall-clock span сохраняет суммарную производительность нескольких реплик:
        // их перекрывающиеся durations нельзя складывать, иначе rate будет занижен.
        var elapsedSeconds = completed.Length == 0
            ? 0
            : Math.Max(0, (completed.Max(run => run.FinishedAt)!.Value -
                completed.Min(run => run.StartedAt)).TotalSeconds);
        var rate = elapsedSeconds > 0 ? attempts / elapsedSeconds : 0;
        var rawEta = rate > 0 && due > 0 ? Math.Ceiling(due / rate) : 0;
        var eta = rawEta > 0
            ? rawEta >= long.MaxValue ? long.MaxValue : (long)rawEta
            : (long?)null;
        return new ValidationTelemetrySnapshot(
            attempts,
            checkedCount,
            completed.Sum(run => run.Alive),
            deferred,
            materialized.Count(run => run.Status == "failed" && run.FinishedAt >= windowStart),
            materialized.Count(run => run.Status == "running" && run.FinishedAt == null),
            rate,
            eta);
    }
}
