using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует финансово значимое правило расчёта Telegram Stars.</summary>
public sealed class TelegramStarsPricingTests
{
    [Fact]
    public void AutomaticPriceUsesCatalogAmountAndAlwaysRoundsUp()
    {
        var options = new TelegramBotOptions
        {
            AutomaticProductCodes = new(StringComparer.OrdinalIgnoreCase) { "pro-30" },
            RublesPerStar = 1.68m,
            StarsRoundingStep = 5
        };
        var product = new PaymentProductOptions { AmountMinor = 49_901, Currency = "RUB" };

        Assert.True(TelegramStarsPricing.TryResolve(options, "PRO-30", product, out var stars));
        Assert.Equal(300, stars);
    }

    [Fact]
    public void ManualPriceIsPreservedWhenAutomaticModeIsOff()
    {
        var options = new TelegramBotOptions
        {
            ProductStars = new(StringComparer.OrdinalIgnoreCase) { ["pro-30"] = 249 },
            RublesPerStar = 5m,
            StarsRoundingStep = 100
        };

        Assert.True(TelegramStarsPricing.TryResolve(options, "pro-30",
            new PaymentProductOptions { AmountMinor = 999_900 }, out var stars));
        Assert.Equal(249, stars);
    }

    [Theory]
    [InlineData(9_900, 60)]
    [InlineData(35_000, 210)]
    [InlineData(69_000, 415)]
    [InlineData(189_000, 1_125)]
    public void AutomaticPriceTracksRublesUsingConfiguredRetailReference(long amountMinor, int expectedStars)
    {
        Assert.Equal(expectedStars, TelegramStarsPricing.Calculate(amountMinor, 1.68m, 5));
    }

    [Theory]
    [InlineData(0, 1, 5)]
    [InlineData(10_000, 0, 5)]
    [InlineData(10_000, 1, 0)]
    public void InvalidFormulaNeverProducesAnInvoicePrice(long amountMinor, double rate, int step)
    {
        Assert.Equal(0, TelegramStarsPricing.Calculate(amountMinor, (decimal)rate, step));
    }
}
