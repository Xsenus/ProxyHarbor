using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProxyHarbor.Infrastructure;

/// <summary>Запускает сбор по расписанию; сбой одного цикла не останавливает сервис.</summary>
public sealed class CollectorWorker(ProxyCollector collector, IOptions<CollectorOptions> options, ILogger<CollectorWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CollectionFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1101, "CollectionFailed"), "Цикл сбора завершился ошибкой.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundWorkersEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.CollectionIntervalMinutes)));
        do
        {
            try { await collector.CollectAsync(stoppingToken); }
            catch (OperationAlreadyRunningException) { }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { CollectionFailed(logger, ex); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Непрерывно проверяет очередной пакет прокси с паузой между пустыми проходами.</summary>
public sealed class ValidatorWorker(ProxyValidator validator, IOptions<CollectorOptions> options, ILogger<ValidatorWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ValidationFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1102, "ValidationFailed"), "Цикл проверки завершился ошибкой.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundWorkersEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await validator.ValidateBatchAsync(stoppingToken);
                await Task.Delay(result.Checked == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationAlreadyRunningException)
            {
                // Ручной запуск уже использует локальный validator; повторим цикл после короткой паузы.
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                ValidationFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}
