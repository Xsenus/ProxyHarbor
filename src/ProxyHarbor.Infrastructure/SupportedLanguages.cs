namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Единый реестр языков, доступных сайту, API, письмам и Telegram-боту.
/// Стабильные двухбуквенные коды хранятся в БД, поэтому новый перевод можно
/// добавить без изменения схемы данных.
/// </summary>
public static class SupportedLanguages
{
    /// <summary>Русский.</summary>
    public const string Russian = "ru";
    /// <summary>Английский.</summary>
    public const string English = "en";
    /// <summary>Немецкий.</summary>
    public const string German = "de";
    /// <summary>Французский.</summary>
    public const string French = "fr";
    /// <summary>Упрощённый китайский.</summary>
    public const string Chinese = "zh";

    /// <summary>Язык по умолчанию и безопасный fallback для неизвестных кодов.</summary>
    public const string Default = Russian;

    /// <summary>Публичный упорядоченный список для UI и валидации.</summary>
    public static readonly string[] All = [Russian, English, German, French, Chinese];
    private static readonly Dictionary<string, string> Cultures = new(StringComparer.Ordinal)
    {
        [Russian] = "ru-RU",
        [English] = "en-US",
        [German] = "de-DE",
        [French] = "fr-FR",
        [Chinese] = "zh-CN"
    };

    /// <summary>
    /// Приводит browser/Telegram коды вроде en-US, de_DE и zh-CN к языку продукта.
    /// Традиционный китайский пока использует общий перевод zh.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;
        var code = value.Trim().Replace('_', '-').Split('-', 2)[0].ToLowerInvariant();
        return All.Contains(code, StringComparer.Ordinal) ? code : Default;
    }

    /// <summary>Проверяет именно сохраняемый двухбуквенный код без неявного fallback.</summary>
    public static bool IsSupported(string? value) =>
        !string.IsNullOrWhiteSpace(value) && All.Contains(value.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Возвращает конкретную culture для форматирования дат, чисел и API-ответов.</summary>
    public static string CultureName(string? value) => Cultures[Normalize(value)];
}
