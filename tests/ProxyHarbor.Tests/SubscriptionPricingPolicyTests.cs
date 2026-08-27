using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class SubscriptionPricingPolicyTests
{
    [Fact]
    public void BuildsSixAffordablePeriodsWithProgressiveLongTermDiscount()
    {
        var products = SubscriptionPricingPolicy.Build(9_900, "rub").Values
            .OrderBy(product => product.DurationDays).ToArray();

        Assert.Equal([1, 7, 30, 90, 180, 365], products.Select(x => x.DurationDays));
        Assert.Equal(0m, products[0].DiscountPercent);
        Assert.Equal(83.425m, products[^1].DiscountPercent);
        Assert.Equal([9_900L, 35_000L, 69_000L, 189_000L, 339_000L, 599_000L],
            products.Select(x => x.AmountMinor));
        Assert.True(products[^1].AmountMinor < 9_900 * 365);
        Assert.True(products.Select(x => x.DiscountPercent).SequenceEqual(
            products.Select(x => x.DiscountPercent).OrderBy(x => x)));
    }

    [Fact]
    public void LegacyMonthlyPriceIsPreservedWhenCatalogIsNormalized()
    {
        var normalized = SubscriptionPricingPolicy.Normalize(new Dictionary<string, PaymentProductOptions>
        {
            ["unlimited-monthly"] = new()
            {
                Enabled = true, Name = "Unlimited", Plan = "unlimited",
                DurationDays = 30, AmountMinor = 99_900, Currency = "RUB"
            }
        });

        Assert.Equal(6, normalized.Count);
        Assert.Equal(99_900, normalized["unlimited-month"].AmountMinor);
        Assert.Equal(83.425m, normalized["unlimited-year"].DiscountPercent);
    }

    [Fact]
    public void ExistingCompleteCatalogIsReturnedWithoutRepricing()
    {
        var original = SubscriptionPricingPolicy.Build(2_500, "EUR", enabled: false);

        var normalized = SubscriptionPricingPolicy.Normalize(original);

        Assert.Equal(original.Keys.Order(), normalized.Keys.Order());
        Assert.All(normalized.Values, product => Assert.False(product.Enabled));
        Assert.Equal(original["unlimited-year"].AmountMinor, normalized["unlimited-year"].AmountMinor);
    }

    [Theory]
    [InlineData(0, "RUB")]
    [InlineData(1_000_000_001, "RUB")]
    public void BuildRejectsUnsafeDailyPrices(long dailyAmountMinor, string currency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SubscriptionPricingPolicy.Build(dailyAmountMinor, currency));
    }

    [Theory]
    [InlineData(3_700, "")]
    [InlineData(3_700, "RU")]
    [InlineData(3_700, "R1B")]
    public void BuildRejectsInvalidIsoCurrencies(long dailyAmountMinor, string currency)
    {
        Assert.Throws<ArgumentException>(() =>
            SubscriptionPricingPolicy.Build(dailyAmountMinor, currency));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void BuildRejectsDiscountsOutsideSupportedRange(int discount)
    {
        var discounts = new Dictionary<int, decimal> { [365] = discount };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SubscriptionPricingPolicy.Build(3_700, "RUB", discounts));
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(100, 0, 0)]
    [InlineData(100, 1, -1)]
    [InlineData(100, 1, 100)]
    public void CalculateReturnsZeroForInvalidArguments(long daily, int days, decimal discount)
    {
        Assert.Equal(0, SubscriptionPricingPolicy.Calculate(daily, days, discount));
    }

    [Fact]
    public void NormalizeUsesSafeDefaultsWhenNoEnabledProductExists()
    {
        var normalized = SubscriptionPricingPolicy.Normalize(new Dictionary<string, PaymentProductOptions>());

        Assert.Equal(9_900, normalized["unlimited-day"].AmountMinor);
        Assert.All(normalized.Values, product => Assert.Equal("RUB", product.Currency));
    }

    [Fact]
    public void NormalizeClampsLegacyDiscountAndInfersUnknownDuration()
    {
        var normalized = SubscriptionPricingPolicy.Normalize(new Dictionary<string, PaymentProductOptions>
        {
            ["legacy"] = new()
            {
                Enabled = true,
                Name = "Legacy",
                Plan = SubscriptionPlans.Pro,
                DurationDays = 10,
                AmountMinor = 8_000,
                DiscountPercent = 25,
                Currency = "USD"
            }
        });

        Assert.Equal(1_100, normalized["unlimited-day"].AmountMinor);
        Assert.All(normalized.Values, product => Assert.Equal("USD", product.Currency));
    }

    [Fact]
    public void NormalizeFallsBackWhenLegacyDurationCannotYieldDailyPrice()
    {
        var normalized = SubscriptionPricingPolicy.Normalize(new Dictionary<string, PaymentProductOptions>
        {
            ["invalid-legacy"] = new()
            {
                Enabled = true,
                Name = "Invalid legacy",
                Plan = SubscriptionPlans.Unlimited,
                DurationDays = 0,
                AmountMinor = 10_000,
                DiscountPercent = 20,
                Currency = "EUR"
            }
        });

        Assert.Equal(9_900, normalized["unlimited-day"].AmountMinor);
    }

    [Fact]
    public void LegacyHighPriceDefaultsAreMigratedToAffordableCatalog()
    {
        var legacy = new Dictionary<string, PaymentProductOptions>();
        var amounts = new long[] { 3_700, 24_700, 99_900, 289_800, 559_500, 1_080_400 };
        for (var index = 0; index < SubscriptionPricingPolicy.Periods.Count; index++)
        {
            var period = SubscriptionPricingPolicy.Periods[index];
            legacy[$"unlimited-{period.Code}"] = new PaymentProductOptions
            {
                Enabled = true, Name = period.RussianName, Plan = SubscriptionPlans.Unlimited,
                DurationDays = period.Days, AmountMinor = amounts[index], Currency = "RUB"
            };
        }

        var normalized = SubscriptionPricingPolicy.Normalize(legacy);

        Assert.Equal(9_900, normalized["unlimited-day"].AmountMinor);
        Assert.Equal(69_000, normalized["unlimited-month"].AmountMinor);
        Assert.Equal(599_000, normalized["unlimited-year"].AmountMinor);
    }
}
