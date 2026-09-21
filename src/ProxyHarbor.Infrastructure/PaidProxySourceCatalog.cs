using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Канонические параметры поддерживаемых платных proxy provider.</summary>
public static class PaidProxySourceCatalog
{
    /// <summary>Безопасный базовый URL без API key.</summary>
    public const string BestProxiesUrl = "https://api.best-proxies.ru/proxylist.txt";
    /// <summary>Операторское имя встроенного платного источника.</summary>
    public const string BestProxiesName = "Best Proxies Premium";
    /// <summary>Порядок provider перед бесплатным каталогом.</summary>
    public const int BestProxiesPriority = -1_000;
    /// <summary>Служебное значение очереди для немедленной проверки платного кандидата.</summary>
    public static readonly DateTimeOffset ImmediateValidationMarker = DateTimeOffset.UnixEpoch;

    /// <summary>Определяет поддерживаемый платный источник по каноническому URL.</summary>
    public static bool IsPaid(ProxySource source) => IsPaidUrl(source.Url);
    /// <summary>Определяет поддерживаемый платный источник по каноническому URL.</summary>
    public static bool IsPaidUrl(string url) =>
        string.Equals(url, BestProxiesUrl, StringComparison.Ordinal);

    internal static string BuildListUrl(string apiKey) =>
        $"{BestProxiesUrl}?key={Uri.EscapeDataString(apiKey)}&includeType&limit=0";

    internal static string BuildStatusUrl(string apiKey) =>
        $"https://api.best-proxies.ru/key.txt?key={Uri.EscapeDataString(apiKey)}&format=seconds";

    internal static DateTimeOffset ParseExpiration(string content, DateTimeOffset checkedAt)
    {
        if (!long.TryParse(content.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or > 31_536_000)
            throw new InvalidDataException("Best Proxies вернул некорректный срок действия ключа.");
        return checkedAt.AddSeconds(seconds);
    }
}

/// <summary>Единый purpose для шифрования ключей платных proxy provider.</summary>
public static class ProxySourceCredentialProtection
{
    private const string Purpose = "ProxyHarbor.ProxySourceCredentials.ApiKey.v1";
    /// <summary>Создаёт protector с фиксированным versioned purpose.</summary>
    public static IDataProtector Create(IDataProtectionProvider provider) =>
        provider.CreateProtector(Purpose);
}

/// <summary>Ограничивает формат ключа до безопасного printable ASCII token.</summary>
public static class ProxySourceApiKeyPolicy
{
    /// <summary>Проверяет bounded token до шифрования и передачи provider.</summary>
    public static bool IsValid(string? value) =>
        value is { Length: >= 16 and <= 256 } &&
        value.All(character => character is >= '!' and <= '~' &&
            character is not ('&' or '?' or '#' or '/' or '\\'));
}
