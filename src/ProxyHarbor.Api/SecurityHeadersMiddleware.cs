namespace ProxyHarbor.Api;

/// <summary>Добавляет безопасные заголовки даже при прямом доступе к API без внешнего Nginx.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'";
        await next(context);
    }
}
