using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

internal enum PaymentSettlementResult { Applied, Unchanged, OrderNotFound, Mismatch }

/// <summary>Единообразно применяет доверенный результат webhook или прямой сверки шлюза.</summary>
public sealed class PaymentSettlementService(
    ProxyHarborDbContext db,
    UserManager<ApplicationUser> users)
{
    internal async Task<PaymentSettlementResult> ApplyAsync(
        string providerCode, PaymentNotification notification, CancellationToken token)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ApplyCoreAsync(providerCode, notification, token));
    }

    private async Task<PaymentSettlementResult> ApplyCoreAsync(
        string providerCode, PaymentNotification notification, CancellationToken token)
    {
        // A transient PostgreSQL failure can replay this delegate. Never retain entities
        // from an aborted attempt in the scoped DbContext.
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var order = await db.PaymentOrders.SingleOrDefaultAsync(x => x.Id == notification.OrderId, token);
        if (order is null) return PaymentSettlementResult.OrderNotFound;
        if (order.Provider != providerCode || order.AmountMinor != notification.AmountMinor ||
            !string.Equals(order.Currency, notification.Currency, StringComparison.OrdinalIgnoreCase))
            return PaymentSettlementResult.Mismatch;
        if (!CanApplyTransition(order.Status, notification.Status))
        {
            await transaction.CommitAsync(token);
            return PaymentSettlementResult.Unchanged;
        }

        order.ProviderPaymentId ??= notification.ProviderPaymentId;
        if (!string.Equals(order.ProviderPaymentId, notification.ProviderPaymentId, StringComparison.Ordinal))
            return PaymentSettlementResult.Mismatch;
        order.Status = notification.Status;
        order.PaymentMethod = notification.PaymentMethod ?? order.PaymentMethod;
        order.PaymentInstrument = notification.PaymentInstrument ?? order.PaymentInstrument;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        if (notification.Status == PaymentStatuses.Paid)
        {
            order.PaidAt = DateTimeOffset.UtcNow;
            var subscription = await db.Subscriptions.SingleAsync(x => x.UserId == order.UserId, token);
            var begins = subscription.ExpiresAt is { } expires && expires > order.PaidAt ? expires : order.PaidAt.Value;
            subscription.Plan = order.Plan;
            subscription.Status = SubscriptionStatuses.Active;
            if (subscription.ExpiresAt is null || subscription.ExpiresAt <= order.PaidAt.Value)
                subscription.StartedAt = order.PaidAt.Value;
            subscription.ExpiresAt = begins.AddDays(order.DurationDays);
            subscription.ExternalCustomerId ??= order.UserId.ToString("D");
            subscription.ExternalSubscriptionId = notification.ProviderPaymentId;
            subscription.UpdatedAt = order.PaidAt.Value;
            var account = await users.FindByIdAsync(order.UserId.ToString());
            if (account is not null && !await users.IsInRoleAsync(account, UserRoles.Subscriber))
                await users.AddToRoleAsync(account, UserRoles.Subscriber);
            await ReferralRewards.GrantForPurchaseAsync(db, users, order, order.PaidAt.Value, token);
        }
        else if (notification.Status == PaymentStatuses.Refunded)
        {
            var subscription = await db.Subscriptions.SingleAsync(x => x.UserId == order.UserId, token);
            if (string.Equals(subscription.ExternalSubscriptionId, notification.ProviderPaymentId, StringComparison.Ordinal))
            {
                subscription.Status = SubscriptionStatuses.Canceled;
                subscription.ExpiresAt = DateTimeOffset.UtcNow;
                subscription.UpdatedAt = DateTimeOffset.UtcNow;
                var account = await users.FindByIdAsync(order.UserId.ToString());
                if (account is not null && await users.IsInRoleAsync(account, UserRoles.Subscriber))
                    await users.RemoveFromRoleAsync(account, UserRoles.Subscriber);
            }
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return PaymentSettlementResult.Applied;
    }

    /// <summary>
    /// Не позволяет запоздалому pending/failed откатить подтверждённые деньги.
    /// Служебно завершённый заказ при этом можно восстановить доверенным paid-событием.
    /// Refunded является окончательным состоянием.
    /// </summary>
    internal static bool CanApplyTransition(string current, string incoming)
    {
        if (string.Equals(current, incoming, StringComparison.Ordinal)) return false;
        return current switch
        {
            PaymentStatuses.Paid => incoming == PaymentStatuses.Refunded,
            PaymentStatuses.Refunded => false,
            PaymentStatuses.Canceled or PaymentStatuses.Failed =>
                incoming is PaymentStatuses.Paid or PaymentStatuses.Refunded,
            _ => true
        };
    }
}

