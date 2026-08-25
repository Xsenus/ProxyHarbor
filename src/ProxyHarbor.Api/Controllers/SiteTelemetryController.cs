using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Минимальная first-party телеметрия посещений без рекламных cookies и query-параметров.</summary>
[ApiController, Route("api/v1/telemetry"), EnableRateLimiting("telemetry")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SiteTelemetryController(ProxyAccessMonitor monitor) : ControllerBase
{
    /// <summary>Учитывает загрузку одной страницы; Global Privacy Control отключает запись.</summary>
    [HttpPost("visit")]
    public IActionResult Visit([FromBody] SiteVisitRequest request)
    {
        if (Request.Headers.TryGetValue("Sec-GPC", out var globalPrivacyControl) &&
            globalPrivacyControl.Count > 0 && globalPrivacyControl[0] == "1")
            return NoContent();

        monitor.RecordSiteVisit(HttpContext, NormalizePage(request.Path));
        return NoContent();
    }

    /// <summary>
    /// Преобразует URL в ограниченный стабильный код. Query, fragment и произвольные
    /// пользовательские строки никогда не попадают в БД.
    /// </summary>
    internal static string NormalizePage(string? path)
    {
        var clean = (path ?? "/").Split('?', '#')[0].Trim().TrimEnd('/').ToLowerInvariant();
        if (clean.Length == 0) clean = "/";
        return clean switch
        {
            "/" => "home",
            "/login" or "/admin/login" => "login",
            "/register" => "register",
            "/forgot-password" => "forgot-password",
            "/reset-password" => "reset-password",
            "/account" or "/account/profile" => "account",
            "/admin" => "admin-overview",
            "/admin/operations" => "admin-operations",
            "/admin/sources" => "admin-sources",
            "/admin/backups" => "admin-backups",
            "/admin/users" => "admin-users",
            "/admin/payments" => "admin-payments",
            "/admin/telegram" => "admin-telegram",
            "/admin/subscriptions" => "admin-subscriptions",
            "/admin/access" => "admin-access",
            _ => "other"
        };
    }
}

/// <summary>Путь текущей SPA-страницы без query-параметров.</summary>
public sealed class SiteVisitRequest
{
    /// <summary>Только pathname браузера; сервер дополнительно нормализует значение.</summary>
    [Required, StringLength(256)] public string Path { get; set; } = "/";
}
