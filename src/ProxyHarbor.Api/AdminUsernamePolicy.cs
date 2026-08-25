using System.Text.RegularExpressions;

namespace ProxyHarbor.Api;

/// <summary>Ограничивает bootstrap-логин администратора простым переносимым форматом.</summary>
internal static partial class AdminUsernamePolicy
{
    internal const int MinimumLength = 3;
    internal const int MaximumLength = 64;

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidUsernameRegex();

    internal static bool IsValid(string? value) =>
        value is { Length: >= MinimumLength and <= MaximumLength } && ValidUsernameRegex().IsMatch(value);
}
