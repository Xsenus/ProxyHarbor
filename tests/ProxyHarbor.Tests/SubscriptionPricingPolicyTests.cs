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
}
