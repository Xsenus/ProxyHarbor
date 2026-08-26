using System.Net;
using System.Net.Sockets;

namespace ProxyHarbor.Api;

/// <summary>Ограничивает сети, которым разрешено управлять X-Forwarded-For/Proto.</summary>
internal static class ForwardedNetworkPolicy
{
    private const int MaximumNetworks = 32;
    private const int MaximumCidrLength = 64;
    private const int MinimumIpv4PrefixLength = 8;
    private const int MinimumIpv6PrefixLength = 24;

    /// <summary>
    /// Принимает только канонические bounded CIDR. Минимальные prefix lengths оставляют
    /// совместимость с крупными CDN ranges, но исключают catch-all и чрезмерно широкое доверие.
    /// </summary>
    internal static bool TryParse(IEnumerable<string?>? configuredNetworks, out IPNetwork[] networks)
    {
        var parsed = new List<IPNetwork>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredCount = 0;

        foreach (var configuredNetwork in configuredNetworks ?? [])
        {
            configuredCount++;
            if (configuredCount > MaximumNetworks || string.IsNullOrEmpty(configuredNetwork) ||
                configuredNetwork.Length > MaximumCidrLength ||
                !string.Equals(configuredNetwork, configuredNetwork.Trim(), StringComparison.Ordinal) ||
                !configuredNetwork.Contains('/') ||
                !IPNetwork.TryParse(configuredNetwork, out var network))
                return Fail(out networks);

            var minimumPrefixLength = network.BaseAddress.AddressFamily switch
            {
                AddressFamily.InterNetwork => MinimumIpv4PrefixLength,
                AddressFamily.InterNetworkV6 => MinimumIpv6PrefixLength,
                _ => int.MaxValue
            };
            var canonical = network.ToString();
            if (network.PrefixLength < minimumPrefixLength ||
                !string.Equals(configuredNetwork, canonical, StringComparison.OrdinalIgnoreCase))
                return Fail(out networks);

            if (unique.Add(canonical)) parsed.Add(network);
        }

        networks = parsed.ToArray();
        return true;
    }

    /// <summary>
    /// ASP.NET Core может получить адрес Docker-шлюза как IPv4-mapped IPv6. Для каждой
    /// явно доверенной IPv4-сети добавляем строго эквивалентную mapped-сеть, не расширяя
    /// заданную администратором границу доверия.
    /// </summary>
    internal static IEnumerable<IPNetwork> ExpandForRuntime(IEnumerable<IPNetwork> networks)
    {
        foreach (var network in networks)
        {
            yield return network;
            if (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
                yield return new IPNetwork(network.BaseAddress.MapToIPv6(), network.PrefixLength + 96);
        }
    }

    private static bool Fail(out IPNetwork[] networks)
    {
        networks = [];
        return false;
    }
}
