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

        var provided = context.Request.Headers["X-Admin-Key"].ToString();
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        Span<byte> providedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(providedBytes, providedHash);
        CryptographicOperations.ZeroMemory(providedBytes);
        var authenticated = _isConfigured && CryptographicOperations.FixedTimeEquals(_expectedHash, providedHash);
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
