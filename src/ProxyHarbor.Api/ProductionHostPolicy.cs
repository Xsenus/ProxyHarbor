using System.Net;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Fail-fast контракт Host Filtering для production-запусков Kestrel.</summary>
internal static class ProductionHostPolicy
{
    private const int MaximumConfigurationLength = 4096;
    private const int MaximumHosts = 32;

    /// <summary>
    /// Разрешает точные DNS/IP hosts и scoped wildcard `*.example.com`, но никогда
    /// не оставляет framework default `*`, отключающий проверку Host header.
    /// </summary>
    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumConfigurationLength ||
            value.Any(character => character is < '!' or > '~'))
            return false;

        var hosts = value.Split(';', StringSplitOptions.None);
        return hosts is { Length: >= 1 and <= MaximumHosts } && hosts.All(IsValidHostPattern);
    }

    private static bool IsValidHostPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*" ||
            !string.Equals(pattern, pattern.Trim(), StringComparison.Ordinal))
            return false;

        var wildcard = pattern.StartsWith("*.", StringComparison.Ordinal);
        var host = wildcard ? pattern[2..] : pattern;
        if (host.Length is < 1 or > 253 || host.Contains('*')) return false;

        if (host[0] == '[' && host[^1] == ']')
            return !wildcard && IPAddress.TryParse(host[1..^1], out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                !address.Equals(IPAddress.IPv6Any) &&
                string.Equals(address.ToString(), host[1..^1], StringComparison.OrdinalIgnoreCase);

        if (IPAddress.TryParse(host, out var literal))
            return !wildcard && literal.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !literal.Equals(IPAddress.Any) &&
                string.Equals(literal.ToString(), host, StringComparison.Ordinal);

        return NetworkSafety.IsCanonicalDnsName(host);
    }
}
