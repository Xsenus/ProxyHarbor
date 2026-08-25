using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace ProxyHarbor.Api;

/// <summary>Защищает административные маршруты ключом из итоговой безопасной configuration.</summary>
public sealed class AdminApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _isConfigured;
    private readonly byte[] _expectedHash;

    /// <summary>Предварительно хеширует валидный configured key и сохраняет следующий middleware.</summary>
    public AdminApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        var expected = configuration["Security:AdminApiKey"];
        _isConfigured = AdminApiKeyPolicy.IsValid(expected);
        _expectedHash = new byte[SHA256.HashSizeInBytes];
        if (_isConfigured && !AdminApiKeyPolicy.TryHash(expected!, _expectedHash))
            throw new InvalidOperationException("Допустимый административный ключ не удалось закодировать.");
    }

    /// <summary>Принимает admin cookie-сессию либо constant-time проверяет X-Admin-Key для automation API.</summary>
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

        // Браузер работает через HttpOnly cookie, а CLI/автоматизация сохраняют
        // обратную совместимость с отдельным X-Admin-Key.
        if (context.User.IsInRole("Administrator"))
        {
            await _next(context);
            return;
        }

        var headerValues = context.Request.Headers["X-Admin-Key"];
        var hasSingleBoundedValue = headerValues.Count == 1 &&
            headerValues[0] is { Length: > 0 and <= 256 };
        // ToString() объединяет несколько header values запятой. Явная проверка количества
        // исключает неоднозначность для ключа, который сам содержит запятую.
        Span<byte> providedHash = stackalloc byte[SHA256.HashSizeInBytes];
        var hashCreated = hasSingleBoundedValue &&
            AdminApiKeyPolicy.TryHash(headerValues[0]!, providedHash);
        var authenticated = hashCreated && _isConfigured &&
            CryptographicOperations.FixedTimeEquals(_expectedHash, providedHash);
        CryptographicOperations.ZeroMemory(providedHash);
        if (!authenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Cookie, ApiKey realm=\"ProxyHarbor\"";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Title = "Требуется административная авторизация", Status = 401 });
            return;
        }
        await _next(context);
    }
}

/// <summary>Единая fail-closed политика production validation и middleware hashing.</summary>
internal static class AdminApiKeyPolicy
{
    internal const int MinimumLength = 24;
    internal const int MaximumLength = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsValid(string? value)
    {
        if (value is null || value.Length is < MinimumLength or > MaximumLength ||
            string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            return false;
        try
        {
            // ThrowOnInvalidBytes блокирует unpaired UTF-16 surrogate вместо замены
            // нескольких разных строк одинаковым U+FFFD перед hashing.
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Хеширует bounded строку и гарантированно очищает временные UTF-8 bytes.</summary>
    internal static bool TryHash(string value, Span<byte> destination)
    {
        if (destination.Length < SHA256.HashSizeInBytes) throw new ArgumentException("SHA-256 destination слишком мал.", nameof(destination));
        byte[]? bytes = null;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
            SHA256.HashData(bytes, destination);
            return true;
        }
        catch (EncoderFallbackException)
        {
            destination[..SHA256.HashSizeInBytes].Clear();
            return false;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
