using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace ProxyHarbor.Api;

/// <summary>Защищает административные маршруты ключом из переменной окружения.</summary>
public sealed class AdminApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _isConfigured;
    private readonly byte[] _expectedHash;

    public AdminApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        var expected = configuration["Security:AdminApiKey"];
        _isConfigured = !string.IsNullOrWhiteSpace(expected);
        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected ?? string.Empty));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/admin"))
        {
            await _next(context);
            return;
        }

        // Admin-ответы и результаты операций никогда не должны сохраняться browser/shared cache.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";

        var headerValues = context.Request.Headers["X-Admin-Key"];
        var hasSingleBoundedValue = headerValues.Count == 1 &&
            headerValues[0] is { Length: > 0 and <= 256 };
        // ToString() объединяет несколько header values запятой. Явная проверка количества
        // исключает неоднозначность для ключа, который сам содержит запятую.
        var provided = hasSingleBoundedValue ? headerValues[0]! : string.Empty;
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        Span<byte> providedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(providedBytes, providedHash);
        CryptographicOperations.ZeroMemory(providedBytes);
        var authenticated = hasSingleBoundedValue && _isConfigured &&
            CryptographicOperations.FixedTimeEquals(_expectedHash, providedHash);
        CryptographicOperations.ZeroMemory(providedHash);
        if (!authenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "ApiKey realm=\"ProxyHarbor\"";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Title = "Требуется административный ключ", Status = 401 });
            return;
        }
        await _next(context);
    }
}