/// <summary>
/// Страховочная сверка pending-заказов. Webhook остаётся основным каналом, а worker
/// забирает только давно не обновлявшиеся заказы и атомарно распределяет их между репликами.
/// </summary>
public sealed class PaymentReconciliationWorker(
    IServiceScopeFactory scopes,
    ILogger<PaymentReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan InitialAge = TimeSpan.FromMinutes(2);
    // YooMoney прекращает автоматические повторы HTTP-уведомления через час.
    // Сутки дают большой запас для временного простоя и отделяют незавершённый
    // checkout от актуальных заказов. Доверенное позднее уведомление всё равно
    // может перевести canceled-заказ в paid через PaymentSettlementService.
    private static readonly TimeSpan StalePendingAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 50;
    private const int MaxParallelism = 8;
    private const int TelegramHistoryPageSize = 100;
    private const int TelegramHistoryMaxPages = 20;
    private static readonly Action<ILogger, Exception?> CycleFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(1801, nameof(CycleFailed)),
        "Сверка зависших платежей завершила цикл с ошибкой; следующий цикл продолжит работу.");
    private static readonly Action<ILogger, Guid, string, Exception?> OrderFailed =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(1802, nameof(OrderFailed)),
            "Не удалось сверить заказ {OrderId} через {Provider}; он останется pending до следующей попытки.");
    private static readonly Action<ILogger, int, Exception?> TelegramRecovered = LoggerMessage.Define<int>(
        LogLevel.Information, new EventId(1803, nameof(TelegramRecovered)),
        "По журналу Telegram Stars восстановлено платежей: {Count}.");
    private static readonly Action<ILogger, string, int, Exception?> StaleCanceled = LoggerMessage.Define<string, int>(
        LogLevel.Information, new EventId(1804, nameof(StaleCanceled)),
        "Завершено устаревших неподтверждённых заказов через {Provider}: {Count}.");
    private static readonly Action<ILogger, Exception?> TelegramHistoryFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1805, nameof(TelegramHistoryFailed)),
        "Журнал Telegram Stars временно недоступен; сверка остальных шлюзов продолжится.");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { CycleFailed(logger, exception); }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - InitialAge;
        var completed = 0;
        try { completed = await ReconcileTelegramStarsAsync(now, cutoff, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { TelegramHistoryFailed(logger, exception); }
        completed += await CancelStaleWebhookOnlyAsync(now, token);
        PaymentReconciliationCandidate[] candidates;
        using (var discoveryScope = scopes.CreateScope())
        {
            var discoveryDb = discoveryScope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
            candidates = await discoveryDb.PaymentOrders.AsNoTracking()
                .Where(x => x.Status == PaymentStatuses.Pending && x.UpdatedAt <= cutoff &&
                    (x.Provider == "yookassa" || x.Provider == "cloudpayments" || x.Provider == "robokassa" ||
                     x.Provider == "tbank" || x.Provider == "stripe" || x.Provider == "cryptomus"))
                .OrderBy(x => x.UpdatedAt).Take(BatchSize)
                .Select(x => new PaymentReconciliationCandidate(x.Id, x.UpdatedAt, x.Provider)).ToArrayAsync(token);
        }

        // Внешняя проверка статуса может занимать секунды. Изолированный scope на
        // заказ делает DbContext/UserManager thread-safe, а небольшой предел не
        // создаёт всплеск запросов к платёжным шлюзам. Compare-and-swap по UpdatedAt
        // ниже по-прежнему гарантирует, что несколько API-реплик не сверят один заказ.
        completed += await ProcessBoundedAsync(candidates, MaxParallelism,
            candidate => ReconcileAsync(candidate, now, token), token);
        return completed;
    }

    /// <summary>
    /// Восстанавливает потерянные successful_payment по официальному журналу Stars.
    /// Один постраничный снимок обслуживает сразу весь batch и не создаёт N запросов
    /// к Telegram для N заказов.
    /// </summary>
    private async Task<int> ReconcileTelegramStarsAsync(
        DateTimeOffset now, DateTimeOffset initialCutoff, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
        var candidates = await db.PaymentOrders.AsNoTracking()
            .Where(x => x.Status == PaymentStatuses.Pending && x.Provider == "telegram_stars" &&
                x.UpdatedAt <= initialCutoff)
            .OrderBy(x => x.UpdatedAt).Take(BatchSize)
            .Select(x => new TelegramPaymentCandidate(
                x.Id, x.AmountMinor, x.CreatedAt, x.UpdatedAt,
                x.User.TelegramChat == null ? null : x.User.TelegramChat.TelegramUserId))
            .ToArrayAsync(token);
        if (candidates.Length == 0) return 0;

        var options = await scope.ServiceProvider.GetRequiredService<ITelegramBotConfigurationStore>().GetAsync(token);
        if (!options.Ready) return 0;
        var api = scope.ServiceProvider.GetRequiredService<TelegramBotApiClient>();
        var oldestRequired = candidates.Min(x => x.CreatedAt);
        var transactions = new List<TelegramStarTransaction>();
        var historyCoversBatch = false;
        for (var page = 0; page < TelegramHistoryMaxPages; page++)
        {
            var values = await api.GetStarTransactionsAsync(
                options, page * TelegramHistoryPageSize, TelegramHistoryPageSize, token);
            transactions.AddRange(values);
            if (values.Length < TelegramHistoryPageSize)
            {
                historyCoversBatch = true;
                break;
            }
            if (values.Min(x => x.CreatedAt) <= oldestRequired)
            {
                historyCoversBatch = true;
                break;
            }
        }

        var recovered = 0;
        var recoveredOrders = new HashSet<Guid>();
        var settlement = scope.ServiceProvider.GetRequiredService<PaymentSettlementService>();
        foreach (var candidate in candidates)
        {
            PaymentNotification? notification = null;
            foreach (var transaction in transactions)
                if (TryCreateTelegramNotification(candidate, transaction, out notification)) break;
            if (notification is null) continue;
            var result = await settlement.ApplyAsync("telegram_stars", notification, token);
            if (result is PaymentSettlementResult.Applied or PaymentSettlementResult.Unchanged)
            {
                recoveredOrders.Add(candidate.Id);
                recovered++;
            }
        }
        if (recovered > 0) TelegramRecovered(logger, recovered, null);

        // Не отменяем старые invoice, пока лимит страниц не покрыл дату самого
        // раннего кандидата: высокая активность бота не должна создавать false negative.
        if (!historyCoversBatch) return recovered;
        var staleCutoff = now - StalePendingAge;
        var canceled = 0;
        foreach (var candidate in candidates.Where(x => x.CreatedAt <= staleCutoff && !recoveredOrders.Contains(x.Id)))
        {
            canceled += await db.PaymentOrders
                .Where(x => x.Id == candidate.Id && x.Status == PaymentStatuses.Pending &&
                    x.UpdatedAt == candidate.UpdatedAt)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, PaymentStatuses.Canceled)
                    .SetProperty(x => x.UpdatedAt, now), token);
        }
        if (canceled > 0) StaleCanceled(logger, "telegram_stars", canceled, null);
        return recovered + canceled;
    }

    /// <summary>
    /// YooMoney не предоставляет безопасную проверку исходной списанной суммы без
    /// отдельного OAuth-контура. NOWPayments invoice не сообщает payment ID до первого
    /// IPN. После суточного окна неподтверждённые формы считаются оставленными;
    /// подписанный поздний webhook по-прежнему имеет право восстановить оплату.
    /// </summary>
    private async Task<int> CancelStaleWebhookOnlyAsync(DateTimeOffset now, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
        var cutoff = now - StalePendingAge;
        var canceled = 0;
        foreach (var provider in new[] { "yoomoney", "nowpayments" })
        {
            var providerCanceled = await db.PaymentOrders
                .Where(x => x.Status == PaymentStatuses.Pending && x.Provider == provider &&
                    x.UpdatedAt <= cutoff)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, PaymentStatuses.Canceled)
                    .SetProperty(x => x.UpdatedAt, now), token);
            if (providerCanceled > 0) StaleCanceled(logger, provider, providerCanceled, null);
            canceled += providerCanceled;
        }
        return canceled;
    }

    /// <summary>Строго связывает операцию Stars с заказом, суммой и Telegram-пользователем.</summary>
    internal static bool TryCreateTelegramNotification(
        TelegramPaymentCandidate candidate,
        TelegramStarTransaction transaction,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PaymentNotification? notification)
    {
        notification = null;
        if (candidate.TelegramUserId is null || transaction.UserId != candidate.TelegramUserId ||
            transaction.Stars != candidate.AmountMinor ||
            !Guid.TryParseExact(transaction.InvoicePayload, "N", out var orderId) || orderId != candidate.Id)
            return false;
        notification = new PaymentNotification(
            candidate.Id, transaction.Id, PaymentStatuses.Paid, candidate.AmountMinor, "XTR",
            "telegram_stars", "Telegram Stars");
        return true;
    }

    private async Task<bool> ReconcileAsync(
        PaymentReconciliationCandidate candidate,
        DateTimeOffset claimedAt,
        CancellationToken token)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
            var claimed = await db.PaymentOrders
                .Where(x => x.Id == candidate.Id && x.Status == PaymentStatuses.Pending && x.UpdatedAt == candidate.UpdatedAt)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, claimedAt), token);
            if (claimed != 1) return false;

            var order = await db.PaymentOrders.AsNoTracking().SingleAsync(x => x.Id == candidate.Id, token);
            var gateway = scope.ServiceProvider.GetRequiredService<PaymentGatewayClient>();
            var notification = await gateway.CheckStatusAsync(order, token);
            if (notification is not null)
            {
                var settlement = scope.ServiceProvider.GetRequiredService<PaymentSettlementService>();
                await settlement.ApplyAsync(order.Provider, notification, token);
            }
            if (ShouldCancelAfterReconciliation(order.CreatedAt, notification?.Status, claimedAt))
            {
                await db.PaymentOrders
                    .Where(x => x.Id == order.Id && x.Status == PaymentStatuses.Pending && x.UpdatedAt == claimedAt)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, PaymentStatuses.Canceled)
                        .SetProperty(x => x.UpdatedAt, claimedAt), token);
            }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            OrderFailed(logger, candidate.Id, candidate.Provider, exception);
            return false;
        }
    }

    /// <summary>
    /// Убирает локально зависший checkout только после успешного ответа шлюза
    /// (null = операция не найдена). Ошибка сети бросает исключение раньше и никогда
    /// не превращается в отмену. Late paid/refunded затем разрешён transition policy.
    /// </summary>
    internal static bool ShouldCancelAfterReconciliation(
        DateTimeOffset createdAt, string? providerStatus, DateTimeOffset now) =>
        createdAt <= now - StalePendingAge &&
        (providerStatus is null || providerStatus == PaymentStatuses.Pending);

    /// <summary>Выполняет I/O-операции с фиксированным пределом и без общей mutable state.</summary>
    internal static async Task<int> ProcessBoundedAsync<T>(
        IReadOnlyCollection<T> items,
        int maxParallelism,
        Func<T, Task<bool>> process,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelism, 1);
        var completed = 0;
        await Parallel.ForEachAsync(items,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = token },
            async (item, _) =>
            {
                if (await process(item)) Interlocked.Increment(ref completed);
            });
        return completed;
    }

    private readonly record struct PaymentReconciliationCandidate(Guid Id, DateTimeOffset UpdatedAt, string Provider);

    internal readonly record struct TelegramPaymentCandidate(
        Guid Id, long AmountMinor, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long? TelegramUserId);
}
