using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Стабильные коды управляемых публичных разделов.</summary>
public static class SiteSectionCodes
{
    /// <summary>Полный поддерживаемый набор в порядке интерфейса.</summary>
    public static readonly string[] All =
    [
        "legal", "pricing", "service", "offer", "privacy",
        "personal-data-consent", "marketing-consent", "acceptable-use",
        "cookies", "refunds", "requisites"
    ];

    /// <summary>
    /// Документы, которые нельзя снять с публикации: на них ссылаются
    /// регистрация и обязательный первый выбор cookies.
    /// </summary>
    public static readonly string[] RequiredPublished =
        ["offer", "privacy", "personal-data-consent", "cookies"];
}

/// <summary>Стабильные коды редактируемых реквизитов.</summary>
public static class RequisiteFieldCodes
{
    /// <summary>Полный поддерживаемый набор в порядке публичной страницы.</summary>
    public static readonly string[] All =
    [
        "fullName", "inn", "taxStatus", "address", "email", "phone",
        "bankRecipient", "bankAccount", "bankName", "bik",
        "correspondentAccount", "bankInn", "bankKpp"
    ];

    /// <summary>Поля банковского блока.</summary>
    public static readonly HashSet<string> Bank =
    [
        "bankRecipient", "bankAccount", "bankName", "bik",
        "correspondentAccount", "bankInn", "bankKpp"
    ];
}

/// <summary>Настройки публикации одного раздела.</summary>
public sealed class SiteSectionOptions
{
    /// <summary>Доступна ли сама страница по прямому адресу.</summary>
    public bool Published { get; set; } = true;
    /// <summary>Показывать ли ссылку в публичной навигации.</summary>
    public bool ShowInNavigation { get; set; } = true;
}

/// <summary>Редактируемое значение и его видимость на странице реквизитов.</summary>
public sealed class PublicRequisiteField
{
    /// <summary>Публичное текстовое значение.</summary>
    public string Value { get; set; } = string.Empty;
    /// <summary>Выводить ли строку на странице реквизитов.</summary>
    public bool Visible { get; set; } = true;
}

/// <summary>Публичная карточка исполнителя и банковские реквизиты.</summary>
public sealed class PublicRequisitesOptions
{
    /// <summary>Заголовок карточки исполнителя.</summary>
    public string IntroTitle { get; set; } = "Исполнитель";
    /// <summary>Краткое описание статуса исполнителя.</summary>
    public string IntroDescription { get; set; } =
        "Самозанятый гражданин Российской Федерации, плательщик налога на профессиональный доход.";
    /// <summary>Показывать ли банковский блок целиком.</summary>
    public bool BankSectionVisible { get; set; }
    /// <summary>Пояснение под реквизитами.</summary>
    public string Note { get; set; } =
        "Для обращений по заказу используйте e-mail или телефон. Паспортные данные и реквизиты банковской карты клиента на сайте не публикуются и службой поддержки не запрашиваются.";
    /// <summary>Значения, индексированные стабильным кодом.</summary>
    public Dictionary<string, PublicRequisiteField> Fields { get; set; } = CreateDefaultFields();

    private static Dictionary<string, PublicRequisiteField> CreateDefaultFields() =>
        new(StringComparer.Ordinal)
        {
            ["fullName"] = Field("Телятников Илья Александрович"),
            ["inn"] = Field("890415962910"),
            ["taxStatus"] = Field("Плательщик НПД (самозанятый)"),
            // Адрес и банковские реквизиты не выводятся до явного решения администратора.
            ["address"] = Field("633209, Новосибирская область, г. Искитим, ул. Терешковой, д. 35", false),
            ["email"] = Field("ilel@list.ru"),
            ["phone"] = Field("+7 913 014-93-49"),
            ["bankRecipient"] = Field("Телятников Илья Александрович"),
            ["bankAccount"] = Field("40817810507220051060"),
            ["bankName"] = Field("АО «Альфа-Банк», г. Москва"),
            ["bik"] = Field("044525593"),
            ["correspondentAccount"] = Field("30101810200000000593"),
            ["bankInn"] = Field("7728168971"),
            ["bankKpp"] = Field("770801001")
        };

    private static PublicRequisiteField Field(string value, bool visible = true) =>
        new() { Value = value, Visible = visible };
}

/// <summary>Один поддерживаемый внешний счётчик без произвольного кода.</summary>
public sealed class ExternalAnalyticsOptions
{
    /// <summary>Разрешить загрузку после согласия пользователя.</summary>
    public bool Enabled { get; set; }
    /// <summary>Числовой или префиксный идентификатор поставщика.</summary>
    public string Identifier { get; set; } = string.Empty;
}

/// <summary>Счётчики посещаемости, доступные администратору.</summary>
public sealed class SiteAnalyticsOptions
{
    /// <summary>Собственная минимальная статистика ProxyHarbor.</summary>
    public bool FirstPartyEnabled { get; set; } = true;
    /// <summary>Яндекс Метрика.</summary>
    public ExternalAnalyticsOptions Yandex { get; set; } = new();
    /// <summary>Google Analytics 4.</summary>
    public ExternalAnalyticsOptions Google { get; set; } = new();
    /// <summary>Пиксель ретаргетинга VK.</summary>
    public ExternalAnalyticsOptions Vk { get; set; } = new();
}

