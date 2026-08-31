using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class PaymentReconciliationWorkerTests
{
    [Theory]
    [InlineData(PaymentStatuses.Canceled, PaymentStatuses.Paid, true)]
    [InlineData(PaymentStatuses.Failed, PaymentStatuses.Paid, true)]
    [InlineData(PaymentStatuses.Paid, PaymentStatuses.Refunded, true)]
    [InlineData(PaymentStatuses.Paid, PaymentStatuses.Failed, false)]
    [InlineData(PaymentStatuses.Canceled, PaymentStatuses.Pending, false)]
    [InlineData(PaymentStatuses.Refunded, PaymentStatuses.Paid, false)]
    [InlineData(PaymentStatuses.Pending, PaymentStatuses.Pending, false)]
    public void SettlementTransitionPolicyProtectsMoneyAndAllowsLateConfirmation(
        string current, string incoming, bool expected) =>
        Assert.Equal(expected, PaymentSettlementService.CanApplyTransition(current, incoming));

    [Fact]
    public void TelegramHistoryRequiresOrderAmountAndUserToMatch()
    {
        var orderId = Guid.NewGuid();
        var candidate = new PaymentReconciliationWorker.TelegramPaymentCandidate(
            orderId, 100, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-5), 9001);
        var transaction = new TelegramStarTransaction(
            "charge-1", 100, DateTimeOffset.UtcNow, orderId.ToString("N"), 9001);

        Assert.True(PaymentReconciliationWorker.TryCreateTelegramNotification(
            candidate, transaction, out var notification));
        Assert.NotNull(notification);
        Assert.Equal(orderId, notification.OrderId);
        Assert.Equal("charge-1", notification.ProviderPaymentId);
        Assert.Equal(PaymentStatuses.Paid, notification.Status);
        Assert.Equal("XTR", notification.Currency);
    }

    [Theory]
    [InlineData("other-order", 100, 9001)]
    [InlineData("same-order", 99, 9001)]
    [InlineData("same-order", 100, 9002)]
    public void TelegramHistoryRejectsMismatchedTransaction(string payloadMode, long stars, long userId)
    {
        var orderId = Guid.NewGuid();
        var payload = payloadMode == "same-order" ? orderId.ToString("N") : Guid.NewGuid().ToString("N");
        var candidate = new PaymentReconciliationWorker.TelegramPaymentCandidate(
            orderId, 100, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-5), 9001);
        var transaction = new TelegramStarTransaction(
            "charge-1", stars, DateTimeOffset.UtcNow, payload, userId);

        Assert.False(PaymentReconciliationWorker.TryCreateTelegramNotification(
            candidate, transaction, out var notification));
        Assert.Null(notification);
    }

    [Fact]
    public async Task BoundedProcessorUsesConfiguredParallelismAndCountsOnlyCompletedItems()
    {
        var active = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processing = PaymentReconciliationWorker.ProcessBoundedAsync(
            Enumerable.Range(1, 24).ToArray(), 4, async item =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref peak, current);
                if (current == 4) started.TrySetResult();
                await release.Task;
                Interlocked.Decrement(ref active);
                return item % 2 == 0;
            }, CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, Volatile.Read(ref peak));
        Assert.Equal(4, Volatile.Read(ref active));
        release.SetResult();

        Assert.Equal(12, await processing);
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task BoundedProcessorRejectsInvalidParallelism()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PaymentReconciliationWorker.ProcessBoundedAsync(
                Array.Empty<int>(), 0, _ => Task.FromResult(true), CancellationToken.None));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(PaymentStatuses.Pending, true)]
    [InlineData(PaymentStatuses.Paid, false)]
    [InlineData(PaymentStatuses.Failed, false)]
    public void StaleReconciliationNeverLeavesCheckoutHanging(
        string? providerStatus, bool expected)
    {
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, PaymentReconciliationWorker.ShouldCancelAfterReconciliation(
            now.AddHours(-25), providerStatus, now));
        Assert.False(PaymentReconciliationWorker.ShouldCancelAfterReconciliation(
            now.AddHours(-23), providerStatus, now));
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

}
