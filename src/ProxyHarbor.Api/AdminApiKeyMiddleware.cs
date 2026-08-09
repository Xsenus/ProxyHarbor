using Microsoft.AspNetCore.Mvc;

namespace ProxyHarbor.Api;

/// <summary>Защищает административные маршруты ключом из переменной окружения.</summary>
public sealed class AdminApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/admin"))
        {
            await next(context);
            return;
        }

        var expected = configuration["Security:AdminApiKey"];
        var provided = context.Request.Headers["X-Admin-Key"].ToString();
        if (string.IsNullOrWhiteSpace(expected) ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(provided)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Title = "Требуется административный ключ", Status = 401 });
            return;
        }
        await next(context);
    }
}
