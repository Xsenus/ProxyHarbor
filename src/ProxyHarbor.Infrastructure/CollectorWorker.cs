using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProxyHarbor.Infrastructure;

/// <summary>Запускает сбор по расписанию; сбой одного цикла не останавливает сервис.</summary>
public sealed class CollectorWorker(ProxyCollector collector, IOptions<CollectorOptions> options, ILogger<CollectorWorker> logger) : BackgroundService
{
    internal enum CycleOutcome { Succeeded, PeerOwned, Failed }
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OverrunCooldown = TimeSpan.FromSeconds(30);
    private static readonly Action<ILogger, Exception?> CollectionFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1101, "CollectionFailed"), "Цикл сбора завершился ошибкой.");
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundWorkersEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = TimeProvider.System.GetTimestamp();
            var outcome = CycleOutcome.Failed;
            try
            {
                await collector.CollectAsync(stoppingToken);
                outcome = CycleOutcome.Succeeded;
            }
            catch (OperationAlreadyRunningException)
            {
                // Другая реплика либо ручной запуск уже выполняет полный цикл.
                // Полный интервал не позволяет нескольким репликам создать lock storm.
                outcome = CycleOutcome.PeerOwned;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                OperationalLogBoundary.Write(() => CollectionFailed(logger, ex));
            }

            var elapsed = TimeProvider.System.GetElapsedTime(startedAt);
            var delay = NextDelay(options.Value.CollectionIntervalMinutes, outcome, elapsed);
            var cachedInterval = outcome == CycleOutcome.Succeeded && options.Value.CachedImportIntervalSeconds > 0
                ? TimeSpan.FromSeconds(Math.Max(30, options.Value.CachedImportIntervalSeconds))
                : (TimeSpan?)null;
            await WaitForNextCollectionAsync(delay, cachedInterval, ImportCachedAsync,
                Task.Delay, TimeProvider.System, stoppingToken);
        }
    }

    private async Task<bool> ImportCachedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var run = await collector.ImportCachedSourcesAsync(cancellationToken);
            return run is { CandidatesFound: > 0 };
        }
        catch (OperationAlreadyRunningException) { return false; }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            OperationalLogBoundary.Write(() => CollectionFailed(logger, exception));
            return false;
        }
    }

    // Измеряем оставшееся время от одного deadline: cached-проход не добавляет
    // свой duration к штатному интервалу сетевого polling.
    internal static async Task WaitForNextCollectionAsync(
        TimeSpan delay, TimeSpan? cachedInterval,
        Func<CancellationToken, Task<bool>> importCachedAsync,
        Func<TimeSpan, CancellationToken, Task> waitAsync,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        while (true)
        {
            var remaining = delay - timeProvider.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero) return;
            if (cachedInterval is null || cachedInterval <= TimeSpan.Zero || remaining <= cachedInterval)
            {
                await waitAsync(remaining, cancellationToken);
                return;
            }
            await waitAsync(cachedInterval.Value, cancellationToken);
            if (timeProvider.GetElapsedTime(startedAt) >= delay) return;
            if (!await importCachedAsync(cancellationToken)) cachedInterval = null;
        }
    }

    /// <summary>
    /// Сохраняет заданный start-to-start cadence для штатного быстрого цикла, но
    /// не допускает немедленного повторного запуска после overrun или общего сбоя.
    /// </summary>
    internal static TimeSpan NextDelay(int intervalMinutes, CycleOutcome outcome, TimeSpan elapsed)
    {
        var regularDelay = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        return outcome switch
        {
            CycleOutcome.Succeeded when elapsed < regularDelay => regularDelay - elapsed,
            CycleOutcome.Succeeded => OverrunCooldown,
            CycleOutcome.PeerOwned => regularDelay,
            CycleOutcome.Failed => regularDelay <= FailureRetryDelay ? regularDelay : FailureRetryDelay,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }
}

/// <summary>Непрерывно проверяет очередной пакет прокси с паузой между пустыми проходами.</summary>
public sealed class ValidatorWorker(
    ProxyValidator validator,
    LocalValidationStandbyGate standbyGate,
    ValidationWakeSignal validationWakeSignal,
    IOptions<CollectorOptions> options,
    ILogger<ValidatorWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ValidationFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1102, "ValidationFailed"), "Цикл проверки завершился ошибкой.");
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundWorkersEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await standbyGate.ShouldStandByAsync(stoppingToken))
                {
                    await validationWakeSignal.WaitAsync(
                        LocalValidationStandbyGate.StandbyPollInterval,
                        stoppingToken);
                    continue;
                }

                var result = await validator.ValidateBatchAsync(stoppingToken);
                await validationWakeSignal.WaitAsync(
                    NextDelay(result.Checked, result.Deferred),
                    stoppingToken);
            }
            catch (OperationAlreadyRunningException)
            {
                // Ручной запуск уже использует локальный validator; повторим цикл после короткой паузы.
                await validationWakeSignal.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                OperationalLogBoundary.Write(() => ValidationFailed(logger, ex));
                await validationWakeSignal.WaitAsync(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Долго ждёт только после действительно пустого прохода. Deferred-only пакет
    /// уже освободил lease, но за ним в очереди могут оставаться другие due-прокси.
    /// </summary>
    internal static TimeSpan NextDelay(int checkedCount, int deferredCount) =>
        checkedCount == 0 && deferredCount == 0
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromSeconds(1);
}
