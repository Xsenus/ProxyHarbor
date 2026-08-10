namespace ProxyHarbor.Api;

/// <summary>Добавляет безопасные заголовки даже при прямом доступе к API без внешнего Nginx.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>Добавляет browser hardening headers до выполнения следующего middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
        // Browser учитывает HSTS только для HTTPS-ответа; на локальном HTTP заголовок безопасно игнорируется.
        context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'";
        await next(context);
    }
}
