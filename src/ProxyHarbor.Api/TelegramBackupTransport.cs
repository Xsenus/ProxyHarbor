using System.Net;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>
/// Отправляет резервные копии через тот же отказоустойчивый набор Telegram-маршрутов,
/// который обслуживает polling, CRM и пользовательские ответы бота.
/// </summary>
public sealed class TelegramBackupTransport(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory clients,
    TelegramTransportHealth transportHealth,
    TelegramProxyHttpClientPool proxyClients) : ITelegramBackupTransport
{
    /// <inheritdoc />
    public async Task SendAsync(
        string path,
        string caption,
        string botToken,
        string chatId,
        CancellationToken token)
    {
        var attempts = await ResolveAttemptsAsync(token);
        if (attempts.Length == 0)
            throw new HttpRequestException("Для отправки backup не осталось доступных маршрутов Telegram.");

        foreach (var proxy in attempts)
        {
            TelegramProxyHttpClientPool.ClientLease? lease = null;
            try
            {
                var client = proxy is null
                    ? clients.CreateClient("telegram")
                    : (lease = proxyClients.Acquire(proxy)).Client;
                await TelegramBackupSender.SendAsync(
                    client, path, caption, botToken, chatId, token);
                transportHealth.MarkSucceeded(proxy);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception) when (IsRouteFailure(exception))
            {
                transportHealth.MarkFailed(proxy, DateTimeOffset.UtcNow);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        // Не сохраняем последний transport exception: URI Bot API содержит token,
        // а multipart — chat id и имя резервной копии.
        throw new HttpRequestException(
            "Telegram недоступен через все настроенные маршруты отправки backup.");
    }

    internal async Task<TelegramProxyOptions?[]> ResolveAttemptsAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var configuration = scope.ServiceProvider
            .GetRequiredService<ITelegramBotConfigurationStore>();
        var options = await configuration.GetAsync(token);
        var result = TelegramBotApiClient.ConnectionAttempts(options).ToList();

        if (options.TransportMode == TelegramTransportModes.Auto)
        {
            if (result.Count > 0 && result[^1] is null) result.RemoveAt(result.Count - 1);
            var known = result.Where(value => value is not null)
                .Select(value => $"{value!.Host.ToLowerInvariant()}:{value.Port}")
                .ToHashSet(StringComparer.Ordinal);
            var candidates = scope.ServiceProvider
                .GetRequiredService<ITelegramProxyCandidateProvider>();
            foreach (var candidate in await candidates.GetCandidatesAsync(token))
                if (known.Add($"{candidate.Host.ToLowerInvariant()}:{candidate.Port}"))
                    result.Add(candidate);
            result.Add(null);
        }

        var now = DateTimeOffset.UtcNow;
        var available = result.Where(value => transportHealth.IsAvailable(value, now)).ToArray();
        // Один маршрут всё равно проверяется после общего cooldown: восстановившаяся
        // сеть не должна ждать локального таймера до следующей суточной копии.
        return available.Length > 0 ? available : result.Take(1).ToArray();
    }

    private static bool IsRouteFailure(HttpRequestException exception) =>
        exception.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int?)exception.StatusCode >= 500;
}
