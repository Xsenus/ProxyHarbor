using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class SubscriptionPricingPolicyTests
{
    [Fact]
    public void BuildsSixMonotonicPeriodsWithTwentyPercentAnnualDiscount()
    {
        var products = SubscriptionPricingPolicy.Build(3_700, "rub").Values
            .OrderBy(product => product.DurationDays).ToArray();

        Assert.Equal([1, 7, 30, 90, 180, 365], products.Select(x => x.DurationDays));
        Assert.Equal(0m, products[0].DiscountPercent);
        Assert.Equal(20m, products[^1].DiscountPercent);
        Assert.Equal(1_080_400, products[^1].AmountMinor);
        Assert.True(products[^1].AmountMinor < 3_700 * 365);
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
        Assert.Equal(20m, normalized["unlimited-year"].DiscountPercent);
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
    [InlineData(21)]
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
    [InlineData(100, 1, 21)]
    public void CalculateReturnsZeroForInvalidArguments(long daily, int days, decimal discount)
    {
        Assert.Equal(0, SubscriptionPricingPolicy.Calculate(daily, days, discount));
    }

    [Fact]
    public void NormalizeUsesSafeDefaultsWhenNoEnabledProductExists()
    {
        var normalized = SubscriptionPricingPolicy.Normalize(new Dictionary<string, PaymentProductOptions>());

        Assert.Equal(3_700, normalized["unlimited-day"].AmountMinor);
        Assert.All(normalized.Values, product => Assert.Equal("RUB", product.Currency));
    }
}
