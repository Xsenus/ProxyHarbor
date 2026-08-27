using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class LocalizationTests
{
    private static readonly string[] QualityTierMarkers =
        ["среднего качества", "medium-quality", "mittlerer Qualität", "qualité moyenne", "中等质量"];

    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("de_DE", "de")]
    [InlineData("fr", "fr")]
    [InlineData("zh-CN", "zh")]
    [InlineData("es", "ru")]
    [InlineData("   ", "ru")]
    [InlineData(null, "ru")]
    public void NormalizesExternalLanguageCodes(string? value, string expected) =>
        Assert.Equal(expected, SupportedLanguages.Normalize(value));

    [Theory]
    [InlineData("ru", "Личный кабинет")]
    [InlineData("en", "Account")]
    [InlineData("de", "Konto")]
    [InlineData("fr", "Compte")]
    [InlineData("zh", "个人中心")]
    public void TelegramCatalogContainsEachSupportedLanguage(string language, string expected) =>
        Assert.Contains(expected, TelegramLocalization.Get("accountButton", language), StringComparison.Ordinal);

    [Fact]
    public void TelegramCatalogReplacesVariablesAndFallsBackToRussian()
    {
        Assert.Equal("Сообщение передано оператору. help@example.test",
            TelegramLocalization.Get("supportForwarded", "unknown", ("support", "help@example.test")));
        Assert.Equal("missing", TelegramLocalization.Get("missing", "en"));
    }

    [Theory]
    [InlineData("ru", true)]
    [InlineData("en", true)]
    [InlineData("EN", true)]
    [InlineData("en-US", false)]
    [InlineData("es", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void ValidatesPersistedLanguageCodes(string value, bool expected) =>
        Assert.Equal(expected, SupportedLanguages.IsSupported(value));

    [Theory]
    [InlineData("ru", "ru-RU")]
    [InlineData("en", "en-US")]
    [InlineData("de", "de-DE")]
    [InlineData("fr", "fr-FR")]
    [InlineData("zh", "zh-CN")]
    [InlineData("unknown", "ru-RU")]
    public void ResolvesConcreteCultureNames(string language, string expected) =>
        Assert.Equal(expected, SupportedLanguages.CultureName(language));

    [Theory]
    [InlineData("ru", "Бесплатный доступ")]
    [InlineData("en", "Free access")]
    [InlineData("de", "Kostenloser Zugang")]
    [InlineData("fr", "Accès gratuit")]
    [InlineData("zh", "免费访问")]
    public void LocalizesFreeExportUpgradeMessage(string language, string expected) =>
        Assert.StartsWith(expected, FreeExportAccessService.GetUpgradeMessage(language), StringComparison.Ordinal);

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("zh")]
    public void FreeAccessMessagesDoNotDescribeQualityTier(string language)
    {
        var messages = new[]
        {
            FreeExportAccessService.GetUpgradeMessage(language),
            FreeExportAccessService.GetProxyCatalogUpgradeMessage(language, 150),
            FreeExportAccessService.GetVpnUpgradeMessage(language, 350)
        };

        Assert.All(messages, message => Assert.DoesNotContain(
            QualityTierMarkers,
            marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }
}
