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
        if (order.Status == notification.Status ||
            order.Status == PaymentStatuses.Paid && notification.Status != PaymentStatuses.Refunded)
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
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 50;
    private static readonly Action<ILogger, Exception?> CycleFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(1801, nameof(CycleFailed)),
        "Сверка зависших платежей завершила цикл с ошибкой; следующий цикл продолжит работу.");
    private static readonly Action<ILogger, Guid, string, Exception?> OrderFailed =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(1802, nameof(OrderFailed)),
            "Не удалось сверить заказ {OrderId} через {Provider}; он останется pending до следующей попытки.");

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
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
        var gateway = scope.ServiceProvider.GetRequiredService<PaymentGatewayClient>();
        var settlement = scope.ServiceProvider.GetRequiredService<PaymentSettlementService>();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - InitialAge;
        var candidates = await db.PaymentOrders.AsNoTracking()
            .Where(x => x.Status == PaymentStatuses.Pending && x.UpdatedAt <= cutoff &&
                (x.Provider == "yookassa" || x.Provider == "cloudpayments" || x.Provider == "robokassa" ||
                 x.Provider == "tbank" || x.Provider == "stripe" || x.Provider == "cryptomus"))
            .OrderBy(x => x.UpdatedAt).Take(BatchSize)
            .Select(x => new { x.Id, x.UpdatedAt }).ToArrayAsync(token);
        var checkedCount = 0;
        foreach (var candidate in candidates)
        {
            var claimed = await db.PaymentOrders
                .Where(x => x.Id == candidate.Id && x.Status == PaymentStatuses.Pending && x.UpdatedAt == candidate.UpdatedAt)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAt, now), token);
            if (claimed != 1) continue;
            db.ChangeTracker.Clear();
            var order = await db.PaymentOrders.AsNoTracking().SingleAsync(x => x.Id == candidate.Id, token);
            try
            {
                var notification = await gateway.CheckStatusAsync(order, token);
                if (notification is not null)
                    await settlement.ApplyAsync(order.Provider, notification, token);
                checkedCount++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { OrderFailed(logger, order.Id, order.Provider, exception); }
            db.ChangeTracker.Clear();
        }
        return checkedCount;
    }
}
