using System.Security.Claims;
using Microsoft.Net.Http.Headers;

namespace ProxyHarbor.Api;

/// <summary>
/// Принимает персональный bearer-токен до rate limiting и Authorization. Cookie имеет
/// приоритет, поэтому обычный кабинет никогда не зависит от заголовка интеграции.
/// </summary>
public sealed class UserApiTokenMiddleware(RequestDelegate next)
{
    /// <summary>Формирует principal только для корректного и действующего платного токена.</summary>
    public async Task InvokeAsync(HttpContext context, IUserApiTokenService tokens)
    {
        if (IsCatalogApi(context.Request.Path) &&
            context.User.Identity?.IsAuthenticated != true &&
            context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var values) &&
            values.Count == 1 && values[0]?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
        {
            var authentication = await tokens.AuthenticateAsync(values[0]![7..].Trim(), context.RequestAborted);
            if (authentication is not null)
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, authentication.User.Id.ToString()),
                    new(ClaimTypes.Name, authentication.User.UserName ?? authentication.User.Id.ToString()),
                    new("ProxyHarbor.ApiTokenId", authentication.TokenId.ToString())
                };
                claims.AddRange(authentication.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "UserApiToken"));
            }
        }
        await next(context);
    }

    // Bearer-токен намеренно не является универсальной cookie-сессией. Он подходит
    // только для чтения каталогов; вход в кабинет выполняется отдельным обменом
    // POST /auth/token-login, поэтому утечка интеграционного токена не даёт доступ
    // к настройкам профиля, оплате или административным операциям.
    private static bool IsCatalogApi(PathString path) =>
        path.StartsWithSegments("/api/v1/proxies") ||
        path.StartsWithSegments("/api/v1/vpn");
}
