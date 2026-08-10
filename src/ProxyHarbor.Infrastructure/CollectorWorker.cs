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
            await Task.Delay(NextDelay(options.Value.CollectionIntervalMinutes, outcome, elapsed), stoppingToken);
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
