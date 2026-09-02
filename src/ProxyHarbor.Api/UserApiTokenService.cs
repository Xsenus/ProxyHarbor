using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Однократно показываемый результат выпуска персонального токена.</summary>
public sealed record IssuedUserApiToken(
    Guid Id,
    string Name,
    string Token,
    string DisplaySuffix,
    string[] Scopes,
    DateTimeOffset CreatedAt);

/// <summary>Проверенная учётная запись и её роли для bearer-аутентификации.</summary>
public sealed record UserApiTokenAuthentication(
    ApplicationUser User,
    IReadOnlyList<string> Roles,
    Guid TokenId);

/// <summary>Выпускает, хеширует, проверяет и отзывает пользовательские API-токены.</summary>
public interface IUserApiTokenService
{
    /// <summary>Создаёт новый токен с полным секретом только в этом ответе.</summary>
    Task<IssuedUserApiToken> IssueAsync(Guid userId, string name, CancellationToken token);
    /// <summary>Проверяет bearer-токен и наличие действующего платного доступа.</summary>
    Task<UserApiTokenAuthentication?> AuthenticateAsync(string rawToken, CancellationToken token);
    /// <summary>Возвращает безопасные метаданные без секрета.</summary>
    Task<IReadOnlyList<UserApiToken>> ListAsync(Guid userId, CancellationToken token);
    /// <summary>Необратимо отзывает токен владельца.</summary>
    Task<bool> RevokeAsync(Guid userId, Guid tokenId, CancellationToken token);
}

/// <summary>
/// Случайный секрет имеет 256 бит энтропии, поэтому SHA-256 достаточно для хранения:
/// перебор невозможен, а Data Protection и обратимое шифрование не требуются.
/// </summary>
public sealed class UserApiTokenService(
    ProxyHarborDbContext db,
    UserManager<ApplicationUser> users) : IUserApiTokenService
{
    private const string Prefix = "ph_live_";
    private const int MaximumActiveTokens = 5;

    /// <inheritdoc />
    public async Task<IssuedUserApiToken> IssueAsync(Guid userId, string name, CancellationToken token)
    {
        var account = await db.Users.Include(x => x.Subscription)
            .SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, token)
            ?? throw new InvalidOperationException("Аккаунт недоступен.");
        var roles = await users.GetRolesAsync(account);
        if (!HasPaidAccess(account.Subscription, roles, DateTimeOffset.UtcNow))
            throw new UnauthorizedAccessException("API-токен доступен только при активной подписке.");
        if (await db.UserApiTokens.CountAsync(x => x.UserId == userId && x.RevokedAt == null, token) >= MaximumActiveTokens)
            throw new InvalidOperationException($"Можно иметь не более {MaximumActiveTokens} активных токенов.");

        var secret = RandomNumberGenerator.GetBytes(32);
        var encodedSecret = WebEncoders.Base64UrlEncode(secret);
        var entity = new UserApiToken
        {
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? "Основной API-токен" : name.Trim(),
            SecretHash = SHA256.HashData(secret),
            DisplaySuffix = encodedSecret[^6..],
            Scopes = "catalog:read"
        };
        db.UserApiTokens.Add(entity);
        await db.SaveChangesAsync(token);
        return new(entity.Id, entity.Name, $"{Prefix}{entity.Id:N}.{encodedSecret}",
            entity.DisplaySuffix, ["catalog:read"], entity.CreatedAt);
    }

    /// <inheritdoc />
    public async Task<UserApiTokenAuthentication?> AuthenticateAsync(string rawToken, CancellationToken token)
    {
        if (!TryParse(rawToken, out var id, out var secret)) return null;
        var entity = await db.UserApiTokens.Include(x => x.User).ThenInclude(x => x.Subscription)
            .SingleOrDefaultAsync(x => x.Id == id && x.RevokedAt == null, token);
        if (entity?.User is not { IsActive: true } account ||
            !CryptographicOperations.FixedTimeEquals(entity.SecretHash, SHA256.HashData(secret))) return null;

        var roles = await users.GetRolesAsync(account);
        if (!HasPaidAccess(account.Subscription, roles, DateTimeOffset.UtcNow)) return null;
        if (entity.LastUsedAt is null || entity.LastUsedAt < DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            entity.LastUsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
        }
        return new(account, roles.ToArray(), entity.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserApiToken>> ListAsync(Guid userId, CancellationToken token) =>
        await db.UserApiTokens.AsNoTracking().Where(x => x.UserId == userId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(token);

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid userId, Guid tokenId, CancellationToken token)
    {
        if (db.Database.IsRelational())
        {
            var revokedAt = DateTimeOffset.UtcNow;
            return await db.UserApiTokens
                .Where(x => x.Id == tokenId && x.UserId == userId && x.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, revokedAt), token) > 0;
        }

        // InMemory provider используется unit-тестами и не поддерживает ExecuteUpdate.
        var entity = await db.UserApiTokens.SingleOrDefaultAsync(
            x => x.Id == tokenId && x.UserId == userId && x.RevokedAt == null, token);
        if (entity is null) return false;
        entity.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        return true;
    }

    /// <summary>Единая entitlement-проверка для токена, cookie и бесплатной выдачи.</summary>
    internal static bool HasPaidAccess(
        UserSubscription? subscription,
        IEnumerable<string> roles,
        DateTimeOffset now) => HasPaidAccess(
            subscription?.Plan, subscription?.Status, subscription?.ExpiresAt, roles, now);

    /// <summary>Entitlement-проверка для узких read model без загрузки всей подписки.</summary>
    internal static bool HasPaidAccess(
        string? plan,
        string? status,
        DateTimeOffset? expiresAt,
        IEnumerable<string> roles,
        DateTimeOffset now) =>
        roles.Contains(UserRoles.Administrator, StringComparer.Ordinal) ||
        status is SubscriptionStatuses.Active or SubscriptionStatuses.Trialing &&
        plan is SubscriptionPlans.Pro or SubscriptionPlans.Unlimited &&
        (expiresAt is null || expiresAt > now);

    private static bool TryParse(string value, out Guid id, out byte[] secret)
    {
        id = default;
        secret = [];
        if (!value.StartsWith(Prefix, StringComparison.Ordinal) || value.Length > 160) return false;
        var separator = value.IndexOf('.', Prefix.Length);
        if (separator < 0 || !Guid.TryParseExact(value.AsSpan(Prefix.Length, separator - Prefix.Length), "N", out id))
            return false;
        try { secret = WebEncoders.Base64UrlDecode(value[(separator + 1)..]); }
        catch (FormatException) { return false; }
        return secret.Length == 32;
    }
}
