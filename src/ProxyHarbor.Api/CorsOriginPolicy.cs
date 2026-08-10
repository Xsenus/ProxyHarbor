namespace ProxyHarbor.Api;

/// <summary>Канонизирует bounded список browser origins и запрещает plaintext CORS в Production.</summary>
internal static class CorsOriginPolicy
{
    private const int MaximumOrigins = 32;
    private const int MaximumOriginLength = 2048;

    /// <summary>
    /// Возвращает только scheme/authority origins без credentials, path, query и fragment.
    /// HTTP разрешается исключительно вызывающим Development-конфигурациям.
    /// </summary>
    internal static bool TryNormalize(
        IEnumerable<string?>? configuredOrigins,
        bool allowHttp,
        out string[] origins)
    {
        var normalized = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredCount = 0;

        foreach (var configuredOrigin in configuredOrigins ?? [])
        {
            if (string.IsNullOrWhiteSpace(configuredOrigin)) continue;
            configuredCount++;
            if (configuredCount > MaximumOrigins || configuredOrigin.Length > MaximumOriginLength)
                return Fail(out origins);

            var candidate = configuredOrigin.Trim();
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp) ||
                !candidate.AsSpan(uri.Scheme.Length).StartsWith("://", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(uri.Host) ||
                uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.UserInfo))
                return Fail(out origins);

            var canonical = uri.GetLeftPart(UriPartial.Authority);
            if (canonical.Length > MaximumOriginLength) return Fail(out origins);
            if (unique.Add(canonical)) normalized.Add(canonical);
        }

        origins = normalized.ToArray();
        return true;
    }

    private static bool Fail(out string[] origins)
    {
        origins = [];
        return false;
    }
}