/// <summary>Тексты и постоянные элементы обязательного первого выбора cookies.</summary>
public sealed class SiteCookieOptions
{
    /// <summary>Показывать постоянную кнопку повторной настройки.</summary>
    public bool ShowSettingsButton { get; set; } = true;
    /// <summary>Заголовок диалога.</summary>
    public string BannerTitle { get; set; } = "Настройки конфиденциальности";
    /// <summary>Пояснение перед выбором.</summary>
    public string BannerText { get; set; } =
        "Необходимые cookies обеспечивают вход и язык сайта. Статистика включается только с вашего разрешения.";
}

/// <summary>Полный несекретный снимок настроек публичного сайта.</summary>
public sealed class SitePublicationSettings
{
    /// <summary>Публикация и навигация по разделам.</summary>
    public Dictionary<string, SiteSectionOptions> Sections { get; set; } = CreateDefaultSections();
    /// <summary>Карточка исполнителя и банковские данные.</summary>
    public PublicRequisitesOptions Requisites { get; set; } = new();
    /// <summary>Cookie-интерфейс.</summary>
    public SiteCookieOptions Cookies { get; set; } = new();
    /// <summary>First-party и внешние счётчики.</summary>
    public SiteAnalyticsOptions Analytics { get; set; } = new();
    /// <summary>Редакция согласия; при изменении аналитики выбор запрашивается заново.</summary>
    public long CookieConsentRevision { get; set; } = 1;
    /// <summary>Последнее изменение; вычисляется из строки БД и в JSON настроек не входит.</summary>
    [JsonIgnore] public DateTimeOffset? UpdatedAt { get; set; }

    private static Dictionary<string, SiteSectionOptions> CreateDefaultSections() =>
        SiteSectionCodes.All.ToDictionary(
            code => code,
            code => new SiteSectionOptions
            {
                Published = true,
                ShowInNavigation = code is not "personal-data-consent" and not "marketing-consent"
            },
            StringComparer.Ordinal);
}

/// <summary>Читает и атомарно сохраняет актуальные настройки сайта.</summary>
public interface ISiteConfigurationStore
{
    /// <summary>Возвращает нормализованный снимок либо безопасные defaults.</summary>
    Task<SitePublicationSettings> GetAsync(CancellationToken token = default);
    /// <summary>Сохраняет полный проверенный снимок.</summary>
    Task SaveAsync(SitePublicationSettings settings, CancellationToken token = default);
}

/// <summary>PostgreSQL-хранилище singleton-настроек публичного сайта.</summary>
public sealed class SiteConfigurationStore(ProxyHarborDbContext db) : ISiteConfigurationStore
{
    private const int SingletonId = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<SitePublicationSettings> GetAsync(CancellationToken token = default)
    {
        var entity = await db.SiteConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == SingletonId, token);
        if (entity is null) return Normalize(new SitePublicationSettings());
        try
        {
            var settings = JsonSerializer.Deserialize<SitePublicationSettings>(entity.SettingsJson, Json)
                ?? throw new InvalidOperationException("Настройки сайта пусты.");
            settings = Normalize(settings);
            settings.UpdatedAt = entity.UpdatedAt;
            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Сохранённые настройки сайта повреждены.", exception);
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(SitePublicationSettings settings, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);
        var entity = await db.SiteConfigurations.SingleOrDefaultAsync(x => x.Id == SingletonId, token);
        if (entity is null)
        {
            entity = new SiteConfiguration { Id = SingletonId };
            db.SiteConfigurations.Add(entity);
        }
        entity.SettingsJson = JsonSerializer.Serialize(normalized, Json);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
    }

    private static SitePublicationSettings Normalize(SitePublicationSettings value)
    {
        value.Sections ??= [];
        foreach (var code in SiteSectionCodes.All)
            if (!value.Sections.ContainsKey(code)) value.Sections[code] = new SiteSectionOptions();
        value.Sections = SiteSectionCodes.All.ToDictionary(
            code => code,
            code => value.Sections[code] ?? new SiteSectionOptions(),
            StringComparer.Ordinal);
        foreach (var code in SiteSectionCodes.RequiredPublished)
            value.Sections[code].Published = true;

        value.Requisites ??= new PublicRequisitesOptions();
        var defaults = new PublicRequisitesOptions();
        value.Requisites.Fields ??= [];
        foreach (var code in RequisiteFieldCodes.All)
            if (!value.Requisites.Fields.ContainsKey(code))
                value.Requisites.Fields[code] = defaults.Fields[code];
        value.Requisites.Fields = RequisiteFieldCodes.All.ToDictionary(
            code => code,
            code => value.Requisites.Fields[code] ?? defaults.Fields[code],
            StringComparer.Ordinal);

        value.Cookies ??= new SiteCookieOptions();
        value.Analytics ??= new SiteAnalyticsOptions();
        value.Analytics.Yandex ??= new ExternalAnalyticsOptions();
        value.Analytics.Google ??= new ExternalAnalyticsOptions();
        value.Analytics.Vk ??= new ExternalAnalyticsOptions();
        value.CookieConsentRevision = Math.Max(1, value.CookieConsentRevision);
        return value;
    }
}
