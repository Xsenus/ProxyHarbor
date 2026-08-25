using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ProxyHarbor.Api;

/// <summary>
/// Проверяет логин и пароль администратора без раннего выхода и без хранения
/// исходных учётных данных после запуска приложения.
/// </summary>
public sealed partial class AdminCredentialValidator
{
    private readonly bool _isConfigured;
    private readonly byte[] _expectedUsernameHash = new byte[SHA256.HashSizeInBytes];
    private readonly byte[] _expectedPasswordHash = new byte[SHA256.HashSizeInBytes];

    /// <summary>Хеширует настроенные credentials один раз при создании singleton.</summary>
    public AdminCredentialValidator(IConfiguration configuration)
    {
        var username = configuration["Security:AdminUsername"];
        var password = configuration["Security:AdminPassword"];
        _isConfigured = AdminUsernamePolicy.IsValid(username) && AdminApiKeyPolicy.IsValid(password);
        if (!_isConfigured) return;

        if (!AdminApiKeyPolicy.TryHash(username!, _expectedUsernameHash) ||
            !AdminApiKeyPolicy.TryHash(password!, _expectedPasswordHash))
            throw new InvalidOperationException("Не удалось безопасно подготовить административные credentials.");
    }

    /// <summary>Constant-time сравнивает bounded логин и пароль с настройками сервера.</summary>
    public bool Validate(string? username, string? password)
    {
        Span<byte> usernameHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> passwordHash = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            var usernameHashed = username is { Length: > 0 and <= AdminUsernamePolicy.MaximumLength } &&
                AdminApiKeyPolicy.TryHash(username, usernameHash);
            var passwordHashed = password is { Length: > 0 and <= AdminApiKeyPolicy.MaximumLength } &&
                AdminApiKeyPolicy.TryHash(password, passwordHash);
            var usernameMatches = CryptographicOperations.FixedTimeEquals(_expectedUsernameHash, usernameHash);
            var passwordMatches = CryptographicOperations.FixedTimeEquals(_expectedPasswordHash, passwordHash);
            return _isConfigured && usernameHashed && passwordHashed && usernameMatches && passwordMatches;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameHash);
            CryptographicOperations.ZeroMemory(passwordHash);
        }
    }
}

/// <summary>Ограничивает административный логин простым переносимым форматом.</summary>
internal static partial class AdminUsernamePolicy
{
    internal const int MinimumLength = 3;
    internal const int MaximumLength = 64;

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidUsernameRegex();

    internal static bool IsValid(string? value) =>
        value is { Length: >= MinimumLength and <= MaximumLength } && ValidUsernameRegex().IsMatch(value);
}
