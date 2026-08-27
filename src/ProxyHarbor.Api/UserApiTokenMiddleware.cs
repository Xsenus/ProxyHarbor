using System.Diagnostics;
using System.Security.Claims;
using Microsoft.Net.Http.Headers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>
/// Принимает персональный bearer-токен до rate limiting и Authorization. Cookie имеет
/// приоритет, поэтому обычный кабинет никогда не зависит от заголовка интеграции.
/// </summary>
public sealed class UserApiTokenMiddleware(RequestDelegate next)
{
    private static readonly Action<ILogger, Exception?> AuditFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1601, "ApiTokenAuditFailed"),
        "Не удалось сохранить аудит запроса пользовательского API-токена.");

    /// <summary>Формирует principal только для корректного и действующего платного токена.</summary>
    public async Task InvokeAsync(HttpContext context, IUserApiTokenService tokens, ProxyHarborDbContext db,
        ILogger<UserApiTokenMiddleware> logger)
    {
        UserApiTokenAuthentication? authentication = null;
        if (IsCatalogApi(context.Request.Path) &&
            context.User.Identity?.IsAuthenticated != true &&
            context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var values) &&
            values.Count == 1 && values[0]?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
        {
            authentication = await tokens.AuthenticateAsync(values[0]![7..].Trim(), context.RequestAborted);
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
        if (authentication is null)
        {
            await next(context);
            return;
        }

        var requestedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        Exception? requestFailure = null;
        try { await next(context); }
        catch (Exception exception)
        {
            requestFailure = exception;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            // Запись аудита не должна превращать успешно выполненную выдачу в ошибку.
            // При удалённом одновременно токене FK/конкурентный конфликт безопасно игнорируется.
            try
            {
                db.UserApiTokenRequests.Add(new UserApiTokenRequest
                {
                    UserApiTokenId = authentication.TokenId,
                    UserId = authentication.User.Id,
                    IpAddress = ProxyAccessMonitor.NormalizeAddress(context.Connection.RemoteIpAddress),
                    Method = context.Request.Method[..Math.Min(context.Request.Method.Length, 10)],
                    Path = context.Request.Path.Value?[..Math.Min(context.Request.Path.Value.Length, 500)] ?? "/",
                    Query = SafeQuery(context.Request.Query),
                    StatusCode = requestFailure is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError,
                    ItemCount = context.Items.TryGetValue("ProxyHarbor.ProxyItems", out var value) && value is int count ? count : null,
                    DurationMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                    RequestedAt = requestedAt
                });
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                AuditFailed(logger, exception);
                db.ChangeTracker.Clear();
            }
        }
    }

    // Bearer-токен намеренно не является универсальной cookie-сессией. Он подходит
    // только для чтения каталогов; вход в кабинет выполняется отдельным обменом
    // POST /auth/token-login, поэтому утечка интеграционного токена не даёт доступ
    // к настройкам профиля, оплате или административным операциям.
    private static bool IsCatalogApi(PathString path) =>
        path.StartsWithSegments("/api/v1/proxies") ||
        path.StartsWithSegments("/api/v1/vpn");

    private static string? SafeQuery(IQueryCollection query)
    {
        if (query.Count == 0) return null;
        var blocked = new[] { "token", "key", "password", "secret", "signature", "authorization" };
        var pairs = query.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
        {
            var sensitive = blocked.Any(word => pair.Key.Contains(word, StringComparison.OrdinalIgnoreCase));
            return $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(sensitive ? "[hidden]" : pair.Value.ToString())}";
        });
        var result = string.Join('&', pairs);
        return result[..Math.Min(result.Length, 1000)];
    }
}
