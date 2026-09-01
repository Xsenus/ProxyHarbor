using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Публичный безопасный снимок оформления сайта.</summary>
[ApiController, Route("api/v1/site-settings"), EnableRateLimiting("public")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SiteSettingsController(ISiteConfigurationStore configurations) : ControllerBase
{
    /// <summary>Возвращает только несекретные публичные настройки.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token) =>
        Ok(ToPublicResponse(await configurations.GetAsync(token)));

    internal static object ToAdminResponse(SitePublicationSettings settings) => new
    {
        settings.Sections,
        settings.Requisites,
        settings.Cookies,
        settings.Analytics,
        settings.CookieConsentRevision,
        consentRequired = true,
        settings.UpdatedAt
    };

    internal static object ToPublicResponse(SitePublicationSettings settings)
    {
        // Values that a published legal document must name remain available there.
        // Optional contact rows and every bank field are redacted when the public
        // requisites card is configured not to show them.
        var legalFields = new HashSet<string>(StringComparer.Ordinal)
            { "fullName", "inn", "address", "email" };
        var fields = settings.Requisites.Fields.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var field = pair.Value;
                var canExpose = RequisiteFieldCodes.Bank.Contains(pair.Key)
                    ? settings.Requisites.BankSectionVisible && field.Visible
                    : field.Visible || legalFields.Contains(pair.Key);
                return new PublicRequisiteField
                {
                    Value = canExpose ? field.Value : string.Empty,
                    Visible = field.Visible && canExpose
                };
            },
            StringComparer.Ordinal);
        var requisites = new PublicRequisitesOptions
        {
            IntroTitle = settings.Requisites.IntroTitle,
            IntroDescription = settings.Requisites.IntroDescription,
            BankSectionVisible = settings.Requisites.BankSectionVisible,
            Note = settings.Requisites.Note,
            Fields = fields
        };
        return new
        {
            settings.Sections,
            Requisites = requisites,
            settings.Cookies,
            settings.Analytics,
            settings.CookieConsentRevision,
            consentRequired = true,
            settings.UpdatedAt
        };
    }
}

/// <summary>Административное управление публичными разделами, cookies и аналитикой.</summary>
[ApiController, Route("api/v1/admin/site-settings"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed partial class AdminSiteSettingsController(ISiteConfigurationStore configurations) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Возвращает полный редактируемый снимок без секретов.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token) =>
        Ok(SiteSettingsController.ToAdminResponse(await configurations.GetAsync(token)));

    /// <summary>Проверяет и атомарно сохраняет полный снимок.</summary>
    [HttpPut]
    public async Task<IActionResult> Update([FromBody] SitePublicationSettings request, CancellationToken token)
    {
        if (request.Sections is null ||
            !request.Sections.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(SiteSectionCodes.All))
            return Invalid("Передайте настройки каждого поддерживаемого раздела без неизвестных кодов.");
        if (request.Sections.Values.Any(value => value is null))
            return Invalid("Настройки разделов не могут быть пустыми.");
        if (SiteSectionCodes.RequiredPublished.Any(code => !request.Sections[code].Published))
            return Invalid("Оферта, политика данных, отдельное согласие и политика cookies должны оставаться опубликованными.");

        if (request.Requisites is null || request.Requisites.Fields is null ||
            !request.Requisites.Fields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(RequisiteFieldCodes.All))
            return Invalid("Передайте полный набор реквизитов без неизвестных полей.");
        if (!ValidText(request.Requisites.IntroTitle, 120, false) ||
            !ValidText(request.Requisites.IntroDescription, 500, true) ||
            !ValidText(request.Requisites.Note, 1_000, true) ||
            request.Requisites.Fields.Values.Any(field => field is null || !ValidText(field.Value, 500, true)))
            return Invalid("Реквизиты превышают допустимую длину или содержат управляющие символы.");

        if (request.Cookies is null ||
            !ValidText(request.Cookies.BannerTitle, 120, false) ||
            !ValidText(request.Cookies.BannerText, 1_000, true))
            return Invalid("Тексты cookie-диалога некорректны.");
        if (request.Analytics is null || request.Analytics.Yandex is null ||
            request.Analytics.Google is null || request.Analytics.Vk is null)
            return Invalid("Передайте настройки всех поддерживаемых систем аналитики.");
        if (!ValidTracker(request.Analytics.Yandex, YandexIdentifier()) ||
            !ValidTracker(request.Analytics.Google, GoogleIdentifier()) ||
            !ValidTracker(request.Analytics.Vk, VkIdentifier()))
            return Invalid("Проверьте идентификаторы Яндекс Метрики, Google Analytics и VK Pixel.");

        request.Requisites.IntroTitle = request.Requisites.IntroTitle.Trim();
        request.Requisites.IntroDescription = request.Requisites.IntroDescription.Trim();
        request.Requisites.Note = request.Requisites.Note.Trim();
        foreach (var field in request.Requisites.Fields.Values) field.Value = field.Value.Trim();
        request.Cookies.BannerTitle = request.Cookies.BannerTitle.Trim();
        request.Cookies.BannerText = request.Cookies.BannerText.Trim();
        request.Analytics.Yandex.Identifier = request.Analytics.Yandex.Identifier.Trim();
        request.Analytics.Google.Identifier = request.Analytics.Google.Identifier.Trim().ToUpperInvariant();
        request.Analytics.Vk.Identifier = request.Analytics.Vk.Identifier.Trim();

        var current = await configurations.GetAsync(token);
        var previousConsentContract = JsonSerializer.Serialize(new { current.Cookies, current.Analytics }, Json);
        var nextConsentContract = JsonSerializer.Serialize(new { request.Cookies, request.Analytics }, Json);
        request.CookieConsentRevision = string.Equals(
            previousConsentContract, nextConsentContract, StringComparison.Ordinal)
            ? current.CookieConsentRevision
            : checked(current.CookieConsentRevision + 1);
        request.UpdatedAt = null;

        await configurations.SaveAsync(request, token);
        return Ok(SiteSettingsController.ToAdminResponse(await configurations.GetAsync(token)));
    }

    private static bool ValidTracker(ExternalAnalyticsOptions value, Regex expression)
    {
        var identifier = value.Identifier.Trim();
        return (!value.Enabled || identifier.Length > 0) &&
            (identifier.Length == 0 || identifier.Length <= 128 && expression.IsMatch(identifier));
    }

    private static bool ValidText(string? value, int maximum, bool allowNewLines)
    {
        if (value is null || value.Length > maximum) return false;
        return value.All(character => !char.IsControl(character) ||
            allowNewLines && character is '\r' or '\n' or '\t');
    }

    private static BadRequestObjectResult Invalid(string title) => new(new ProblemDetails
    {
        Title = title,
        Status = StatusCodes.Status400BadRequest
    });

    [GeneratedRegex("^[1-9][0-9]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex YandexIdentifier();

    [GeneratedRegex("^G-[A-Z0-9]{4,32}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GoogleIdentifier();

    [GeneratedRegex("^(?:VK-RTRG-[A-Za-z0-9_-]{3,100}|[1-9][0-9]{0,19})$", RegexOptions.CultureInvariant)]
    private static partial Regex VkIdentifier();
}
