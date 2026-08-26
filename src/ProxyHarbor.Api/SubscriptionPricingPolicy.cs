using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>
/// Единая ценовая политика сайта, платёжных шлюзов и Telegram Stars. Базой служит
/// цена одного дня, а длинные периоды получают возрастающую скидку до 20% за год.
/// </summary>
public static class SubscriptionPricingPolicy
{
    /// <summary>Поддерживаемые сроки и скидки относительно ежедневной покупки.</summary>
    public static readonly IReadOnlyList<SubscriptionPeriod> Periods =
    [
        new("day", 1, 0m, "1 день"),
        new("week", 7, 5m, "1 неделя"),
        new("month", 30, 10m, "1 месяц"),
        new("quarter", 90, 13m, "3 месяца"),
        new("half-year", 180, 16m, "6 месяцев"),
        new("year", 365, 20m, "1 год")
    ];

    /// <summary>Строит полный каталог из управляемой дневной цены.</summary>
    public static Dictionary<string, PaymentProductOptions> Build(
        long dailyAmountMinor,
        string currency,
        IReadOnlyDictionary<int, decimal>? discounts = null,
        bool enabled = true)
    {
        if (dailyAmountMinor is < 1 or > 1_000_000_000) throw new ArgumentOutOfRangeException(nameof(dailyAmountMinor));
        currency = currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) throw new ArgumentException("Invalid ISO currency.", nameof(currency));
        return Periods.ToDictionary(period => $"unlimited-{period.Code}", period =>
        {
            var discount = discounts?.GetValueOrDefault(period.Days) ?? period.DefaultDiscountPercent;
            if (discount is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(discounts));
            return new PaymentProductOptions
            {
                Enabled = enabled,
                Name = $"Unlimited · {period.RussianName}",
                Plan = SubscriptionPlans.Unlimited,
                DurationDays = period.Days,
                AmountMinor = Calculate(dailyAmountMinor, period.Days, discount),
                DiscountPercent = discount,
                Currency = currency,
                Description = discount == 0
                    ? "Полный каталог и API без ограничений на один день."
                    : $"Полный каталог и API без ограничений. Экономия {discount:0.#}% относительно ежедневной оплаты."
            };
        }, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Восстанавливает новую сетку из старого каталога без скачка месячной цены.</summary>
    public static Dictionary<string, PaymentProductOptions> Normalize(
        IReadOnlyDictionary<string, PaymentProductOptions> current)
    {
        if (current.Count == Periods.Count && Periods.All(period =>
            current.TryGetValue($"unlimited-{period.Code}", out var product) &&
            product.Plan == SubscriptionPlans.Unlimited && product.DurationDays == period.Days))
            return current.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        var reference = current.Values.Where(x => x.Enabled && x.AmountMinor > 0)
            .OrderByDescending(x => x.Plan == SubscriptionPlans.Unlimited)
            .ThenBy(x => Math.Abs(x.DurationDays - 30)).FirstOrDefault();
        const long defaultDailyAmountMinor = 3_700;
        var daily = reference is null ? defaultDailyAmountMinor : InferDailyPrice(reference);
        return Build(daily, reference?.Currency ?? "RUB");
    }

    /// <summary>Цена периода с округлением вверх до целой денежной единицы.</summary>
    public static long Calculate(long dailyAmountMinor, int days, decimal discountPercent)
    {
        if (dailyAmountMinor <= 0 || days <= 0 || discountPercent is < 0 or > 20) return 0;
        var raw = dailyAmountMinor * days * (100m - discountPercent) / 100m;
        return checked((long)(decimal.Ceiling(raw / 100m) * 100m));
    }

    private static long InferDailyPrice(PaymentProductOptions product)
    {
        var period = Periods.SingleOrDefault(x => x.Days == product.DurationDays);
        var discount = period?.DefaultDiscountPercent ?? Math.Clamp(product.DiscountPercent, 0m, 20m);
        var denominator = product.DurationDays * (100m - discount) / 100m;
        return denominator <= 0 ? 3_700 : Math.Max(1, (long)decimal.Ceiling(product.AmountMinor / denominator));
    }
}

/// <summary>Стабильный срок подписки и его рекомендуемая скидка.</summary>
public sealed record SubscriptionPeriod(string Code, int Days, decimal DefaultDiscountPercent, string RussianName);
