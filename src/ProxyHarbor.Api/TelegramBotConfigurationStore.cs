using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Deploy-настройки Telegram, которые нельзя перенаправить из админки.</summary>
public sealed class TelegramBotHostOptions
{
    /// <summary>Имя configuration section.</summary>
    public const string Section = "TelegramBot";
    /// <summary>Публичный HTTPS origin webhook.</summary>
    public string PublicBaseUrl { get; set; } = "https://proxy.blagodaty.ru";
}

/// <summary>Эффективная runtime-конфигурация торгового бота.</summary>
public sealed class TelegramBotOptions
{
    /// <summary>Разрешена ли обработка update и отправка очереди.</summary>
    public bool Enabled { get; set; }
    /// <summary>webhook или polling.</summary>
    public string UpdateMode { get; set; } = TelegramUpdateModes.Webhook;
    /// <summary>Доверенный deploy-origin.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;
    /// <summary>Имя профиля бота.</summary>
    public string Name { get; set; } = "ProxyHarbor";
    /// <summary>Полное описание пустого чата.</summary>
    public string Description { get; set; } = "Проверенные прокси, подписка и личный кабинет ProxyHarbor.";
    /// <summary>Краткое описание профиля.</summary>
    public string ShortDescription { get; set; } = "Покупка подписки и получение проверенных прокси.";
    /// <summary>Текст передачи вопроса в поддержку.</summary>
    public string SupportText { get; set; } = "Напишите вопрос — оператор увидит его в панели управления.";
    /// <summary>Максимум строк в одном proxy-файле.</summary>
    public int ProxyFileMaxItems { get; set; } = 1000;
    /// <summary>Число параллельных webhook соединений Telegram.</summary>
    public int WebhookMaxConnections { get; set; } = 20;
    /// <summary>Цена каждого кода продукта в Stars.</summary>
    public Dictionary<string, int> ProductStars { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Коды продуктов, цена которых рассчитывается от основного каталога.</summary>
    public HashSet<string> AutomaticProductCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Ориентировочная розничная стоимость одной Star в рублях. Telegram может
    /// показывать пользователям другой курс из-за платформы, налогов и региона.
    /// </summary>
    public decimal RublesPerStar { get; set; } = TelegramStarsPricing.DefaultRublesPerStar;
    /// <summary>Шаг округления автоматической цены вверх.</summary>
    public int StarsRoundingStep { get; set; } = 5;
    /// <summary>auto пробует SOCKS5 по очереди и затем прямое соединение; proxy запрещает direct fallback.</summary>
    public string TransportMode { get; set; } = TelegramTransportModes.Auto;
    /// <summary>Защищённый список SOCKS5 upstream с авторизацией и failover.</summary>
    public List<TelegramProxyOptions> Proxies { get; set; } = [];
    /// <summary>Секретный bot token; доступен только серверу.</summary>
    public string BotToken { get; set; } = string.Empty;
    /// <summary>Секрет проверки webhook header.</summary>
    public string WebhookSecret { get; set; } = string.Empty;
    /// <summary>Проверенный Telegram bot id.</summary>
    public long? BotId { get; set; }
    /// <summary>Проверенный username.</summary>
    public string? BotUsername { get; set; }
    /// <summary>Последний успешный provisioning.</summary>
    public DateTimeOffset? ProvisionedAt { get; set; }
    /// <summary>Последнее сохранение.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Полный URL уникального webhook endpoint.</summary>
    public string WebhookUrl => string.IsNullOrWhiteSpace(BotUsername)
        ? string.Empty
        : $"{PublicBaseUrl.TrimEnd('/')}/api/v1/telegram/webhook/{Uri.EscapeDataString(BotUsername.ToLowerInvariant())}";

    /// <summary>Готов ли runtime принимать update.</summary>
    public bool Ready => Enabled && BotId.HasValue && BotToken.Length > 0 && WebhookSecret.Length > 0;
}

/// <summary>Защищённые параметры одного SOCKS5-маршрута к Telegram API.</summary>
public sealed class TelegramProxyOptions
{
    /// <summary>Стабильный идентификатор для безопасного обновления пароля.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Имя хоста или IP SOCKS5-сервера.</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>TCP-порт SOCKS5-сервера.</summary>
    public int Port { get; set; } = 1080;
    /// <summary>Логин SOCKS5-сервера.</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>Пароль SOCKS5-сервера; хранится только в защищённой конфигурации.</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>Политики выбора маршрута к Telegram API.</summary>
public static class TelegramTransportModes
{
    /// <summary>Сначала прокси, затем прямой резервный маршрут.</summary>
    public const string Auto = "auto";
    /// <summary>Только настроенные SOCKS5-прокси.</summary>
    public const string Proxy = "proxy";
    /// <summary>Только прямое соединение.</summary>
    public const string Direct = "direct";
    /// <summary>Все поддерживаемые политики.</summary>
    public static readonly string[] All = [Auto, Proxy, Direct];
}

/// <summary>Допустимые взаимоисключающие способы получения update.</summary>
public static class TelegramUpdateModes
{
    /// <summary>HTTPS webhook.</summary>
    public const string Webhook = "webhook";
    /// <summary>Long polling.</summary>
    public const string Polling = "polling";
    /// <summary>Полный допустимый набор.</summary>
    public static readonly string[] All = [Webhook, Polling];
}

/// <summary>Хранилище конфигурации с Data Protection шифрованием token.</summary>
public interface ITelegramBotConfigurationStore
{
    /// <summary>Читает и расшифровывает эффективный снимок.</summary>
    Task<TelegramBotOptions> GetAsync(CancellationToken token = default);
    /// <summary>Сохраняет полный проверенный снимок.</summary>
    Task SaveAsync(TelegramBotOptions options, CancellationToken token = default);
}

/// <summary>
/// Публичные параметры сохраняются JSONB, а token и webhook secret никогда не
/// возвращаются в административный браузер и лежат только в ciphertext.
/// </summary>
public sealed class TelegramBotConfigurationStore(
    ProxyHarborDbContext db,
    IOptions<TelegramBotHostOptions> host,
    IDataProtectionProvider protectionProvider) : ITelegramBotConfigurationStore
{
    private const int SingletonId = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector = protectionProvider.CreateProtector(
        "ProxyHarbor.TelegramCommerce.Secrets.v1");

    /// <inheritdoc />
    public async Task<TelegramBotOptions> GetAsync(CancellationToken token = default)
    {
        var entity = await db.TelegramBotConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == SingletonId, token);
        if (entity is null) return new TelegramBotOptions { PublicBaseUrl = host.Value.PublicBaseUrl };
        try
        {
            var settings = JsonSerializer.Deserialize<StoredSettings>(entity.SettingsJson, Json)
                ?? throw new InvalidOperationException("Настройки Telegram-бота пусты.");
            var secrets = JsonSerializer.Deserialize<StoredSecrets>(
                protector.Unprotect(entity.ProtectedSecrets), Json)
                ?? throw new InvalidOperationException("Секреты Telegram-бота пусты.");
            return new TelegramBotOptions
            {
                Enabled = settings.Enabled,
                UpdateMode = settings.UpdateMode,
                PublicBaseUrl = host.Value.PublicBaseUrl,
                Name = settings.Name,
                Description = settings.Description,
                ShortDescription = settings.ShortDescription,
                SupportText = settings.SupportText,
                ProxyFileMaxItems = settings.ProxyFileMaxItems,
                WebhookMaxConnections = settings.WebhookMaxConnections,
                ProductStars = new Dictionary<string, int>(settings.ProductStars, StringComparer.OrdinalIgnoreCase),
                AutomaticProductCodes = new HashSet<string>(settings.AutomaticProductCodes ?? [], StringComparer.OrdinalIgnoreCase),
                // Старые снимки хранили обратный коэффициент Stars/₽, который давал
                // завышенную цену 1:1. При первом чтении переводим их на новый
                // понятный ориентир ₽/Star без изменения структуры таблицы.
                RublesPerStar = settings.RublesPerStar is > 0
                    ? settings.RublesPerStar.Value
                    : TelegramStarsPricing.DefaultRublesPerStar,
                StarsRoundingStep = settings.StarsRoundingStep <= 0 ? 5 : settings.StarsRoundingStep,
                TransportMode = TelegramTransportModes.All.Contains(settings.TransportMode, StringComparer.Ordinal)
                    ? settings.TransportMode : TelegramTransportModes.Auto,
                Proxies = secrets.Proxies ?? [],
                BotToken = secrets.BotToken,
                WebhookSecret = secrets.WebhookSecret,
                BotId = entity.BotId,
                BotUsername = entity.BotUsername,
                ProvisionedAt = entity.ProvisionedAt,
                UpdatedAt = entity.UpdatedAt
            };
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException)
        {
            throw new InvalidOperationException(
                "Сохранённые настройки Telegram-бота повреждены или больше не расшифровываются.", exception);
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(TelegramBotOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var entity = await db.TelegramBotConfigurations.SingleOrDefaultAsync(x => x.Id == SingletonId, token);
        if (entity is null)
        {
            entity = new TelegramBotConfiguration { Id = SingletonId };
            db.TelegramBotConfigurations.Add(entity);
        }
        entity.SettingsJson = JsonSerializer.Serialize(new StoredSettings(
            options.Enabled, options.UpdateMode, options.Name, options.Description,
            options.ShortDescription, options.SupportText, options.ProxyFileMaxItems,
            options.WebhookMaxConnections, options.ProductStars, options.AutomaticProductCodes,
            options.RublesPerStar, options.StarsRoundingStep, options.TransportMode), Json);
        entity.ProtectedSecrets = protector.Protect(JsonSerializer.Serialize(
            new StoredSecrets(options.BotToken, options.WebhookSecret, options.Proxies), Json));
        entity.BotId = options.BotId;
        entity.BotUsername = options.BotUsername;
        entity.ProvisionedAt = options.ProvisionedAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
    }

    private sealed record StoredSettings(
        bool Enabled,
        string UpdateMode,
        string Name,
        string Description,
        string ShortDescription,
        string SupportText,
        int ProxyFileMaxItems,
        int WebhookMaxConnections,
        Dictionary<string, int> ProductStars,
        HashSet<string>? AutomaticProductCodes = null,
        decimal? RublesPerStar = null,
        int StarsRoundingStep = 5,
        string TransportMode = TelegramTransportModes.Auto);
    private sealed record StoredSecrets(string BotToken, string WebhookSecret, List<TelegramProxyOptions>? Proxies = null);
}

/// <summary>Единая формула цены Stars для админки и runtime торгового бота.</summary>
public static class TelegramStarsPricing
{
    /// <summary>
    /// Базовый ориентир по крупному пользовательскому пакету: около 1,68 ₽ за Star.
    /// Администратор может менять его без выпуска новой версии приложения.
    /// </summary>
    public const decimal DefaultRublesPerStar = 1.68m;

    /// <summary>Возвращает ручную цену либо автоматически рассчитывает её из цены подписки.</summary>
    public static bool TryResolve(
        TelegramBotOptions options,
        string productCode,
        PaymentProductOptions product,
        out int stars)
    {
        if (!options.AutomaticProductCodes.Contains(productCode))
            return options.ProductStars.TryGetValue(productCode, out stars) && stars is >= 1 and <= 1_000_000;

        stars = Calculate(product.AmountMinor, options.RublesPerStar, options.StarsRoundingStep);
        return stars is >= 1 and <= 1_000_000;
    }

    /// <summary>
    /// Переводит цену из минимальных денежных единиц в Stars и округляет вверх,
    /// чтобы округление никогда не уменьшало заданную владельцем стоимость.
    /// </summary>
    public static int Calculate(long amountMinor, decimal rublesPerStar, int roundingStep)
    {
        if (amountMinor <= 0 || rublesPerStar <= 0 || roundingStep <= 0) return 0;
        var raw = amountMinor / 100m / rublesPerStar;
        var rounded = decimal.Ceiling(raw / roundingStep) * roundingStep;
        return rounded > 1_000_000m ? 0 : decimal.ToInt32(rounded);
    }
}
