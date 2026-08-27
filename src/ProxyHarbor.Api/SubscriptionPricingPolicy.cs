using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>
/// Единая ценовая политика сайта, платёжных шлюзов и Telegram Stars. Базой служит
/// цена одного дня, а длинные периоды получают существенно более низкую цену за день.
/// </summary>
public static class SubscriptionPricingPolicy
{
    /// <summary>Поддерживаемые сроки и скидки относительно ежедневной покупки.</summary>
    public static readonly IReadOnlyList<SubscriptionPeriod> Periods =
    [
        new("day", 1, 0m, "1 день"),
        new("week", 7, 49.5m, "1 неделя"),
        new("month", 30, 76.77m, "1 месяц"),
        new("quarter", 90, 78.79m, "3 месяца"),
        new("half-year", 180, 80.98m, "6 месяцев"),
        new("year", 365, 83.425m, "1 год")
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
            if (discount is < 0 or >= 100) throw new ArgumentOutOfRangeException(nameof(discounts));
            return new PaymentProductOptions
            {
                Enabled = enabled,
                Name = period.Code switch
                {
                    "day" => "Пробный · 1 день",
                    "week" => "Начальный · 1 неделя",
                    "month" => "Оптимальный · 1 месяц",
                    "quarter" => "Профессиональный · 3 месяца",
                    "half-year" => "Бизнес · 6 месяцев",
                    "year" => "Максимальный · 1 год",
                    _ => $"Unlimited · {period.RussianName}"
                },
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
        if (UsesLegacyHighPriceDefaults(current))
            return Build(9_900, "RUB");

        if (current.Count == Periods.Count && Periods.All(period =>
            current.TryGetValue($"unlimited-{period.Code}", out var product) &&
            product.Plan == SubscriptionPlans.Unlimited && product.DurationDays == period.Days))
            return current.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        var reference = current.Values.Where(x => x.Enabled && x.AmountMinor > 0)
            .OrderByDescending(x => x.Plan == SubscriptionPlans.Unlimited)
            .ThenBy(x => Math.Abs(x.DurationDays - 30)).FirstOrDefault();
        const long defaultDailyAmountMinor = 9_900;
        var daily = reference is null ? defaultDailyAmountMinor : InferDailyPrice(reference);
        return Build(daily, reference?.Currency ?? "RUB");
    }

    /// <summary>Цена периода с округлением вверх до целой денежной единицы.</summary>
    public static long Calculate(long dailyAmountMinor, int days, decimal discountPercent)
    {
        if (dailyAmountMinor <= 0 || days <= 0 || discountPercent is < 0 or >= 100) return 0;
        var raw = dailyAmountMinor * days * (100m - discountPercent) / 100m;
        return checked((long)(decimal.Ceiling(raw / 100m) * 100m));
    }

    private static long InferDailyPrice(PaymentProductOptions product)
    {
        var period = Periods.SingleOrDefault(x => x.Days == product.DurationDays);
        var discount = period?.DefaultDiscountPercent ?? Math.Clamp(product.DiscountPercent, 0m, 95m);
        var denominator = product.DurationDays * (100m - discount) / 100m;
        return denominator <= 0 ? 9_900 : Math.Max(1, (long)decimal.Floor(product.AmountMinor / denominator));
    }

    /// <summary>Опознаёт прежнюю стандартную сетку, чтобы безопасно заменить только системные цены.</summary>
    private static bool UsesLegacyHighPriceDefaults(IReadOnlyDictionary<string, PaymentProductOptions> current)
    {
        long[] legacyAmounts = [3_700, 24_700, 99_900, 289_800, 559_500, 1_080_400];
        var ordered = current.Values.OrderBy(x => x.DurationDays).Select(x => x.AmountMinor).ToArray();
        return ordered.SequenceEqual(legacyAmounts);
    }
}

/// <summary>Стабильный срок подписки и его рекомендуемая скидка.</summary>
public sealed record SubscriptionPeriod(string Code, int Days, decimal DefaultDiscountPercent, string RussianName);
